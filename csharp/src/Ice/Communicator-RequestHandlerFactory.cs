//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using IceInternal;
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
                TaskCompletionSource<IRequestHandler> source;
                lock (_handlers)
                {
                    if (!_handlers.TryGetValue(reference, out Queue<TaskCompletionSource<IRequestHandler>>? queue))
                    {
                        queue = new Queue<TaskCompletionSource<IRequestHandler>>();
                        _handlers.Add(reference, queue);
                        connect = true;
                    }
                    source = new TaskCompletionSource<IRequestHandler>();
                    queue!.Enqueue(source);
                }

                if (connect)
                {
                    // TODO: Optimize if GetConnectionRequestHandlerAsync completes synchronously.
                    _ = reference.GetConnectionRequestHandlerAsync().AsTask().ContinueWith(async task =>
                    {
                        try
                        {
                            IRequestHandler handler = await task;
                            lock (_handlers)
                            {
                                foreach (TaskCompletionSource<IRequestHandler> source in _handlers[reference])
                                {
                                    source.SetResult(handler);
                                }
                                _handlers.Remove(reference);
                            }
                        }
                        catch (System.Exception ex)
                        {
                            lock (_handlers)
                            {
                                foreach (TaskCompletionSource<IRequestHandler> source in _handlers[reference])
                                {
                                    source.SetException(ex);
                                }
                                _handlers.Remove(reference);
                            }
                            throw;
                        }
                    }, TaskScheduler.Default); // TODO: Review?
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

        private readonly Dictionary<Reference, Queue<TaskCompletionSource<IRequestHandler>>> _handlers =
            new Dictionary<Reference, Queue<TaskCompletionSource<IRequestHandler>>>();
    }
}
