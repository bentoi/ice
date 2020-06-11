//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace ZeroC.Ice
{
    public sealed partial class Communicator
    {
        private readonly Dictionary<Reference, GetConnectionAwaitable> _pendingGets =
            new Dictionary<Reference, GetConnectionAwaitable>();

        internal void ClearCachedRequestHandler(Reference reference, IRequestHandler handler)
        {
            Debug.Assert(reference.IsConnectionCached);

            // This is called when a request fails to clear the request handler. The reference request handler
            // data member is protected by the _pendingGets mutex when the connection is cached.
            lock (_pendingGets)
            {
                if (reference.RequestHandler == handler)
                {
                    reference.RequestHandler = null;
                }
            }
        }

        internal async ValueTask<IRequestHandler> GetCachedRequestHandlerAsync(Reference reference)
        {
            Debug.Assert(reference.IsConnectionCached);

            // Get the a request handler for the given reference. Getting the request handler might block if the
            // connection needs to be established. We want to ensure that the continuations are ran in the same
            // order as GetRequestHandlerAsync was called to provide serializating guarantee of async invocations
            // on a proxy when the connection is cached with the proxy and even if the connection isn't established
            // yet. For this purpose, we implement an awaitable that will queue the continuations and run them
            // synchronously in the order they were queued once the connection is establihsed.
            GetConnectionAwaitable? getConnectionAwaitable = null;
            lock (_pendingGets)
            {
                if (reference.RequestHandler == null && reference.IsCollocationOptimized)
                {
                    ObjectAdapter? adapter = FindObjectAdapter(reference);
                    if (adapter != null)
                    {
                        reference.RequestHandler = new CollocatedRequestHandler(reference, adapter);
                    }
                }

                if (reference.RequestHandler != null)
                {
                    return reference.RequestHandler;
                }

                if (!_pendingGets.TryGetValue(reference, out getConnectionAwaitable))
                {
                    ValueTask<IRequestHandler> task = reference.GetConnectionRequestHandlerAsync();
                    if (task.IsCompleted)
                    {
                        reference.RequestHandler = task.Result;
                        return reference.RequestHandler;
                    }
                    getConnectionAwaitable = new GetConnectionAwaitable(_pendingGets, reference, task.AsTask());
                    _pendingGets[reference] = getConnectionAwaitable;
                }
            }
            return await getConnectionAwaitable;
        }

        private class GetConnectionAwaitable
        {
            public bool IsPending { get; private set; }
            private Exception? _exception;
            private IRequestHandler? _handler;
            private readonly Dictionary<Reference, GetConnectionAwaitable> _pendingGets =
                new Dictionary<Reference, GetConnectionAwaitable>();
            private Queue<Action>? _queue;
            private readonly Reference _reference;

            public GetConnectionAwaitable(Dictionary<Reference, GetConnectionAwaitable> pendingGets,
                Reference reference, Task<IRequestHandler> task)
            {
                _pendingGets = pendingGets;
                _reference = reference;
                IsPending = true;

                // Wait for the given get connection task to be done to flush the continuation's queue.
                _ = WaitForGetConnectionAndFlushPendingAsync(task);
            }

            public void Enqueue(Action action)
            {
                lock (_pendingGets)
                {
                    // Queue the continuation if the get connection is still pending.
                    if (IsPending)
                    {
                        _queue ??= new Queue<Action>();
                        _queue.Enqueue(action);
                        return;
                    }
                }

                // Get connection is no longer pending, run the continuation now.
                Debug.Assert(_handler != null || _exception != null);
                action();
            }

            public GetConnectionAwaiter GetAwaiter() => new GetConnectionAwaiter(this);

            public IRequestHandler GetResult() => _handler ?? throw _exception!;

            private async Task WaitForGetConnectionAndFlushPendingAsync(Task<IRequestHandler> task)
            {
                // Wait for the connection request handler to be returned.
                try
                {
                    _handler = await task.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _exception = ex;
                }

                // Get the queued continuations and clear the queue reference. If new continuations are added a new
                // queue will be created and we'll try to obtain again the connection request handler from the factory.
                Queue<Action>? queue;
                lock (_pendingGets)
                {
                    queue = _queue;
                    _queue = null;
                }

                if (queue != null)
                {
                    // Run the continuations
                    foreach (Action action in queue)
                    {
                        action();
                    }
                }

                lock (_pendingGets)
                {
                    if (_queue == null)
                    {
                        // No more requests queued, it's time to abandon this awaitable, we unregister it from the
                        // pending gets and we set the request handler on the reference. If another thread is about
                        // to queue a continuation on the awaitable, the continuatin will be executed synchronously
                        // since IsPending is now false.
                        IsPending = false;
                        _pendingGets.Remove(_reference);
                        _reference.RequestHandler = _handler;
                        return;
                    }

                    // If new requests got queued while we were flushing the queue, we reset the awaitable and
                    // try to obtain a new connection request handler.
                    _handler = null;
                    _exception = null;
                    IsPending = true;
                }

                _ = WaitForGetConnectionAndFlushPendingAsync(_reference.GetConnectionRequestHandlerAsync().AsTask());            }
        }

        private struct GetConnectionAwaiter : System.Runtime.CompilerServices.INotifyCompletion
        {
            public bool IsCompleted => !_awaitable.IsPending;

            private readonly GetConnectionAwaitable _awaitable;

            public GetConnectionAwaiter(GetConnectionAwaitable awaitable) => _awaitable = awaitable;

            public IRequestHandler GetResult() => _awaitable.GetResult();

            public void OnCompleted(Action continuation) => _awaitable.Enqueue(continuation);
        }
    }
}
