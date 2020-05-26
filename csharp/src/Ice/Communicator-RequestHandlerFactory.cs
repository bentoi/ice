//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using IceInternal;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace ZeroC.Ice
{
    public sealed partial class Communicator
    {
        // All the reference here are routable references.

        internal async ValueTask<IRequestHandler> GetRequestHandlerAsync(Reference reference, CancellationToken cancel)
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
                Task<IRequestHandler> connectTask;
                lock (_pendingConnects)
                {
                    if (_pendingConnects.TryGetValue(reference, out Task<IRequestHandler>? previousTask))
                    {
                        // A connect is pending, create a new task that completes when the previously queued task
                        // completes.
                        connectTask = ChainAsync(previousTask!);
                    }
                    else
                    {
                        // No connect pending, try to obtain a new connection. If the outgoing connection factory
                        // has a matching connection already established this will complete synchronously so it's
                        // important to check for the status of the ValueTask here.
                        ValueTask<IRequestHandler> connect = reference.GetConnectionRequestHandlerAsync();
                        if (connect.IsCompleted)
                        {
                            return connect.Result;
                        }
                        connectTask = ChainAsync(connect.AsTask());
                    }
                    _pendingConnects[reference] = connectTask;
                }

                try
                {
                    if (!connectTask.IsCompleted && cancel.CanBeCanceled)
                    {
                        var cancelable = new TaskCompletionSource<bool>();
                        using (cancel.Register(() => cancelable.TrySetCanceled()))
                        {
                            Task completed = await Task.WhenAny(connectTask, cancelable.Task).ConfigureAwait(false);
                            if (completed != connectTask)
                            {
                                Debug.Assert(cancel.IsCancellationRequested);
                                cancel.ThrowIfCancellationRequested();
                            }
                            Debug.Assert(connectTask.IsCompleted);
                            return await connectTask.ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        return await connectTask.ConfigureAwait(false);
                    }
                }
                finally
                {
                    lock (_pendingConnects)
                    {
                        if (_pendingConnects[reference] == connectTask)
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

            static async Task<IRequestHandler> ChainAsync(Task<IRequestHandler> task) =>
                await task.ConfigureAwait(false);
        }

        private readonly Dictionary<Reference, Task<IRequestHandler>> _pendingConnects =
            new Dictionary<Reference, Task<IRequestHandler>>();
    }
}
