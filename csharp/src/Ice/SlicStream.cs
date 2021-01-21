// Copyright (c) ZeroC, Inc. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using ZeroC.Ice.Slic;

namespace ZeroC.Ice
{
    /// <summary>The stream implementation for Slic.</summary>
    internal class SlicStream : SignaledSocketStream<(int, bool, bool)>
    {
        protected override ReadOnlyMemory<byte> TransportHeader => SlicDefinitions.FrameHeader;
        protected override bool ReceivedEndOfStream => _receivedEndOfStream;
        private int _flowControlCreditReleased;
        private CircularBuffer? _receiveBuffer;
        private int _receiveBufferConsumed;
        private bool _receivedFromBuffer;
        private int _receivedOffset;
        private int _receivedSize;
        private bool _receivedEndOfStream;
        private volatile int _sendMaxSize;
        private AsyncSemaphore? _sendSemaphore;
        private readonly SlicSocket _socket;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                try
                {
                    ValueTask<(int, bool, bool)> valueTask = WaitSignalAsync(CancellationToken.None);
                    Debug.Assert(valueTask.IsCompleted);
                    (_receivedSize, _receivedEndOfStream, _receivedFromBuffer) = valueTask.Result;
                    _receivedOffset = 0;

                    if (!_receivedFromBuffer && _receivedSize == 0)
                    {
                        _socket.FinishedReceivedStreamData(0);
                    }
                }
                catch
                {
                    // Ignore, there's nothing to consume.
                }

                // If there's still data pending to be received for the stream, we notify the socket that
                // we're abandoning the reading. It will finish to read the stream's frame data in order to
                // continue receiving frames for other streams.
                if (!_receivedFromBuffer && _receivedOffset < _receivedSize)
                {
                    _socket.FinishedReceivedStreamData(_receivedSize - _receivedOffset);
                }

