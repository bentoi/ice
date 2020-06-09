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

        private class ConnectAwaitable
        {
            public ConcurrentQueue<Action> Queue { get; }
            public bool IsCompleted => _handler != null || _exception != null;
            private IRequestHandler? _handler;
            private Exception? _exception = null;

            public ConnectAwaiter GetAwaiter() => new ConnectAwaiter(this);

            public IRequestHandler GetResult()
            {
                if (_exception != null)
                {
                    throw _exception;
                }
                return _handler!;
            }

            public void SetResult(IRequestHandler handler)
            {
                _handler = handler;
                Completed();
            }

            public void SetException(Exception exception)
            {
                _exception = exception;
                Completed();
            }

            public ConnectAwaitable() => Queue = new ConcurrentQueue<Action>();

            private void Completed()
            {
                while (Queue.TryDequeue(out Action? action))
                {
                    action();
                }
            }
        }

        private struct ConnectAwaiter : System.Runtime.CompilerServices.INotifyCompletion
        {
            private readonly ConnectAwaitable _awaitable;

            public bool IsCompleted => _awaitable.IsCompleted;

            public IRequestHandler GetResult() => _awaitable.GetResult();

            public ConnectAwaiter(ConnectAwaitable awaitable) => _awaitable = awaitable;

            public void OnCompleted(Action continuation) => _awaitable.Queue.Enqueue(continuation);
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
                // Add the task for the connection establishment on this reference to the pending connects dictionary.
                // If an entry already exists, we chain a new task to the existing task and udate the dictionary entry
                // with this chained task. This chaining ensures that requests waiting for the connection request
                // handler will be sent in the same order as they were invoked.
                ConnectAwaitable? connectAwaitable = null;
                lock (_pendingConnects)
                {
                    if (!_pendingConnects.TryGetValue(reference, out connectAwaitable))
                    {
                        // No connect pending, try to obtain a new connection. If the outgoing connection factory
                        // has a matching connection already established this will complete synchronously so it's
                        // important to check for the status of the ValueTask here.
                        connectAwaitable = new ConnectAwaitable();
                        _ = ConnectAsync(reference, connectAwaitable);
                        _pendingConnects[reference] = connectAwaitable;
                    }
                }

                try
                {
                    return await connectAwaitable;
                }
                finally
                {
                    lock (_pendingConnects)
                    {
                        if (_pendingConnects[reference] == connectAwaitable)
                        {
                            _pendingConnects.Remove(reference);
                        }
                    }
                }
            }
            else
            {
                return await reference.GetConnectionRequestHandlerAsync().ConfigureAwait(false);
            }

            static async ValueTask ConnectAsync(Reference reference, ConnectAwaitable awaitable)
            {
                try
                {
                    awaitable.SetResult(await reference.GetConnectionRequestHandlerAsync().ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    awaitable.SetException(ex);
                }
            }
        }
    }
}
