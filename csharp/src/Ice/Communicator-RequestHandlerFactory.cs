//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using IceInternal;
using System.Collections.Concurrent;
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
                ValueTask<IRequestHandler> connect = reference.GetConnectionRequestHandlerAsync();
                if (connect.IsCompleted)
                {
                    // The task completes synchronously if the connection is already established so it's important to
                    // check here if it's completed to avoid having to perform more costly code bellow to wait for the
                    // connection establishment.
                    return connect.Result;
                }

                // Add the task for the connection establishment on this reference to the pending connects dictionary.
                // If an entry already exists, we chain a new task to the existing task and udate the dictionary entry
                // with this chained task. This chaining ensures that requests waiting for the connection request
                // handler will be sent in the same order as they were invoked.
                Task<IRequestHandler> connectTask = connect.AsTask();
                connectTask = _pendingConnects.AddOrUpdate(reference, connectTask, (key, value) => Chain(connectTask));

                var cancelableTaskSource = new TaskCompletionSource<bool>();
                using (cancel.Register(() => cancelableTaskSource.TrySetCanceled()))
                {
                    Task completed = await Task.WhenAny(connectTask, cancelableTaskSource.Task).ConfigureAwait(false);
                    if (completed == connectTask)
                    {
                        return await connectTask;
                    }
                    else
                    {
                        await cancelableTaskSource.Task;
                        Debug.Assert(false);
                        return null!;
                    }
                }
            }
            else
            {
                return await reference.GetConnectionRequestHandlerAsync().ConfigureAwait(false);
            }

            // Return a task that will complete only once the given task completes to guarantee ordering.
            // TODO: Verify that this is actually needed! I.e: are continuations of multiple await on the same task
            // run in the same order as they were awaited?
            static async Task<IRequestHandler> Chain(Task<IRequestHandler> task) => await task.ConfigureAwait(false);
        }

        private readonly ConcurrentDictionary<Reference, Task<IRequestHandler>> _pendingConnects =
            new ConcurrentDictionary<Reference, Task<IRequestHandler>>();
    }
}
