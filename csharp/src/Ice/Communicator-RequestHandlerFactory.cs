//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace ZeroC.Ice
{
    public sealed partial class Communicator
    {
        private readonly Dictionary<Reference, ConnectAwaitable> _pendingConnects =
            new Dictionary<Reference, ConnectAwaitable>();

        internal void ClearRequestHandler(Reference reference, IRequestHandler handler)
        {
            if (reference.IsConnectionCached)
            {
                lock (_pendingConnects)
                {
                    if (reference.RequestHandler == handler)
                    {
                        reference.RequestHandler = null;
                    }
                }
            }
        }

        internal async ValueTask<IRequestHandler> GetRequestHandlerAsync(Reference reference)
        {
            if (reference.IsCollocationOptimized)
            {
                ObjectAdapter? adapter = FindObjectAdapter(reference);
                if (adapter != null)
                {
                    return new CollocatedRequestHandler(reference, adapter);
                }
            }

            if (reference.IsConnectionCached)
            {
                ConnectAwaitable? connectAwaitable = null;
                lock (_pendingConnects)
                {
                    if (reference.RequestHandler != null)
                    {
                        return reference.RequestHandler;
                    }

                    if (!_pendingConnects.TryGetValue(reference, out connectAwaitable))
                    {
                        ValueTask<IRequestHandler> task = reference.GetConnectionRequestHandlerAsync();
                        if (task.IsCompleted)
                        {
                            return task.Result;
                        }
                        connectAwaitable = new ConnectAwaitable(this, reference, task.AsTask());
                        _pendingConnects[reference] = connectAwaitable;
                    }
                }
                return await connectAwaitable;
            }
            else
            {
                return await reference.GetConnectionRequestHandlerAsync().ConfigureAwait(false);
            }
        }

        private void RemovePendingConnect(Reference reference, IRequestHandler? handler)
        {
            lock (_pendingConnects)
            {
                Debug.Assert(reference.IsConnectionCached);
                _pendingConnects.Remove(reference);
                reference.RequestHandler = handler;
            }
        }

        private class ConnectAwaitable
        {
            public bool IsCompleted
            {
                get
                {
                    lock (_mutex)
                    {
                        return !_pending;
                    }
                }
            }

            private readonly Communicator _communicator;
            private Exception? _exception;
            private IRequestHandler? _handler;
            private readonly object _mutex = new object();
            private bool _pending;
            private Queue<Action>? _queue;
            private readonly Reference _reference;

            public ConnectAwaitable(Communicator communicator, Reference reference, Task<IRequestHandler> task)
            {
                _communicator = communicator;
                _reference = reference;
                _pending = true;
                _ = WaitForConnectAndFlushPendingAsync(task);
            }

            public void Enqueue(Action action)
            {
                lock (_mutex)
                {
                    // Queue the continuation if the connect is pending
                    if (_pending)
                    {
                        _queue ??= new Queue<Action>();
                        _queue.Enqueue(action);
                        return;
                    }
                }

                // Connect is no longer pending, run the continuation now
                Debug.Assert(_handler != null || _exception != null);
                action();
            }

            public ConnectAwaiter GetAwaiter() => new ConnectAwaiter(this);

            public IRequestHandler GetResult()
            {
                Debug.Assert(_exception != null || _handler != null);
                if (_exception != null)
                {
                    throw _exception;
                }
                return _handler!;
            }

            private void Completed()
            {
                Queue<Action> queue;
                lock (_mutex)
                {
                    // If there's no continuations queued, just mark this awaitable as completed. This will cause
                    // await on this awaitable to complete synchronously.
                    if (_queue == null)
                    {
                        _pending = false;
                        _communicator.RemovePendingConnect(_reference, _handler);
                        return;
                    }

                    // Get the queued continuations and clear the queue. If new continuations are added a new queue
                    // will be created and we'll try to obtain again the connection request handler from the factory.
                    queue = _queue;
                    _queue = null;
                }

                // Run the continuations
                foreach (Action action in queue)
                {
                    action();
                }

                lock (_mutex)
                {
                    if (_queue == null)
                    {
                        // No more requests queued, it's time to abandon this awaitable and unregister it.
                        _pending = false;
                        _communicator.RemovePendingConnect(_reference, _handler);
                        return;
                    }

                    // If new requests got queued while we were flushing the queue, we reset the awaitable and
                    // try to obtain a new connection request handler.
                    _handler = null;
                    _exception = null;
                    _pending = true;
                }
                _ = WaitForConnectAndFlushPendingAsync(_reference.GetConnectionRequestHandlerAsync().AsTask());
            }

            private async Task WaitForConnectAndFlushPendingAsync(Task<IRequestHandler> task)
            {
                // Wait for the connection request handler to be returned
                try
                {
                    _handler = await task.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _exception = ex;
                }
                // Flush the queued continuations
                Completed();
            }
        }

        private struct ConnectAwaiter : System.Runtime.CompilerServices.INotifyCompletion
        {
            public bool IsCompleted => _awaitable.IsCompleted;

            private readonly ConnectAwaitable _awaitable;

            public ConnectAwaiter(ConnectAwaitable awaitable) => _awaitable = awaitable;

            public IRequestHandler GetResult() => _awaitable.GetResult();

            public void OnCompleted(Action continuation) => _awaitable.Enqueue(continuation);
        }
    }
}
