//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using IceInternal;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ZeroC.Ice
{
    public sealed partial class Communicator
    {
        // All the reference here are routable references.

        internal async ValueTask<IRequestHandler> GetRequestHandler(Reference reference)
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
                Task<IRequestHandler>? task;
                bool connect = false;
                lock (_handlers)
                {
                    if (!_handlers.TryGetValue(reference, out task))
                    {
                        task = reference.GetConnectionRequestHandlerAsync().AsTask();
                        _handlers.Add(reference, task);
                        connect = true;
                    }
                    else
                    {
                        task = Chain(task);
                        _handlers[reference] = task;
                    }
                }

                try
                {
                    return await task!.ConfigureAwait(false); // TODO: Compress flag
                }
                finally
                {
                    if (connect)
                    {
                        lock (_handlers)
                        {
                            _handlers.Remove(reference);
                        }
                    }
                }
            }
            else
            {
                return await reference.GetConnectionRequestHandlerAsync();
            }

            static async Task<IRequestHandler> Chain(Task<IRequestHandler> task) => await task.ConfigureAwait(false);
        }

        private readonly Dictionary<Reference, Task<IRequestHandler>> _handlers =
            new Dictionary<Reference, Task<IRequestHandler>>();
    }
}
