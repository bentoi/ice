//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

namespace ZeroC.Ice
{
    public class Ice1Transport : ITransport
    {
        public Endpoint Endpoint { get; }
        public TimeSpan AcmLastActivity { get; private set; }

        private readonly int _compressionLevel;
        private readonly int _frameSizeMax;
        private readonly bool _incoming;
        private readonly object _mutex = new object();
        private int _nextStreamId;
        private Task _receiveTask = Task.CompletedTask;
        private Task _sendTask = Task.CompletedTask;
        private readonly ITransceiver _transceiver;
        private bool _validated;
        private readonly bool _warn;
        private readonly bool _warnUdp;

        private static readonly List<ArraySegment<byte>> _closeConnectionFrame =
            new List<ArraySegment<byte>> { Ice1Definitions.CloseConnectionFrame };

        private static readonly List<ArraySegment<byte>> _validateConnectionFrame =
            new List<ArraySegment<byte>> { Ice1Definitions.ValidateConnectionFrame };

        public async ValueTask CloseAsync(Exception exception, CancellationToken cancel)
        {
            if (!(exception is ConnectionClosedByPeerException))
            {
                // Write and wait for the close connection frame to be written
                try
                {
                    await SendFrameAsync(null, _closeConnectionFrame, true, cancel).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }
            }

            // Notify the transport of the graceful connection closure.
            try
            {
                await _transceiver.ClosingAsync(exception, cancel).ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }

            // Wait for the connection closure from the peer
            try
            {
                await _receiveTask.WaitAsync(cancel).ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }
        public async ValueTask HeartbeatAsync(CancellationToken cancel) =>
            await SendFrameAsync(null, _validateConnectionFrame, true, cancel).ConfigureAwait(false);

        public async ValueTask InitializeAsync(CancellationToken cancel)
        {
            // Initialize the transport
            await _transceiver.InitializeAsync(cancel).ConfigureAwait(false);

            ArraySegment<byte> readBuffer = default;
            if (!Endpoint.IsDatagram) // Datagram connections are always implicitly validated.
            {
                if (_incoming) // The server side has the active role for connection validation.
                {
                    int offset = 0;
                    while (offset < _validateConnectionFrame.GetByteCount())
                    {
                        offset += await _transceiver.WriteAsync(_validateConnectionFrame,
                                                                offset,
                                                                cancel).ConfigureAwait(false);
                    }
                    Debug.Assert(offset == _validateConnectionFrame.GetByteCount());
                }
                else // The client side has the passive role for connection validation.
                {
                    readBuffer = new ArraySegment<byte>(new byte[Ice1Definitions.HeaderSize]);
                    int offset = 0;
                    while (offset < Ice1Definitions.HeaderSize)
                    {
                        offset += await _transceiver.ReadAsync(readBuffer,
                                                                offset,
                                                                cancel).ConfigureAwait(false);
                    }

                    Ice1Definitions.CheckHeader(readBuffer.AsSpan(0, 8));
                    var frameType = (Ice1Definitions.FrameType)readBuffer[8];
                    if (frameType != Ice1Definitions.FrameType.ValidateConnection)
                    {
                        throw new InvalidDataException(@$"received ice1 frame with frame type `{frameType
                            }' before receiving the validate connection frame");
                    }

                    int size = InputStream.ReadInt(readBuffer.AsSpan(10, 4));
                    if (size != Ice1Definitions.HeaderSize)
                    {
                        throw new InvalidDataException(
                            @$"received an ice1 frame with validate connection type and a size of `{size
                            }' bytes");
                    }
                }
            }

            if (!Endpoint.IsDatagram) // Datagram connections are always implicitly validated.
            {
                if (_incoming) // The server side has the active role for connection validation.
                {
                    // TODO
                    //TraceSentAndUpdateObserver(Ice1Definitions.ValidateConnectionFrame.Length);
                    ProtocolTrace.TraceSend(Endpoint.Communicator,
                                            Endpoint.Protocol,
                                            Ice1Definitions.ValidateConnectionFrame);
                }
                else
                {
                    // TODO
                    //TraceReceivedAndUpdateObserver(readBuffer.Count);
                    ProtocolTrace.TraceReceived(Endpoint.Communicator, Endpoint.Protocol, readBuffer);
                }
            }

            lock (_mutex)
            {
                if (Endpoint.Communicator.TraceLevels.Network >= 1)
                {
                    var s = new StringBuilder();
                    if (Endpoint.IsDatagram)
                    {
                        s.Append("starting to ");
                        s.Append(_incoming ? "send" : "receive");
                        s.Append(" ");
                        s.Append(Endpoint.TransportName);
                        s.Append(" datagrams\n");
                        s.Append(_transceiver.ToDetailedString());
                    }
                    else
                    {
                        s.Append(_incoming ? "established" : "accepted");
                        s.Append(" ");
                        s.Append(Endpoint.TransportName);
                        s.Append(" connection\n");
                        s.Append(ToString());
                    }
                    Endpoint.Communicator.Logger.Trace(Endpoint.Communicator.TraceLevels.NetworkCategory,
                                                       s.ToString());
                }

                if (AcmLastActivity != Timeout.InfiniteTimeSpan)
                {
                    AcmLastActivity = Time.Elapsed;
                }
                if (_incoming)
                {
                    _validated = true;
                }
            }
        }

        public async ValueTask<(int StreamId, object Frame, bool Fin)> ReceiveAsync(CancellationToken cancel)
        {
            while (true)
            {
                int requestId = 0;
                object? frame = null;
                Task<ArraySegment<byte>>? task = null;
                lock (_mutex)
                {
                    ValueTask<ArraySegment<byte>> receiveTask = PerformReceiveFrameAsync();
                    if (receiveTask.IsCompletedSuccessfully)
                    {
                        _receiveTask = Task.CompletedTask;
                        (requestId, frame) = ParseFrame(receiveTask.Result);
                    }
                    else
                    {
                        _receiveTask = task = receiveTask.AsTask();
                    }
                }

                if (task != null)
                {
                    (requestId, frame) = ParseFrame(await task.ConfigureAwait(false));
                }

                if (frame != null)
                {
                    return (requestId, frame, requestId > 0 && frame is IncomingRequestFrame);
                }
            }
        }

        public ValueTask ResetAsync(int streamId) =>
            throw new NotSupportedException("ice1 transports don't support stream reset");

        public async ValueTask SendAsync(int streamId, object frame, bool fin, CancellationToken cancel)
        {
            if (frame is OutgoingResponseFrame)
            {
                Debug.Assert(streamId > 0 && fin);
                await SendFrameAsync(null, frame, fin, cancel);
            }
            else
            {
                throw new NotSupportedException("ice1 transports don't support streams");
            }
        }

        public async ValueTask SendAsync(
            Action<int> streamIdCallback,
            object frame,
            bool fin,
            CancellationToken cancel) =>
            await SendFrameAsync(streamIdCallback, frame, fin, cancel);

        internal Ice1Transport(ITransceiver transceiver, Endpoint endpoint, ObjectAdapter adapter)
        {
            _transceiver = transceiver;
            _incoming = adapter != null;
            _frameSizeMax = adapter != null ? adapter.FrameSizeMax : Endpoint.Communicator.FrameSizeMax;
            _warn = Endpoint.Communicator.GetPropertyAsBool("Ice.Warn.Connections") ?? false;
            _warnUdp = Endpoint.Communicator.GetPropertyAsBool("Ice.Warn.Datagrams") ?? false;

            Endpoint = endpoint;

            _compressionLevel = Endpoint.Communicator.GetPropertyAsInt("Ice.Compression.Level") ?? 1;
            if (_compressionLevel < 1)
            {
                _compressionLevel = 1;
            }
            else if (_compressionLevel > 9)
            {
                _compressionLevel = 9;
            }
        }

        private (int, object?) ParseFrame(ArraySegment<byte> readBuffer)
        {
            lock (_mutex)
            {
                // The magic and version fields have already been checked.
                var frameType = (Ice1Definitions.FrameType)readBuffer[8];
                byte compressionStatus = readBuffer[9];
                if (compressionStatus == 2)
                {
                    if (BZip2.IsLoaded)
                    {
                        readBuffer = BZip2.Decompress(readBuffer, Ice1Definitions.HeaderSize, _frameSizeMax);
                    }
                    else
                    {
                        throw new LoadException("compression not supported, bzip2 library not found");
                    }
                }

                switch (frameType)
                {
                    case Ice1Definitions.FrameType.CloseConnection:
                    {
                        ProtocolTrace.TraceReceived(Endpoint.Communicator, Endpoint.Protocol, readBuffer);
                        if (Endpoint.IsDatagram)
                        {
                            if (_warn)
                            {
                                Endpoint.Communicator.Logger.Warning(
                                    $"ignoring close connection frame for datagram connection:\n{this}");
                            }
                        }
                        else
                        {
                            throw new ConnectionClosedByPeerException();
                        }
                        return default;
                    }

                    case Ice1Definitions.FrameType.Request:
                    {
                        var request = new IncomingRequestFrame(Endpoint.Protocol,
                                                                readBuffer.Slice(Ice1Definitions.HeaderSize + 4));
                        ProtocolTrace.TraceFrame(Endpoint.Communicator, readBuffer, request);
                        if (!_incoming)
                        {
                            throw new ObjectNotExistException(request.Identity, request.Facet, request.Operation);
                        }
                        return (InputStream.ReadInt(readBuffer.AsSpan(Ice1Definitions.HeaderSize, 4)), request);
                    }

                    case Ice1Definitions.FrameType.RequestBatch:
                    {
                        ProtocolTrace.TraceReceived(Endpoint.Communicator, Endpoint.Protocol, readBuffer);
                        int invokeNum = InputStream.ReadInt(readBuffer.AsSpan(Ice1Definitions.HeaderSize, 4));
                        if (invokeNum < 0)
                        {
                            throw new InvalidDataException(
                                $"received ice1 RequestBatchMessage with {invokeNum} batch requests");
                        }
                        Debug.Assert(false); // TODO: deal with batch requests
                        return default;
                    }

                    case Ice1Definitions.FrameType.Reply:
                    {
                        var responseFrame = new IncomingResponseFrame(Endpoint.Protocol,
                                                                      readBuffer.Slice(Ice1Definitions.HeaderSize + 4));
                        ProtocolTrace.TraceFrame(Endpoint.Communicator, readBuffer, responseFrame);
                        return (InputStream.ReadInt(readBuffer.AsSpan(14, 4)), responseFrame);
                    }

                    case Ice1Definitions.FrameType.ValidateConnection:
                    {
                        ProtocolTrace.TraceReceived(Endpoint.Communicator, Endpoint.Protocol, readBuffer);
                        // TODO
                        // incoming = () =>
                        // {
                        //     try
                        //     {
                        //         HeartbeatReceived?.Invoke(this, EventArgs.Empty);
                        //     }
                        //     catch (Exception ex)
                        //     {
                        //         Endpoint.Communicator.Logger.Error($"connection callback exception:\n{ex}\n{this}");
                        //     }
                        //     return default;
                        // };
                        return default;
                    }

                    default:
                    {
                        ProtocolTrace.Trace(
                            "received unknown frame\n(invalid, closing connection)",
                            Endpoint.Communicator,
                            Endpoint.Protocol,
                            readBuffer);
                        throw new InvalidDataException(
                            $"received ice1 frame with unknown frame type `{frameType}'");
                    }
                }
            }
        }

        private async ValueTask<ArraySegment<byte>> PerformReceiveFrameAsync()
        {
            // Read header
            ArraySegment<byte> readBuffer;
            if (Endpoint.IsDatagram)
            {
                readBuffer = await _transceiver.ReadAsync().ConfigureAwait(false);
            }
            else
            {
                readBuffer = new ArraySegment<byte>(new byte[256], 0, Ice1Definitions.HeaderSize);
                int offset = 0;
                while (offset < Ice1Definitions.HeaderSize)
                {
                    offset += await _transceiver.ReadAsync(readBuffer, offset).ConfigureAwait(false);
                }
            }

            // Check header
            Ice1Definitions.CheckHeader(readBuffer.AsSpan(0, 8));
            int size = InputStream.ReadInt(readBuffer.Slice(10, 4));
            if (size < Ice1Definitions.HeaderSize)
            {
                throw new InvalidDataException($"received ice1 frame with only {size} bytes");
            }

            if (size > _frameSizeMax)
            {
                throw new InvalidDataException($"frame with {size} bytes exceeds Ice.MessageSizeMax value");
            }

            lock (_mutex)
            {
                // TODO: XXX
                //TraceReceivedAndUpdateObserver(readBuffer.Count);
                if (AcmLastActivity != Timeout.InfiniteTimeSpan)
                {
                    AcmLastActivity = Time.Elapsed;
                }

                // Connection is validated on the first frame. This is only used by setState() to check whether or
                // not we can print a connection warning (a client might close the connection forcefully if the
                // connection isn't validated, we don't want to print a warning in this case).
                _validated = true;
            }

            // Read the remainder of the frame if needed
            if (!Endpoint.IsDatagram)
            {
                if (size > readBuffer.Array!.Length)
                {
                    // Allocate a new array and copy the header over
                    var buffer = new ArraySegment<byte>(new byte[size], 0, size);
                    readBuffer.AsSpan().CopyTo(buffer.AsSpan(0, Ice1Definitions.HeaderSize));
                    readBuffer = buffer;
                }
                else if (size > readBuffer.Count)
                {
                    readBuffer = new ArraySegment<byte>(readBuffer.Array!, 0, size);
                }
                Debug.Assert(size == readBuffer.Count);

                int offset = Ice1Definitions.HeaderSize;
                while (offset < readBuffer.Count)
                {
                    int bytesReceived = await _transceiver.ReadAsync(readBuffer, offset).ConfigureAwait(false);
                    offset += bytesReceived;

                    // Trace the receive progress within the loop as we might be receiving significant amount
                    // of data here.
                    lock (_mutex)
                    {
                        // TODO
                        //TraceReceivedAndUpdateObserver(bytesReceived);
                        if (AcmLastActivity != Timeout.InfiniteTimeSpan)
                        {
                            AcmLastActivity = Time.Elapsed;
                        }
                    }
                }
            }
            else if (size > readBuffer.Count)
            {
                if (_warnUdp)
                {
                    Endpoint.Communicator.Logger.Warning($"maximum datagram size of {readBuffer.Count} exceeded");
                }
                return default;
            }
            return readBuffer;
        }

        private async ValueTask PerformSendFrameAsync(int streamId, object frame, bool fin)
        {
            List<ArraySegment<byte>> writeBuffer;
            if (frame is OutgoingRequestFrame requestFrame)
            {
                Debug.Assert(streamId == 0); // Not assigned yet for a new request!
                writeBuffer = Ice1Definitions.GetRequestData(requestFrame, streamId);
                if (Endpoint.Communicator.TraceLevels.Protocol >= 1)
                {
                    ProtocolTrace.TraceFrame(Endpoint.Communicator, writeBuffer[0], requestFrame);
                }
            }
            else if (frame is OutgoingResponseFrame responseFrame)
            {
                writeBuffer = Ice1Definitions.GetResponseData(responseFrame, streamId);
                if (Endpoint.Communicator.TraceLevels.Protocol > 0)
                {
                    ProtocolTrace.TraceFrame(Endpoint.Communicator, writeBuffer[0], responseFrame);
                }
            }
            else
            {
                Debug.Assert(frame is List<ArraySegment<byte>>);
                writeBuffer = (List<ArraySegment<byte>>)frame;
                if (Endpoint.Communicator.TraceLevels.Protocol > 0)
                {
                    ProtocolTrace.TraceSend(Endpoint.Communicator, Endpoint.Protocol, writeBuffer[0]);
                }
            }

            bool compress = false; // TODO: XXX: How do we deal with compress? Provide it to SendAsync (yuck)?

            // Compress the frame if needed and possible
            int size = writeBuffer.GetByteCount();
            if (BZip2.IsLoaded && compress)
            {
                List<ArraySegment<byte>>? compressed = null;
                if (size >= 100)
                {
                    compressed = BZip2.Compress(writeBuffer, size, Ice1Definitions.HeaderSize, _compressionLevel);
                }

                if (compressed != null)
                {
                    writeBuffer = compressed!;
                    size = writeBuffer.GetByteCount();
                }
                else // Message not compressed, request compressed response, if any.
                {
                    ArraySegment<byte> header = writeBuffer[0];
                    header[9] = 1; // Write the compression status
                }
            }

            // Write the frame
            int offset = 0;
            while (offset < size)
            {
                int bytesSent = await _transceiver.WriteAsync(writeBuffer, offset).ConfigureAwait(false);
                offset += bytesSent;
                lock (_mutex)
                {
                    // TODO
                    //TraceSentAndUpdateObserver(bytesSent);
                    if (AcmLastActivity != Timeout.InfiniteTimeSpan)
                    {
                        AcmLastActivity = Time.Elapsed;
                    }
                }
            }
        }

        private Task SendFrameAsync(Action<int>? streamIdCallback, object frame, bool fin, CancellationToken cancel)
        {
            lock (_mutex)
            {
                cancel.ThrowIfCancellationRequested();
                ValueTask sendTask = QueueAsync(streamIdCallback, frame, fin, cancel);
                _sendTask = sendTask.IsCompletedSuccessfully ? Task.CompletedTask : sendTask.AsTask();
                return _sendTask;
            }

            async ValueTask QueueAsync(Action<int>? streamIdCallback, object frame, bool fin, CancellationToken cancel)
            {
                // Wait for the previous send to complete
                try
                {
                    await _sendTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // If the previous send was canceled, ignore and continue sending.
                }

                // If the send got cancelled, throw now. This isn't a fatal connection error, the next pending
                // outgoing will be sent because we ignore the cancelation exception above.
                cancel.ThrowIfCancellationRequested();

                int streamId = 0;
                if (streamIdCallback != null && !fin)
                {
                    // Assign a new stream ID and notify the caller
                    streamId = ++_nextStreamId;
                    streamIdCallback(streamId); // TODO: safe to call this from here?
                }

                // Perform the write
                await PerformSendFrameAsync(streamId, frame, fin).ConfigureAwait(false);
            }
        }
    }

