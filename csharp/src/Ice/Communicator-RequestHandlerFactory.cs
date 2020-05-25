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
                    if (_pendingConnects.TryGetValue(reference, out Task<IRequestHandler>? value))
                    {
                        connectTask = ChainAsync(value!);
                        _pendingConnects[reference] = connectTask;
                    }
                    else
                    {
                        ValueTask<IRequestHandler> connect = reference.GetConnectionRequestHandlerAsync();
                        if (connect.IsCompleted)
                        {
                            return connect.Result;
                        }
                        connectTask = connect.AsTask();
                        _pendingConnects[reference] = connectTask;
                        connectTask = CompleteConnectAsync(reference, connectTask);
                    }
                }

                if (cancel.CanBeCanceled)
                {
                    var cancelableSource = new TaskCompletionSource<bool>();
                    using (cancel.Register(() => cancelableSource.TrySetCanceled()))
                    {
                        Task completed = await Task.WhenAny(connectTask, cancelableSource.Task).ConfigureAwait(false);
                        if (completed != connectTask)
                        {
                            Debug.Assert(cancel.IsCancellationRequested);
                            cancel.ThrowIfCancellationRequested();
                        }
                        Debug.Assert(connectTask.IsCompleted);
                        return connectTask.Result;
                    }
                }
                else
                {
                    return await connectTask.ConfigureAwait(false);
                }
            }
            else
            {
                return await reference.GetConnectionRequestHandlerAsync().ConfigureAwait(false);
            }

            // Return a task that will complete only once the given task completes to guarantee ordering.
            // TODO: Verify that this is actually needed! I.e: are continuations of multiple await on the same task
            // run in the same order as they were awaited?
            static async Task<IRequestHandler> ChainAsync(Task<IRequestHandler> task) =>
                await task.ConfigureAwait(false);
        }

        private async Task<IRequestHandler> CompleteConnectAsync(Reference reference, Task<IRequestHandler> connectTask)
        {
            IRequestHandler handler = await connectTask.ConfigureAwait(false);
            lock (_pendingConnects)
            {
                Debug.Assert(_pendingConnects.ContainsKey(reference));
                _pendingConnects.Remove(reference);
            }
            return handler;
        }

        private readonly Dictionary<Reference, Task<IRequestHandler>> _pendingConnects =
            new Dictionary<Reference, Task<IRequestHandler>>();
    }
}