                // Only release the flow control credit for incoming streams on Dispose. Flow control for Slic outgoing
                // streams are released when the StreamLast or StreamReset frame is received. It can be received after
                // the stream is disposed (e.g.: for oneway requests we dispose of the stream as soon as the request is
                // sent and before receiving the StreamLast frame).
                if (IsIncoming)
                {
                    ReleaseFlowControlCredit(notifyPeer: true);
                }
            }
        }

        protected override void EnableReceiveFlowControl()
        {
            // TODO: buffer at most 2 Slic packet, configurable buffer capacity?
            _receiveBuffer = new CircularBuffer(2 * _socket.Endpoint.Communicator.SlicPacketMaxSize);

            // If the stream is in the signaled state, the socket is waiting for the frame to be received. In this
            // case we get the frame information and notify again the stream that the frame was received. This time
            // the frame will be received in the buffer and queued.
            if (IsSignaled)
            {
                ValueTask<(int, bool, bool)> valueTask = WaitSignalAsync();
                Debug.Assert(valueTask.IsCompleted);
                (int size, bool fin, bool buffered) = valueTask.Result;
                Debug.Assert(!buffered);
                ReceivedFrame(size, fin);
            }
        }

        protected override void EnableSendFlowControl() => _sendSemaphore = new AsyncSemaphore(1);

        protected override async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancel)
        {
            if (_receivedSize == _receivedOffset)
            {
                if (_receivedEndOfStream)
                {
                    return 0;
                }
                _receivedOffset = 0;

                // Wait to be signaled for the reception of a new stream frame for this stream. If buffering is
                // enabled, check for the circular buffer element count instead of the signal result since
                // multiple Slic frame might have been received and buffered while waiting for the signal.
                (_receivedSize, _receivedEndOfStream, _receivedFromBuffer) =
                    await WaitSignalAsync(cancel).ConfigureAwait(false);
                Console.Error.WriteLine($"DEQUEUED {_receivedSize} {_receivedEndOfStream}");
                if (_receivedSize == 0)
                {
                    if (!_receivedFromBuffer)
                    {
                        _socket.FinishedReceivedStreamData(0);
                    }
                    return 0;
                }
            }

            int size = Math.Min(_receivedSize - _receivedOffset, buffer.Length);
            _receivedOffset += size;
            if (_receivedFromBuffer)
            {
                // Copy the data from the stream's circular receive buffer to the given buffer.
                Debug.Assert(_receiveBuffer != null);
                _receiveBuffer.Consume(buffer.Slice(0, size));

                // If we've consumed 75% or more of the circular buffer capacity, notify the peer to allow more data
                // to be sent.
                int consumed = Interlocked.Add(ref _receiveBufferConsumed, size);
                if (_socket.Endpoint.Communicator.TraceLevels.Transport > 2)
                {
                    _socket.Endpoint.Communicator.Logger.Trace(
                        TraceLevels.TransportCategory,
                        $"consumed {consumed}/{_receiveBuffer.Capacity} bytes of flow control credit");
                }
                if (consumed >= _receiveBuffer.Capacity * 0.75)
                {
                    // Notify the peer that it can send additional data.
                    await _socket.PrepareAndSendFrameAsync(
                        SlicDefinitions.FrameType.StreamConsumed,
                        ostr =>
                        {
                            checked
                            {
                                new StreamConsumedBody((ulong)consumed).IceWrite(ostr);
                            }
                        },
                        Id,
                        CancellationToken.None).ConfigureAwait(false);
                    Interlocked.Exchange(ref _receiveBufferConsumed, 0);
                }
            }
            else
            {
                // Read and append the received stream frame data into the given buffer.
                await _socket.ReceiveDataAsync(buffer.Slice(0, size), CancellationToken.None).ConfigureAwait(false);

                // If we've consumed the whole Slic frame, notify the socket that it can start receiving a new frame.
                if (_receivedOffset == _receivedSize)
                {
                    _socket.FinishedReceivedStreamData(0);
                }
            }
            return size;
        }

        protected override async ValueTask ResetAsync(long errorCode)
        {
            // Abort the stream.
            Abort(new IOException($"the stream was aborted with the error code {errorCode}"));

            // Send the reset frame to the peer.
            await _socket.PrepareAndSendFrameAsync(
                SlicDefinitions.FrameType.StreamReset,
                ostr =>
                {
                    checked
                    {
                        new StreamResetBody((ulong)errorCode).IceWrite(ostr);
                    }
                },
                Id).ConfigureAwait(false);

            ReleaseFlowControlCredit();
        }

        protected override async ValueTask SendAsync(
            IList<ArraySegment<byte>> buffer,
            bool fin,
            CancellationToken cancel)
        {
            // Ensure the caller reserved space for the Slic header by checking for the sentinel header.
            Debug.Assert(TransportHeader.Span.SequenceEqual(buffer[0].Slice(0, TransportHeader.Length)));

            int size = buffer.GetByteCount() - TransportHeader.Length;

            // If the protocol buffer is larger than what the stream allowed to send or larger than the peer
            // Slic packet maximum size, send the buffer with smaller and multiple frames.
            int packetSize = Math.Min(_sendMaxSize, _socket.PeerPacketMaxSize);
            if (size > packetSize)
            {
                // The send buffer for the Slic stream frame.
                var sendBuffer = new List<ArraySegment<byte>>(buffer.Count);

                // The amount of data sent so far.
                int offset = 0;

                // The position of the data to send next.
                var start = new OutputStream.Position();

                while (offset < size)
                {
                    sendBuffer.Clear();

                    if (_sendSemaphore != null)
                    {
                        // Acquire the semaphore to ensure flow control allows sending additional data.
                        await _sendSemaphore.WaitAsync(cancel).ConfigureAwait(false);
                    }

                    // Compute next packet size which can be smaller or larger than the packet max size if flow
                    // control is enabled.
                    packetSize = Math.Min(_sendMaxSize, _socket.PeerPacketMaxSize);

                    int sendSize = 0;
                    if (offset > 0)
                    {
                        // If it's not the first Slic stream frame, we re-use the space reserved for the Slic header in
                        // the first segment of the protocol buffer.
                        sendBuffer.Add(buffer[0].Slice(0, TransportHeader.Length));
                    }
                    else
                    {
                        sendSize = -TransportHeader.Length;
                    }

                    // Append data until we reach the Slic packet size or the end of the buffer to send.
                    bool lastBuffer = false;
                    for (int i = start.Segment; i < buffer.Count; ++i)
                    {
                        int segmentOffset = i == start.Segment ? start.Offset : 0;
                        if (buffer[i].Slice(segmentOffset).Count > packetSize - sendSize)
                        {
                            sendBuffer.Add(buffer[i].Slice(segmentOffset, packetSize - sendSize));
                            start = new OutputStream.Position(i, segmentOffset + sendBuffer[^1].Count);
                            Debug.Assert(start.Offset < buffer[i].Count);
                            sendSize = packetSize;
                            break;
                        }
                        else
                        {
                            sendBuffer.Add(buffer[i].Slice(segmentOffset));
                            sendSize += sendBuffer[^1].Count;
                            lastBuffer = i + 1 == buffer.Count;
                        }
                    }

                    // Send the Slic stream frame.
                    offset += sendSize;

                    try
                    {
                        await _socket.SendStreamFrameAsync(
                            this,
                            sendSize,
                            lastBuffer && fin,
                            sendBuffer,
                            cancel).ConfigureAwait(false);

                        if (_sendSemaphore != null)
                        {
                            // If flow control is enabled, decrease the size of remaining data that we are allowed to
                            // send. This size is increased when the peer sends a notification that it consumed the
                            // data with the StreamConsumed frame. If we've consumed all the credit for sending data,
                            // _sendMaxSize will be 0. In this case, we don't release the semaphore, it will be
                            // released when the peer sends a StreamConsumed frame.
                            int value = Interlocked.Add(ref _sendMaxSize, -sendSize);
                            if (_socket.Endpoint.Communicator.TraceLevels.Transport > 2)
                            {
                                _socket.Endpoint.Communicator.Logger.Trace(
                                    TraceLevels.TransportCategory,
                                    $"decreased flow control credit to {value} {sendSize}");
                            }
                            if (value > 0)
                            {
                                // If flow control allows sending more data, release the semaphore.
                                _sendSemaphore.Release();
                            }
                        }
                    }
                    catch
                    {
                        _sendSemaphore?.Release();
                        throw;
                    }
                }
            }
            else
            {
                if (_sendSemaphore != null)
                {
                    // Acquire the semaphore to ensure flow control allows sending additional data.
                    await _sendSemaphore.WaitAsync(cancel).ConfigureAwait(false);
                }

                // If the buffer to send is small enough to fit in a single Slic stream frame, send it directly.
                try
                {
                    await _socket.SendStreamFrameAsync(this, size, fin, buffer, cancel).ConfigureAwait(false);
                    if (_sendSemaphore != null)
                    {
                        // If flow control is enabled, decrease the size of remaining data that we are allowed to
                        // send. This size is increased when the peer sends a notification that it consumed the
                        // data with the StreamConsumed frame. If we've consumed all the credit for sending data,
                        // _sendMaxSize will be 0. In this case, we don't release the semaphore, it will be
                        // released when the peer sends a StreamConsumed frame.
                        int value = Interlocked.Add(ref _sendMaxSize, -size);
                        if (_socket.Endpoint.Communicator.TraceLevels.Transport > 2)
                        {
                            _socket.Endpoint.Communicator.Logger.Trace(
                                TraceLevels.TransportCategory,
                                $"decreased flow control credit to {value} {size}");
                        }
                        if (value > 0)
                        {
                            // If flow control allows sending more data, release the semaphore.
                            _sendSemaphore.Release();
                        }
                    }
                }
                catch
                {
                    _sendSemaphore?.Release();
                    throw;
                }
            }
        }

        internal SlicStream(SlicSocket socket, long streamId)
            : base(socket, streamId)
        {
            _socket = socket;
            _sendMaxSize = 2 * _socket.PeerPacketMaxSize;
        }

        internal SlicStream(SlicSocket socket, bool bidirectional, bool control)
            : base(socket, bidirectional, control)
        {
            _socket = socket;
            _sendMaxSize = 2 * _socket.PeerPacketMaxSize;
        }

        internal void ReceivedConsumed(int size)
        {
            if (_sendSemaphore == null)
            {
                throw new InvalidDataException("invalid stream consumed frame, flow control is not enabled");
            }

            // If _sendPacketMaxSize == 0, we release the semaphore to allow new sends.
            int previous = _sendMaxSize;
            int newValue = Interlocked.Add(ref _sendMaxSize, size);
            if (newValue == size)
            {
                Debug.Assert(_sendSemaphore.Count == 0);
                _sendSemaphore.Release();
            }
            else if (newValue > 2 * _socket.PeerPacketMaxSize)
            {
                // The peer is trying to increase the credit to a value which is larger than what it is allowed to.
                throw new InvalidDataException("invalid flow control credit increase");
            }

            if (_socket.Endpoint.Communicator.TraceLevels.Transport > 2)
            {
                _socket.Endpoint.Communicator.Logger.Trace(
                    TraceLevels.TransportCategory,
                    $"increased flow control credit to {newValue} {previous}");
            }
        }

        internal void ReceivedFrame(int size, bool fin)
        {
            // If an outgoing stream and this is the last stream frame, we release the flow control
            // credit to eventually allow a new outgoing stream to be opened. If the flow control credit
            // is already released, there's an issue with the peer sending twice a last stream frame.
            if (!IsIncoming && fin && !ReleaseFlowControlCredit())
            {
                throw new InvalidDataException("already received last stream frame");
            }

            // If no ReceiveAsync call is pending and we can buffer the received data, receive the data in the
            // stream's buffer. Otherwise, we just signal the stream, the data will be consumed when ReceiveAsync
            // is called.
            if (_receiveBuffer == null)
            {
                SetResult((size, fin, false));
            }
            else
            {
                // If the peer sends more data than the circular buffer remaining capacity, it violated flow control.
                // It's considered as a fatal failure for the connection.
                if (size > _receiveBuffer.Available)
                {
                    throw new InvalidDataException("flow control violation, peer sent too much data");
                }

                // Receive the data asynchronously. The task will notify the socket when the Slic frame is fully
                // received to allow the socket to process the next Slic frame.
                _ = PerformReceiveInBufferAsync();
            }

            async Task PerformReceiveInBufferAsync()
            {
                Debug.Assert(_receiveBuffer != null);

                // Read and append the received data into the circular buffer.
                for (int offset = 0; offset < size;)
                {
                    // Get a segment from the buffer to receive the data. The buffer might return a smaller
                    // segment than the requested size. If this is the case, we'll loop to receiving the
                    // remaining data in a next available segment.
                    Memory<byte> segment = _receiveBuffer.Enqueue(size - offset);
                    await _socket.ReceiveDataAsync(segment, CancellationToken.None).ConfigureAwait(false);
                    offset += segment.Length;
                }

                // Queue the result before notify the socket we're done reading. It's important to ensure the
                // frame receives are queued in order.
                try
                {
                    QueueResult((size, fin, true));
                }
                catch
                {
                    // Ignore, the stream has been canceled.
                }

                _socket.FinishedReceivedStreamData(0);
            }
        }

        internal override void ReceivedReset(long errorCode)
        {
            // We ignore the stream reset if the stream flow control credit is already released (the last stream
            // frame has already been sent). Otherwise, we send release the flow control credit and eventually
            // send a last stream frame to notify the outgoing side if this is an incoming connection.
            if (ReleaseFlowControlCredit())
            {
                Abort(new IOException($"the peer aborted the stream with the error code {errorCode}"));
                base.ReceivedReset(errorCode);
            }
        }

        internal bool ReleaseFlowControlCredit(bool notifyPeer = false)
        {
            if (Interlocked.CompareExchange(ref _flowControlCreditReleased, 1, 0) == 0)
            {
                if (!IsControl)
                {
                    // If the flow control credit is not already released, releases it now.
                    _socket.ReleaseFlowControlCredit(this);

                    if (notifyPeer)
                    {
                        // It's important to decrement the stream count before sending the StreamLast frame to prevent
                        // a race where the peer could start a new stream before the counter is decremented.
                        _ = _socket.PrepareAndSendFrameAsync(SlicDefinitions.FrameType.StreamLast, streamId: Id);
                    }
                }
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}