    internal class TcpServerTransport : IServerTransport
    {
        public Endpoint Endpoint { get; }

        private readonly ObjectAdapter _adapter;
        private readonly Socket _fd;
        private readonly IPEndPoint _addr;

        public async ValueTask<ITransport> AcceptAsync()
        {
            Socket fd = await _fd.AcceptAsync().ConfigureAwait(false);

            // TODO: read data from the socket to figure out if were are accepting an ice1 tcp/ssl connection, or ice2
            // tcp connection

            ITransceiver _transceiver =
                 ((TcpEndpoint)Endpoint).CreateTransceiver(new StreamSocket(Endpoint.Communicator, fd), _adapter.Name);

            return new Ice1Transport(_transceiver, Endpoint, _adapter);
        }

        public ValueTask DisposeAsync()
        {
            Network.CloseSocketNoThrow(_fd);
            return new ValueTask(Task.CompletedTask);
        }

        public string ToDetailedString()
        {
            var s = new StringBuilder("local address = ");
            s.Append(ToString());

            List<string> interfaces =
                Network.GetHostsForEndpointExpand(_addr.Address.ToString(), Endpoint.Communicator.IPVersion, true);
            if (interfaces.Count != 0)
            {
                s.Append("\nlocal interfaces = ");
                s.Append(string.Join(", ", interfaces));
            }
            return s.ToString();
        }

        public override string ToString() => Network.AddrToString(_addr);

        internal TcpServerTransport(TcpEndpoint endpoint, ObjectAdapter adapter)
        {
            _adapter = adapter;

            _addr = Network.GetAddressForServerEndpoint(endpoint.Host,
                                                        endpoint.Port,
                                                        endpoint.Communicator.IPVersion,
                                                        endpoint.Communicator.PreferIPv6);

            _fd = Network.CreateServerSocket(false, _addr.AddressFamily, endpoint.Communicator.IPVersion);
            Network.SetBlock(_fd, false);
            Network.SetTcpBufSize(_fd, endpoint.Communicator);

            _addr = Network.DoBind(_fd, _addr);
            Network.DoListen(_fd, endpoint.Communicator.GetPropertyAsInt("Ice.TCP.Backlog") ?? 511);

            Endpoint = endpoint.NewPort((ushort)_addr.Port);
        }
    }
}
