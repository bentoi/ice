//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroC.Ice
{
    internal sealed class WSTransceiver : ITransceiver
    {
        public IReadOnlyDictionary<string, string> Headers => _parser.GetHeaders();

        internal SslStream? SslStream => (_underlying as SslTransceiver)?.SslStream;

        private enum OpCode : byte
        {
            Continuation = 0x0,
            Text = 0x1,
            Data = 0x2,
            Close = 0x8,
            Ping = 0x9,
            Pong = 0xA
        };

        private enum ClosureStatusCode : short
        {
            Normal = 1000,
            Shutdown = 1001
        };

        private const byte FLAG_FINAL = 0x80;   // Last frame
        private const byte FLAG_MASKED = 0x80;   // Payload is masked
        private const string IceProtocol = "ice.zeroc.com";
        private const string WsUUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private static readonly UTF8Encoding _utf8 = new UTF8Encoding(false, true);

        private bool _closing;
        private readonly Communicator _communicator;
        private readonly bool _incoming;
        private readonly string _host;
        private string _key;
        private readonly HttpParser _parser;
        private readonly object _mutex = new object();
        private readonly ITransceiver _underlying;
        private readonly Random _rand;
        private ArraySegment<byte> _readBuffer;
        private bool _readLastFrame;
        private ArraySegment<byte> _readMask;
        private int _readPayloadLength;
        private int _readPayloadOffset;
        private string _resource;
        private readonly string _transportName;
        private readonly byte[] _writeMask;
        private readonly IList<ArraySegment<byte>> _writeBuffer;
        private Task _writeTask = Task.CompletedTask;

        public void CheckSendSize(int size) => _underlying.CheckSendSize(size);

        public async ValueTask ClosingAsync(Exception exception, CancellationToken cancel)
        {
            byte[] payload = new byte[2];
            short reason = System.Net.IPAddress.HostToNetworkOrder(
                (short)(exception is ObjectDisposedException ? ClosureStatusCode.Shutdown : ClosureStatusCode.Normal));
            MemoryMarshal.Write(payload, ref reason);

            _closing = true;

            // Write the close frame.
            await WriteImplAsync(OpCode.Close, new List<ArraySegment<byte>> { payload }, cancel).ConfigureAwait(false);

            if (exception is ConnectionClosedByPeerException)
            {
                await ReadFrameAsync(new ArraySegment<byte>(), cancel).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync() => _underlying.DisposeAsync();

        public Socket? Fd() => _underlying.Fd();

        public async ValueTask InitializeAsync(CancellationToken cancel)
        {
            await _underlying.InitializeAsync(cancel).ConfigureAwait(false);

            try
            {
                //
                // The server waits for the client's upgrade request, the client sends the upgrade request.
                //
                if (!_incoming)
                {
                    //
                    // Compose the upgrade request.
                    //
                    var sb = new StringBuilder();
                    sb.Append("GET " + _resource + " HTTP/1.1\r\n");
                    sb.Append("Host: " + _host + "\r\n");
                    sb.Append("Upgrade: websocket\r\n");
                    sb.Append("Connection: Upgrade\r\n");
                    sb.Append("Sec-WebSocket-Protocol: " + IceProtocol + "\r\n");
                    sb.Append("Sec-WebSocket-Version: 13\r\n");
                    sb.Append("Sec-WebSocket-Key: ");

                    //
                    // The value for Sec-WebSocket-Key is a 16-byte random number,
                    // encoded with Base64.
                    //
                    byte[] key = new byte[16];
                    _rand.NextBytes(key);
                    _key = Convert.ToBase64String(key);
                    sb.Append(_key + "\r\n\r\n"); // EOM
                    byte[] data = _utf8.GetBytes(sb.ToString());
                    _writeBuffer.Add(data);

                    await _underlying.WriteAsync(_writeBuffer, cancel).ConfigureAwait(false);
                }

                //
                // Try to read the client's upgrade request or the server's response.
                //
                int offset = 0;
                _readBuffer = new byte[1024];
                _writeBuffer.Clear();
                ArraySegment<byte> httpBuffer;
                while (true)
                {
                    offset += await _underlying.ReadAsync(_readBuffer.Slice(offset), cancel).ConfigureAwait(false);

                    // Check if we have enough data for a complete frame.
                    int p = HttpParser.IsCompleteMessage(_readBuffer.AsSpan(0, offset));
                    if (p != -1)
                    {
                        httpBuffer = _readBuffer.Slice(0, p);
                        _readBuffer = _readBuffer.Slice(p, offset - p);
                        break; // Done
                    }
                    else if (offset == _readBuffer.Array!.Length)
                    {
                        // Enlarge the buffer and try to read more.
                        if (offset + 1024 > _communicator.IncomingFrameSizeMax)
                        {
                            throw new InvalidDataException("WebSocket frame size exceeds Ice.IncomingFrameSizeMax value");
                        }
                        byte[] tmpBuffer = new byte[offset + 1024];
                        _readBuffer.AsSpan(0, offset).CopyTo(tmpBuffer);
                        _readBuffer = tmpBuffer;
                    }
                }

                try
                {
                    if (_parser.Parse(httpBuffer))
                    {
                        if (_incoming)
                        {
                            (bool addProtocol, string key) = ReadUpgradeRequest();

                            // Compose the response.
                            var sb = new StringBuilder();
                            sb.Append("HTTP/1.1 101 Switching Protocols\r\n");
                            sb.Append("Upgrade: websocket\r\n");
                            sb.Append("Connection: Upgrade\r\n");
                            if (addProtocol)
                            {
                                sb.Append($"Sec-WebSocket-Protocol: {IceProtocol}\r\n");
                            }

                            // The response includes:
                            //
                            // "A |Sec-WebSocket-Accept| header field.  The value of this header field is constructed
                            // by concatenating /key/, defined above in step 4 in Section 4.2.2, with the string
                            // "258EAFA5-E914-47DA-95CA-C5AB0DC85B11", taking the SHA-1 hash of this concatenated value
                            // to obtain a 20-byte value and base64-encoding (see Section 4 of [RFC4648]) this 20-byte
                            // hash.
                            sb.Append("Sec-WebSocket-Accept: ");
                            string input = key + WsUUID;
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms
                            byte[] hash = SHA1.Create().ComputeHash(_utf8.GetBytes(input));
#pragma warning restore CA5350 // Do Not Use Weak Cryptographic Algorithms
                            sb.Append(Convert.ToBase64String(hash) + "\r\n" + "\r\n"); // EOM

                            Debug.Assert(_writeBuffer.Count == 0);
                            byte[] data = _utf8.GetBytes(sb.ToString());
                            _writeBuffer.Add(data);
                            await _underlying.WriteAsync(_writeBuffer, cancel).ConfigureAwait(false);
                            _writeBuffer.Clear();
                        }
                        else
                        {
                            ReadUpgradeResponse();
                        }
                    }
                    else
                    {
                        throw new InvalidDataException("incomplete WebSocket request frame");
                    }
                }
                catch (WebSocketException ex)
                {
                    throw new InvalidDataException(ex.Message, ex);
                }
            }
            catch (Exception ex)
            {
                if (_communicator.TraceLevels.Network >= 2)
                {
                    _communicator.Logger.Trace(_communicator.TraceLevels.NetworkCategory,
                        $"{_transportName} connection HTTP upgrade request failed\n{this}\n{ex}");
                }
                throw;
            }

            if (_communicator.TraceLevels.Network >= 1)
            {
                if (_incoming)
                {
                    _communicator.Logger.Trace(_communicator.TraceLevels.NetworkCategory,
                        $"accepted {_transportName} connection HTTP upgrade request\n{this}");
                }
                else
                {
                    _communicator.Logger.Trace(_communicator.TraceLevels.NetworkCategory,
                        $"{_transportName} connection HTTP upgrade request accepted\n{this}");
                }
            }
        }

        public ValueTask<ArraySegment<byte>> ReadAsync(CancellationToken cancel) => throw new InvalidOperationException();

        public async ValueTask<int> ReadAsync(ArraySegment<byte> buffer, CancellationToken cancel)
        {
            int received;
            if (_readPayloadOffset == _readPayloadLength)
            {
                // If the payload has been fully read, read a new frame.
                received = await ReadFrameAsync(buffer, cancel).ConfigureAwait(false);
                Debug.Assert(_readPayloadLength > 0 && _readPayloadOffset == 0);
            }
            else if (_readBuffer.Count > 0)
            {
                // Otherwise, if there's still data buffered for the payload, consume the buffered data.
                int length = Math.Min(_readBuffer.Count, buffer.Count);
                ArraySegment<byte> buffered = await ReadBufferedAsync(length, cancel).ConfigureAwait(false);
                buffered.CopyTo(buffer);
                received = length;
            }
            else
            {
                // No buffered data, we'll read the payload directly from the underlying transport.
                received = 0;
            }

            // Read the reminder of the payload from the underlying transport
            if (received < buffer.Count)
            {
                int length = Math.Min(_readPayloadLength - _readPayloadOffset, buffer.Count);
                received += await _underlying.ReadAsync(buffer.Slice(received, length - received),
                                                        cancel).ConfigureAwait(false);
            }

            if (_incoming)
            {
                for (int i = 0; i < received; ++i)
                {
                    buffer[i] = (byte)(buffer[i] ^ _readMask[_readPayloadOffset++ % 4]);
                }
            }
            else
            {
                _readPayloadOffset += received;
            }
            return received;
        }

        public override string ToString() => _underlying.ToString()!;

        public string ToDetailedString() => _underlying.ToDetailedString();

        public ValueTask<int> WriteAsync(IList<ArraySegment<byte>> buffers, CancellationToken cancel) =>
             WriteImplAsync(OpCode.Data, buffers, cancel);

        internal
        WSTransceiver(Communicator communicator, ITransceiver del, string host, string resource) :
            this(communicator, del)
        {
            _host = host;
            _resource = resource;
            _incoming = false;
            _transportName = (del is SslTransceiver) ? "wss" : "ws";
        }

        internal WSTransceiver(Communicator communicator, ITransceiver del)
        {
            _communicator = communicator;
            _underlying = del;
            _parser = new HttpParser();
            _readBuffer = ArraySegment<byte>.Empty;
            _readLastFrame = true;
            _writeBuffer = new List<ArraySegment<byte>>();
            _writeMask = new byte[4];
            _key = "";
            _rand = new Random();
            _host = "";
            _resource = "";
            _incoming = true;
            _transportName = (del is SslTransceiver) ? "wss" : "ws";
        }

        private ArraySegment<byte> PrepareWriteHeader(OpCode opCode, int payloadLength)
        {
            // Prepare the frame header.
            byte[] buffer = new byte[16];
            int i = 0;

            // Set the opcode - this is the one and only data frame.
            buffer[i++] = (byte)((byte)opCode | FLAG_FINAL);

            //
            // Set the payload length.
            //
            if (payloadLength <= 125)
            {
                buffer[i++] = (byte)payloadLength;
            }
            else if (payloadLength > 125 && payloadLength <= 65535)
            {
                //
                // Use an extra 16 bits to encode the payload length.
                //
                buffer[i++] = 126;
                short length = System.Net.IPAddress.HostToNetworkOrder((short)payloadLength);
                MemoryMarshal.Write(buffer.AsSpan(i, 2), ref length);
                i += 2;
            }
            else if (payloadLength > 65535)
            {
                //
                // Use an extra 64 bits to encode the payload length.
                //
                buffer[i++] = 127;
                long length = System.Net.IPAddress.HostToNetworkOrder((long)payloadLength);
                MemoryMarshal.Write(buffer.AsSpan(i, 8), ref length);
                i += 8;
            }

            if (!_incoming)
            {
                //
                // Add a random 32-bit mask to every outgoing frame, copy the payload data,
                // and apply the mask.
                //
                buffer[1] = (byte)(buffer[1] | FLAG_MASKED);
                _rand.NextBytes(_writeMask);
                Buffer.BlockCopy(_writeMask, 0, buffer, i, _writeMask.Length);
                i += _writeMask.Length;
            }
            return new ArraySegment<byte>(buffer, 0, i);
        }

        private async ValueTask<ArraySegment<byte>> ReadBufferedAsync(int byteCount, CancellationToken cancel)
        {
            int offset = _readBuffer.Count;
            if (_readBuffer.Count < byteCount)
            {
                // If there's not enough data buffered for byteCount we read more data in the buffer. We first
                // need to make sure there's enough space in the buffer to read it however.
                if (_readBuffer.Count == 0)
                {
                    // Use the full buffer array if there's no more buffered data.
                    _readBuffer = new ArraySegment<byte>(_readBuffer.Array!);
                }
                else if (_readBuffer.Offset + _readBuffer.Count + byteCount > _readBuffer.Array!.Length)
                {
                    // There's still buffered data but not enough space left in the array to read the given bytes.
                    // In theory, the number of bytes to read should always be lower than the un-used buffer space
                    // at the start of the buffer. We move the data at the end of the buffer to the begining to
                    // make space to read the given number of bytes.
                    Debug.Assert(_readBuffer.Offset >= byteCount);
                    _readBuffer.CopyTo(_readBuffer.Array!, 0);
                    _readBuffer = new ArraySegment<byte>(_readBuffer.Array);
                }
                else
                {
                    // There's still buffered data and enough space to read the given bytes after the buffered
                    // data.
                    _readBuffer = new ArraySegment<byte>(
                        _readBuffer.Array,
                        _readBuffer.Offset,
                        _readBuffer.Array.Length - _readBuffer.Offset);
                }

                while (offset < byteCount)
                {
                    offset += await _underlying.ReadAsync(_readBuffer.Slice(offset), cancel);
                }
            }

            ArraySegment<byte> buffer = _readBuffer.Slice(0, byteCount);
            if (byteCount < offset)
            {
                _readBuffer = _readBuffer.Slice(byteCount, offset - byteCount);
            }
            else
            {
                _readBuffer = _readBuffer.Slice(0, 0);
            }
            return buffer;
        }

        private async ValueTask<int> ReadFrameAsync(ArraySegment<byte> buffer, CancellationToken cancel)
        {
            while (true)
            {
                // Ensure the previous payload has been totally consumed.
                Debug.Assert(_readPayloadOffset == _readPayloadLength);

                // Read the first 2 bytes of the WS frame header
                ArraySegment<byte> header = await ReadBufferedAsync(2, cancel);

                // Most-significant bit indicates if this is the last frame, least-significant four bits hold the opcode.
                var opCode = (OpCode)(header[0] & 0xf);

                // Check if the OpCode is compatible of the FIN flag of the previous frame.
                if (opCode == OpCode.Data && !_readLastFrame)
                {
                    throw new InvalidDataException("invalid WebSocket data frame, no FIN on previous frame");
                }
                else if (opCode == OpCode.Continuation && _readLastFrame)
                {
                    throw new InvalidDataException("invalid WebSocket continuation frame, previous frame FIN set");
                }

                // Remember the FIN flag of this frame for the previous check.
                _readLastFrame = (header[0] & FLAG_FINAL) == FLAG_FINAL;

                // Messages sent by a client must be masked; frames sent by a server must not be masked.
                bool masked = (header[1] & FLAG_MASKED) == FLAG_MASKED;
                if (masked != _incoming)
                {
                    throw new InvalidDataException("invalid WebSocket masking");
                }

                // Extract the payload length, which can have the following values:
                // 0-125: The payload length
                // 126:   The subsequent two bytes contain the payload length
                // 127:   The subsequent eight bytes contain the payload length
                _readPayloadLength = header[1] & 0x7f;
                if (_readPayloadLength == 126)
                {
                    header = await ReadBufferedAsync(2, cancel).ConfigureAwait(false);
                    ushort length = header.AsReadOnlySpan().ReadUShort();
                    _readPayloadLength = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)length);
                }
                else if (_readPayloadLength == 127)
                {
                    header = await ReadBufferedAsync(8, cancel).ConfigureAwait(false);
                    long length = System.Net.IPAddress.NetworkToHostOrder(header.AsReadOnlySpan().ReadLong());
                    if (length > int.MaxValue)
                    {
                        // We never send payloads with such length, we shouldn't get any.
                        throw new InvalidDataException("WebSocket payload length is not supported");
                    }
                    _readPayloadLength = (int)length;
                }
                _readPayloadOffset = 0;

                if (_incoming)
                {
                    // Read the mask if this is an incoming connection.
                    _readMask = await ReadBufferedAsync(4, cancel).ConfigureAwait(false);
                }

                if (_communicator.TraceLevels.Network >= 3)
                {
                    _communicator.Logger.Trace(_communicator.TraceLevels.NetworkCategory,
                        $"received {_transportName} {opCode} frame with {_readPayloadLength} bytes payload\n{this}");
                }

                switch (opCode)
                {
                    case OpCode.Text:
                    {
                        throw new InvalidDataException("WebSocket text frames not supported");
                    }
                    case OpCode.Data:
                    case OpCode.Continuation:
                    {
                        if (_readPayloadLength <= 0)
                        {
                            throw new InvalidDataException("WebSocket payload length is invalid");
                        }

                        // If we received more data than the header size, copy the payload data to the caller's buffer
                        int length = Math.Min(_readBuffer.Count, buffer.Count);
                        if (length > 0)
                        {
                            ArraySegment<byte> payload = await ReadBufferedAsync(length, cancel).ConfigureAwait(false);
                            payload.CopyTo(buffer);
                        }
                        return length;
                    }
                    case OpCode.Close:
                    {
                        // Read the Close frame payload.
                        ArraySegment<byte> payload = await ReadBufferedAsync(_readPayloadLength,
                                                                             cancel).ConfigureAwait(false);
                        if (_incoming)
                        {
                            for (int i = 0; i < payload.Count; ++i)
                            {
                                payload[i] = (byte)(payload[i] ^ _readMask[_readPayloadOffset++ % 4]);
                            }
                        }
                        else
                        {
                            _readPayloadOffset = 2;
                        }

                        // If we've received a close frame and we were waiting for it, notify the task. Otherwise,
                        // we didn't send a close frame and we should reply back with a close frame.
                        if (_closing)
                        {
                            throw new ConnectionLostException();
                        }
                        else
                        {
                            var writeBuffer = new List<ArraySegment<byte>> { payload };
                            await WriteImplAsync(OpCode.Close, writeBuffer, cancel).ConfigureAwait(false);
                        }
                        break;
                    }
                    case OpCode.Ping:
                    {
                        // Read the ping payload.
                        ArraySegment<byte> payload = await ReadBufferedAsync(_readPayloadLength,
                                                                             cancel).ConfigureAwait(false);
                        _readPayloadOffset = payload.Count;

                        // Send a Pong frame with the received payload.
                        var writeBuffer = new List<ArraySegment<byte>> { payload };
                        await WriteImplAsync(OpCode.Pong, writeBuffer, cancel).ConfigureAwait(false);
                        break;
                    }
                    case OpCode.Pong:
                    {
                        // Read the pong payload.
                        ArraySegment<byte> payload = await ReadBufferedAsync(_readPayloadLength,
                                                                             cancel).ConfigureAwait(false);
                        _readPayloadOffset = payload.Count;

                        // Nothing to do, this can be received even if we don't send a ping frame if the peer sends
                        // an unidirectional heartbeat.
                        break;
                    }
                    default:
                    {
                        throw new InvalidDataException($"unsupported WebSocket opcode: {opCode}");
                    }
                }
            }
        }

        private (bool, string) ReadUpgradeRequest()
        {
            // HTTP/1.1
            if (_parser.VersionMajor() != 1 || _parser.VersionMinor() != 1)
            {
                throw new WebSocketException("unsupported HTTP version");
            }

            // "An |Upgrade| header field containing the value 'websocket', treated as an ASCII case-insensitive value."
            string? value = _parser.GetHeader("Upgrade", true);
            if (value == null)
            {
                throw new WebSocketException("missing value for Upgrade field");
            }
            else if (!value.Equals("websocket"))
            {
                throw new WebSocketException($"invalid value `{value}' for Upgrade field");
            }

            // "A |Connection| header field that includes the token 'Upgrade', treated as an ASCII case-insensitive
            // value.
            value = _parser.GetHeader("Connection", true);
            if (value == null)
            {
                throw new WebSocketException("missing value for Connection field");
            }
            else if (!value.Contains("upgrade"))
            {
                throw new WebSocketException($"invalid value `{value}' for Connection field");
            }

            // "A |Sec-WebSocket-Version| header field, with a value of 13."
            value = _parser.GetHeader("Sec-WebSocket-Version", false);
            if (value == null)
            {
                throw new WebSocketException("missing value for WebSocket version");
            }
            else if (!value.Equals("13"))
            {
                throw new WebSocketException($"unsupported WebSocket version `{value}'");
            }

            // "Optionally, a |Sec-WebSocket-Protocol| header field, with a list of values indicating which protocols
            // the client would like to speak, ordered by preference."
            bool addProtocol = false;
            value = _parser.GetHeader("Sec-WebSocket-Protocol", true);
            if (value != null)
            {
                string[]? protocols = StringUtil.SplitString(value, ",");
                if (protocols == null)
                {
                    throw new WebSocketException($"invalid value `{value}' for WebSocket protocol");
                }

                foreach (string protocol in protocols)
                {
                    if (!protocol.Trim().Equals(IceProtocol))
                    {
                        throw new WebSocketException($"unknown value `{protocol}' for WebSocket protocol");
                    }
                    addProtocol = true;
                }
            }

            // "A |Sec-WebSocket-Key| header field with a base64-encoded value that, when decoded, is 16 bytes in
            // length."
            string? key = _parser.GetHeader("Sec-WebSocket-Key", false);
            if (key == null)
            {
                throw new WebSocketException("missing value for WebSocket key");
            }

            byte[] decodedKey = Convert.FromBase64String(key);
            if (decodedKey.Length != 16)
            {
                throw new WebSocketException($"invalid value `{key}' for WebSocket key");
            }

            // Retain the target resource.
            _resource = _parser.Uri();

            return (addProtocol, key);
        }

        private void ReadUpgradeResponse()
        {
            // HTTP/1.1
            if (_parser.VersionMajor() != 1 || _parser.VersionMinor() != 1)
            {
                throw new WebSocketException("unsupported HTTP version");
            }

            // "If the status code received from the server is not 101, the client handles the response per HTTP
            // [RFC2616] procedures. In particular, the client might perform authentication if it receives a 401 status
            // code; the server might redirect the client using a 3xx status code (but clients are not required to
            // follow them), etc."
            if (_parser.Status() != 101)
            {
                var sb = new StringBuilder("unexpected status value " + _parser.Status());
                if (_parser.Reason().Length > 0)
                {
                    sb.Append(":\n" + _parser.Reason());
                }
                throw new WebSocketException(sb.ToString());
            }

            // "If the response lacks an |Upgrade| header field or the |Upgrade| header field contains a value that is
            // not an ASCII case-insensitive match for the value "websocket", the client MUST_Fail the WebSocket
            // Connection_."
            string? value = _parser.GetHeader("Upgrade", true);
            if (value == null)
            {
                throw new WebSocketException("missing value for Upgrade field");
            }
            else if (!value.Equals("websocket"))
            {
                throw new WebSocketException($"invalid value `{value}' for Upgrade field");
            }

            // "If the response lacks a |Connection| header field or the |Connection| header field doesn't contain a
            // token that is an ASCII case-insensitive match for the value "Upgrade", the client MUST _Fail the
            // WebSocket Connection_."
            value = _parser.GetHeader("Connection", true);
            if (value == null)
            {
                throw new WebSocketException("missing value for Connection field");
            }
            else if (!value.Contains("upgrade"))
            {
                throw new WebSocketException($"invalid value `{value}' for Connection field");
            }

            // "If the response includes a |Sec-WebSocket-Protocol| header field and this header field indicates the
            // use of a subprotocol that was not present in the client's handshake (the server has indicated a
            // subprotocol not requested by the client), the client MUST _Fail the WebSocket Connection_."
            value = _parser.GetHeader("Sec-WebSocket-Protocol", true);
            if (value != null && !value.Equals(IceProtocol))
            {
                throw new WebSocketException($"invalid value `{value}' for WebSocket protocol");
            }

            // "If the response lacks a |Sec-WebSocket-Accept| header field or the |Sec-WebSocket-Accept| contains a
            // value other than the base64-encoded SHA-1 of the concatenation of the |Sec-WebSocket-Key| (as a string,
            // not base64-decoded) with the string "258EAFA5-E914-47DA-95CA-C5AB0DC85B11" but ignoring any leading and
            // trailing whitespace, the client MUST _Fail the WebSocket Connection_."
            value = _parser.GetHeader("Sec-WebSocket-Accept", false);
            if (value == null)
            {
                throw new WebSocketException("missing value for Sec-WebSocket-Accept");
            }

            string input = _key + WsUUID;
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms
            byte[] hash = SHA1.Create().ComputeHash(_utf8.GetBytes(input));
#pragma warning restore CA5350 // Do Not Use Weak Cryptographic Algorithms
            if (!value.Equals(Convert.ToBase64String(hash)))
            {
                throw new WebSocketException($"invalid value `{value}' for Sec-WebSocket-Accept");
            }
        }

        private async ValueTask<int> WriteImplAsync(
            OpCode opCode,
            IList<ArraySegment<byte>> buffers,
            CancellationToken cancel)
        {
            // Write can be called concurrently because it's called from both ReadAsync and WriteAsync. For example,
            // the reading of a ping frame requires writing a pong frame.
            Task<int> task;
            lock (_mutex)
            {
                ValueTask<int> writeTask = PerformWriteAsync(opCode, buffers, cancel);

                // Optimization: we check if the write completed already and avoid creating a Task if it did.
                if (writeTask.IsCompletedSuccessfully)
                {
                    _writeTask = Task.CompletedTask;
                    return writeTask.Result;
                }

                task = writeTask.AsTask();
                _writeTask = task;
            }
            return await task.ConfigureAwait(false);

            async ValueTask<int> PerformWriteAsync(
                OpCode opCode,
                IList<ArraySegment<byte>> buffers,
                CancellationToken cancel)
            {
                // Wait for the current write to be done.
                await _writeTask.ConfigureAwait(false);

                // Write the given buffer.
                Debug.Assert(_writeBuffer.Count == 0);
                int size = buffers.GetByteCount();
                _writeBuffer.Add(PrepareWriteHeader(opCode, size));
                if (_communicator.TraceLevels.Network >= 3)
                {
                    _communicator.Logger.Trace(_communicator.TraceLevels.NetworkCategory,
                        $"sending {_transportName} {opCode} frame with {size} bytes payload\n{this}");
                }

                if (_incoming || opCode == OpCode.Pong)
                {
                    foreach (ArraySegment<byte> segment in buffers)
                    {
                        _writeBuffer.Add(segment); // Borrow data from the buffer
                    }
                }
                else
                {
                    // For an outgoing connection, each frame must be masked with a random 32-bit value.
                    int n = 0;
                    foreach (ArraySegment<byte> segment in buffers)
                    {
                        byte[] data = new byte[segment.Count];
                        for (int i = 0; i < segment.Count; ++i, ++n)
                        {
                            data[i] = (byte)(segment[i] ^ _writeMask[n % 4]);
                        }
                        _writeBuffer.Add(data);
                    }
                }
                await _underlying.WriteAsync(_writeBuffer, cancel).ConfigureAwait(false);
                _writeBuffer.Clear();
                return size;
            }
        }
    }
}
