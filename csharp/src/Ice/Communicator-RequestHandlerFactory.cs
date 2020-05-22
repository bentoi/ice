//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using IceInternal;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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
                bool connect = false;
                var source = new TaskCompletionSource<IRequestHandler>();
                _pendingConnects.AddOrUpdate(reference,
                    key =>
                    {
                        // Add list to queue task completion sources
                        var sources = new List<TaskCompletionSource<IRequestHandler>>() { source };
                        connect = true;
                        return sources;
                    },
                    (key, value) =>
                    {
                        // Update task completion source list
                        value.Add(source);
                        return value;
                    });

                if (connect)
                {
                    // TODO: Optimize if GetConnectionRequestHandlerAsync completes synchronously.
                    _ = reference.GetConnectionRequestHandlerAsync().AsTask().ContinueWith(async task =>
                    {
                        try
                        {
                            IRequestHandler handler = await task.ConfigureAwait(false);

                            // Notify waiting asynchronous requests of the result
                            _pendingConnects.TryRemove(reference,
                                out List<TaskCompletionSource<IRequestHandler>>? sources);
                            foreach (TaskCompletionSource<IRequestHandler> source in sources!)
                            {
                                source.SetResult(handler);
                            }
                        }
                        catch (System.Exception ex)
                        {
                            // Notify waiting asynchronous requests of the exception
                            _pendingConnects.TryRemove(reference,
                                out List<TaskCompletionSource<IRequestHandler>>? sources);
                            foreach (TaskCompletionSource<IRequestHandler> source in sources!)
                            {
                                source.SetException(ex);
                            }
                        }
                    }, TaskScheduler.Default);
                }

                using (cancel.Register(() => source.TrySetCanceled()))
                {
                    return await source.Task!.ConfigureAwait(false);
                }
            }
            else
            {
                return await reference.GetConnectionRequestHandlerAsync().ConfigureAwait(false);
            }
        }

        private readonly ConcurrentDictionary<Reference, List<TaskCompletionSource<IRequestHandler>>> _pendingConnects =
            new ConcurrentDictionary<Reference, List<TaskCompletionSource<IRequestHandler>>>();
    }
}
