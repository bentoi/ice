//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroC.Ice
{
    public interface IBinaryConnection : IAsyncDisposable
    {
        Endpoint Endpoint { get; }
        ITransceiver Transceiver { get; }

        /// <summary>Gracefully closes the transport.</summary>
        ValueTask ClosingAsync(Exception ex, CancellationToken cancel);

        /// <summary>Sends a heartbeat.</summary>
        ValueTask HeartbeatAsync(CancellationToken cancel);

        /// <summary>Initializes the transport.</summary>
        ValueTask InitializeAsync(
            Action heartbeatCallback,
            Action<int> sentCallback,
            Action<int> receivedCallback,
            CancellationToken cancel);

        /// <summary>Receives a new frame.</summary>
        ValueTask<(long StreamId, IncomingFrame? Frame, bool Fin)> ReceiveAsync(CancellationToken cancel);

        /// <summary>Creates a new stream.</summary>
        long NewStream(bool bidirectional);

        /// <summary>Resets the given stream.</summary>
        ValueTask ResetAsync(long streamId);

        /// <summary>Sends the given frame on an existing stream.</summary>
        ValueTask SendAsync(long streamId, OutgoingFrame frame, bool fin, CancellationToken cancel);
    }
}
