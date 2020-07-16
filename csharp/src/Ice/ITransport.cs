//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroC.Ice
{
    public interface ITransport : IAsyncDisposable
    {
        TimeSpan AcmLastActivity { get; }
        Endpoint Endpoint { get; }

        ValueTask CloseAsync(Exception exception, CancellationToken cancel);
        ValueTask HeartbeatAsync(CancellationToken cancel);
        ValueTask InitializeAsync(CancellationToken cancel);
        ValueTask<(int StreamId, object Frame, bool Fin)> ReceiveAsync(CancellationToken cancel);
        ValueTask ResetAsync(int streamId);
        ValueTask SendAsync(int streamId, object frame, bool fin, CancellationToken cancel);
        ValueTask SendAsync(Action<int> streamIdCallback, object frame, bool fin, CancellationToken cancel);
    }

    public interface IServerTransport : IAsyncDisposable
    {
        Endpoint Endpoint { get; }

        ValueTask<ITransport> AcceptAsync();
        string ToDetailedString();
    }
}
