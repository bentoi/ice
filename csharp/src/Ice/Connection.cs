//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using Ice.Instrumentation;
using IceInternal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ice
{
    public delegate void CloseCallback(Connection con);
    public delegate void HeartbeatCallback(Connection con);

    public enum ACMClose
    {
        CloseOff,
        CloseOnIdle,
        CloseOnInvocation,
        CloseOnInvocationAndIdle,
        CloseOnIdleForceful
    }

    public enum ACMHeartbeat
    {
        HeartbeatOff,
        HeartbeatOnDispatch,
        HeartbeatOnIdle,
        HeartbeatAlways
    }

    [Serializable]
    public struct ACM : IEquatable<ACM>
    {
        public int Timeout;
        public ACMClose Close;
        public ACMHeartbeat Heartbeat;

        public ACM(int timeout, ACMClose close, ACMHeartbeat heartbeat)
        {
            Timeout = timeout;
            Close = close;
            Heartbeat = heartbeat;
        }

        public override int GetHashCode() => HashCode.Combine(Timeout, Close, Heartbeat);

        public bool Equals(ACM other) =>
            Timeout == other.Timeout && Close == other.Close && Heartbeat == other.Heartbeat;

        public override bool Equals(object? other) =>
            ReferenceEquals(this, other) || (other is ACM value && Equals(value));

        public static bool operator ==(ACM lhs, ACM rhs) => Equals(lhs, rhs);

        public static bool operator !=(ACM lhs, ACM rhs) => !Equals(lhs, rhs);
    }

    public enum ConnectionClose
    {
        Forcefully,
        Gracefully,
        GracefullyWithWait
    }

    public sealed class Connection : ICancellationHandler
    {
        public interface IStartCallback
        {
            void ConnectionStartCompleted(Connection connection);
            void ConnectionStartFailed(Connection connection, System.Exception ex);
        }

        private class TimeoutCallback : ITimerTask
        {
            public TimeoutCallback(Connection connection) => _connection = connection;

            public void RunTimerTask() => _connection.TimedOut();

            private readonly Connection _connection;
        }

        // TODO: refactor remove IStartCallback
        public void Start(IStartCallback? callback)
        {
            var task = StartAsync().AsTask();
            if (callback != null)
            {
                task.ContinueWith(t => {
                    try
                    {
                        t.Wait();
                        callback!.ConnectionStartCompleted(this);
                    }
                    catch (Exception ex)
                    {
                        callback!.ConnectionStartFailed(this, ex);
                    }
                }, _taskScheduler ?? TaskScheduler.Default);
            }
        }

        internal async ValueTask StartAsync()
        {
            await _transceiver.InitializeAsync().ConfigureAwait(false);

            lock (this)
            {
                if (_state >= StateClosed)
                {
                    throw _exception!;
                }

                //
                // Update the connection description once the transceiver is initialized.
                //
                _desc = _transceiver.ToString()!;
                _initialized = true;
                SetState(StateNotValidated);

                if (!_endpoint.IsDatagram) // Datagram connections are always implicitly validated.
                {
                    if (_adapter != null) // The server side has the active role for connection validation.
                    {
                        // TODO we need a better API for tracing
                        TraceUtil.TraceSend(_communicator, VectoredBufferExtensions.ToArray(_validateConnectionMessage),
                            _logger, _traceLevels);
                    }
                }
            }

            byte[]? readBuffer = null;
            if (!_endpoint.IsDatagram) // Datagram connections are always implicitly validated.
            {
                if (_adapter != null) // The server side has the active role for connection validation.
                {
                    int sentBytes = await _transceiver.WriteAsync(_validateConnectionMessage).ConfigureAwait(false);
                    Debug.Assert(sentBytes == _validateConnectionMessage.GetByteCount());
                }
                else // The client side has the passive role for connection validation.
                {
                    readBuffer = new byte[256];
                    int receivedBytes = await _transceiver.ReadAsync(
                        new ArraySegment<byte>(readBuffer!, 0, Ice1Definitions.HeaderSize)).ConfigureAwait(false);
                    Debug.Assert(receivedBytes == Ice1Definitions.HeaderSize);

                    Ice1Definitions.CheckHeader(readBuffer.AsSpan(0, 8));
                    var messageType = (Ice1Definitions.MessageType)readBuffer[8];
                    if (messageType != Ice1Definitions.MessageType.ValidateConnectionMessage)
                    {
                        throw new InvalidDataException(@$"received ice1 frame with message type `{messageType
                            }' before receiving the validate connection message");
                    }

                    int size = InputStream.ReadInt(readBuffer.AsSpan(10, 4));
                    if (size != Ice1Definitions.HeaderSize)
                    {
                        throw new InvalidDataException(
                            $"received an ice1 frame with validate connection type and a size of `{size}' bytes");
                    }
                }
            }

            lock (this)
            {
                if (_state >= StateClosed)
                {
                    throw _exception!;
                }

                if (!_endpoint.IsDatagram) // Datagram connections are always implicitly validated.
                {
                    if (_adapter != null) // The server side has the active role for connection validation.
                    {
                        TraceSentAndUpdateObserver(_validateConnectionMessage.GetByteCount());
                    }
                    else
                    {
                        TraceReceivedAndUpdateObserver(readBuffer!.Length);
                        TraceUtil.TraceRecv(new InputStream(_communicator, readBuffer!), _logger, _traceLevels);
                    }
                }

                if (_communicator.TraceLevels.Network >= 1)
                {
                    var s = new StringBuilder();
                    if (_endpoint.IsDatagram)
                    {
                        s.Append("starting to ");
                        s.Append(_connector != null ? "send" : "receive");
                        s.Append(" ");
                        s.Append(_endpoint.Name);
                        s.Append(" messages\n");
                        s.Append(_transceiver.ToDetailedString());
                    }
                    else
                    {
                        s.Append(_connector != null ? "established" : "accepted");
                        s.Append(" ");
                        s.Append(_endpoint.Name);
                        s.Append(" connection\n");
                        s.Append(ToString());
                    }
                    _logger.Trace(_communicator.TraceLevels.NetworkCat, s.ToString());
                }

                if (_acmLastActivity > -1)
                {
                    _acmLastActivity = Time.CurrentMonotonicTimeMillis();
                }
                if (_adapter == null)
                {
                    _validated = true;
                }
                SetState(StateActive);
            }
        }

        internal void Destroy(Exception ex)
        {
            lock (this)
            {
                SetState(StateClosing, ex);
            }
        }

        /// <summary>Manually close the connection using the specified closure mode.</summary>
        /// <param name="mode">Determines how the connection will be closed.</param>
        public void Close(ConnectionClose mode)
        {
            lock (this)
            {
                if (mode == ConnectionClose.Forcefully)
                {
                    SetState(StateClosed, new ConnectionClosedLocallyException("connection closed forcefully"));
                }
                else if (mode == ConnectionClose.Gracefully)
                {
                    SetState(StateClosing, new ConnectionClosedLocallyException("connection closed gracefully"));
                }
                else
                {
                    Debug.Assert(mode == ConnectionClose.GracefullyWithWait);

                    //
                    // Wait until all outstanding requests have been completed.
                    //
                    while (_asyncRequests.Count != 0)
                    {
                        System.Threading.Monitor.Wait(this);
                    }

                    SetState(StateClosing, new ConnectionClosedLocallyException("connection closed gracefully"));
                }
            }
        }

        internal bool Active
        {
            get
            {
                lock (this)
                {
                    return _state > StateNotValidated && _state < StateClosing;
                }
            }
        }

        /// <summary>
        /// Throw an exception indicating the reason for connection closure.
        /// For example,
        /// ConnectionClosedByPeerException is raised if the connection was closed gracefully by the peer,
        /// whereas ConnectionClosedLocallyException is raised if the connection was
        /// manually closed by the application. This operation does nothing if the connection is
        /// not yet closed.
        /// </summary>
        public void ThrowException()
        {
            lock (this)
            {
                if (_exception != null)
                {
                    Debug.Assert(_state >= StateClosing);
                    throw _exception;
                }
            }
        }

        internal void WaitUntilFinished()
        {
            lock (this)
            {
                //
                // We wait indefinitely until the connection is finished and all
                // outstanding requests are completed. Otherwise we couldn't
                // guarantee that there are no outstanding calls when deactivate()
                // is called on the servant locators.
                //
                while (_state < StateFinished || _dispatchCount > 0)
                {
                    System.Threading.Monitor.Wait(this);
                }

                Debug.Assert(_state == StateFinished);

                //
                // Clear the OA. See bug 1673 for the details of why this is necessary.
                //
                _adapter = null;
            }
        }

        internal void UpdateObserver()
        {
            lock (this)
            {
                if (_state < StateNotValidated || _state > StateClosed)
                {
                    return;
                }

                _communicatorObserver = _communicator.Observer!;
                _observer = _communicatorObserver.GetConnectionObserver(InitConnectionInfo(), _endpoint,
                    ToConnectionState(_state), _observer);
                if (_observer != null)
                {
                    _observer.Attach();
                }
            }
        }

        internal void Monitor(long now, ACMConfig acm)
        {
            lock (this)
            {
                if (_state != StateActive)
                {
                    return;
                }

                //
                // We send a heartbeat if there was no activity in the last
                // (timeout / 4) period. Sending a heartbeat sooner than
                // really needed is safer to ensure that the receiver will
                // receive the heartbeat in time. Sending the heartbeat if
                // there was no activity in the last (timeout / 2) period
                // isn't enough since monitor() is called only every (timeout
                // / 2) period.
                //
                // Note that this doesn't imply that we are sending 4 heartbeats
                // per timeout period because the monitor() method is still only
                // called every (timeout / 2) period.
                //
                if (acm.Heartbeat == ACMHeartbeat.HeartbeatAlways ||
                   (acm.Heartbeat != ACMHeartbeat.HeartbeatOff && now >= (_acmLastActivity + (acm.Timeout / 4))))
                {
                    if (acm.Heartbeat != ACMHeartbeat.HeartbeatOnDispatch || _dispatchCount > 0)
                    {
                        SendHeartbeatNow();
                    }
                }

                if (acm.Close != ACMClose.CloseOff && now >= (_acmLastActivity + acm.Timeout))
                {
                    if (acm.Close == ACMClose.CloseOnIdleForceful ||
                       (acm.Close != ACMClose.CloseOnIdle && (_asyncRequests.Count > 0)))
                    {
                        //
                        // Close the connection if we didn't receive a heartbeat in
                        // the last period.
                        //
                        SetState(StateClosed, new ConnectionTimeoutException());
                    }
                    else if (acm.Close != ACMClose.CloseOnInvocation &&
                            _dispatchCount == 0 && _asyncRequests.Count == 0)
                    {
                        //
                        // The connection is idle, close it.
                        //
                        SetState(StateClosing, new ConnectionIdleException());
                    }
                }
            }
        }

        internal int SendAsyncRequest(OutgoingAsyncBase outgoing, bool compress, bool response)
        {
            lock (this)
            {
                //
                // If the exception is thrown before we even have a chance
                // to send our request, we always try to send the request
                // again.
                //
                if (_exception != null)
                {
                    throw new RetryException(_exception);
                }

                Debug.Assert(_state > StateNotValidated);
                Debug.Assert(_state < StateClosing);

                //
                // Notify the request that it's cancelable with this connection.
                // This will throw if the request is canceled.
                //
                outgoing.Cancelable(this);
                int requestId = 0;
                if (response)
                {
                    //
                    // Create a new unique request ID.
                    //
                    requestId = _nextRequestId++;
                    if (requestId <= 0)
                    {
                        _nextRequestId = 1;
                        requestId = _nextRequestId++;
                    }
                }

                List<ArraySegment<byte>> data = outgoing.GetRequestData(requestId);
                int size = data.GetByteCount();
                // Ensure the message isn't bigger than what we can send with the
                // transport.
                _transceiver.CheckSendSize(size);

                outgoing.AttachRemoteObserver(InitConnectionInfo(), _endpoint, requestId,
                    size - (Ice1Definitions.HeaderSize + 4));

                int status = OutgoingAsyncBase.AsyncStatusQueued;
                try
                {
                    status = SendMessage(new OutgoingMessage(outgoing, data, compress, requestId));
                }
                catch (Exception ex)
                {
                    SetState(StateClosed, ex);
                    Debug.Assert(_exception != null);
                    throw _exception;
                }

                if (response)
                {
                    //
                    // Add to the asynchronous requests map.
                    //
                    _asyncRequests[requestId] = outgoing;
                }
                return status;
            }
        }

        /// <summary>
        /// Set a close callback on the connection.
        /// The callback is called by the
        /// connection when it's closed. The callback is called from the
        /// Ice thread pool associated with the connection. If the callback needs
        /// more information about the closure, it can call Connection.throwException.
        ///
        /// </summary>
        /// <param name="callback">The close callback object.</param>
        public void SetCloseCallback(CloseCallback callback)
        {
            lock (this)
            {
                if (_state >= StateClosed)
                {
                    if (callback != null)
                    {
                        RunTask(() => {
                            try
                            {
                                callback(this);
                            }
                            catch (Exception ex)
                            {
                                _logger.Error($"connection callback exception:\n{ex}\n{_desc}");
                            }
                        });
                    }
                }
                else
                {
                    _closeCallback = callback;
                }
            }
        }

        /// <summary>
        /// Set a heartbeat callback on the connection.
        /// The callback is called by the
        /// connection when a heartbeat is received. The callback is called
        /// from the Ice thread pool associated with the connection.
        ///
        /// </summary>
        /// <param name="callback">The heartbeat callback object.</param>
        public void SetHeartbeatCallback(HeartbeatCallback callback)
        {
            lock (this)
            {
                if (_state >= StateClosed)
                {
                    return;
                }
                _heartbeatCallback = callback;
            }
        }

        /// <summary>
        /// Send a heartbeat message.
        /// </summary>
        public void Heartbeat() => HeartbeatAsync().Wait();

        private class HeartbeatTaskCompletionCallback : TaskCompletionCallback<object>
        {
            public HeartbeatTaskCompletionCallback(IProgress<bool>? progress,
                                                   CancellationToken cancellationToken) :
                base(progress, cancellationToken)
            {
            }

            public override void HandleInvokeResponse(bool ok, OutgoingAsyncBase og) => SetResult(null!);
        }

        private class HeartbeatOutgoingAsync : OutgoingAsyncBase
        {
            public HeartbeatOutgoingAsync(Connection connection,
                                          Communicator communicator,
                                          IOutgoingAsyncCompletionCallback completionCallback) :
                base(communicator, completionCallback) => _connection = connection;

            public override List<ArraySegment<byte>> GetRequestData(int requestId) => _validateConnectionMessage;

            public void Invoke()
            {
                try
                {
                    int status = _connection.SendAsyncRequest(this, false, false);

                    if ((status & AsyncStatusSent) != 0)
                    {
                        SentSynchronously = true;
                        if ((status & AsyncStatusInvokeSentCallback) != 0)
                        {
                            InvokeSent();
                        }
                    }
                }
                catch (RetryException ex)
                {
                    if (Exception(ex.InnerException!))
                    {
                        InvokeExceptionAsync();
                    }
                }
                catch (Exception ex)
                {
                    if (Exception(ex))
                    {
                        InvokeExceptionAsync();
                    }
                }
            }

            private readonly Connection _connection;
        }

        public Task HeartbeatAsync(IProgress<bool>? progress = null, CancellationToken cancel = new CancellationToken())
        {
            var completed = new HeartbeatTaskCompletionCallback(progress, cancel);
            var outgoing = new HeartbeatOutgoingAsync(this, _communicator, completed);
            outgoing.Invoke();
            return completed.Task;
        }

        /// <summary>
        /// Set the active connection management parameters.
        /// </summary>
        /// <param name="timeout">The timeout value in seconds, must be &gt;= 0.
        ///
        /// </param>
        /// <param name="close">The close condition
        ///
        /// </param>
        /// <param name="heartbeat">The heartbeat condition</param>
        public void SetACM(int? timeout, ACMClose? close, ACMHeartbeat? heartbeat)
        {
            lock (this)
            {
                if (timeout is int timeoutValue && timeoutValue < 0)
                {
                    throw new ArgumentException("invalid negative ACM timeout value", nameof(timeout));
                }

                if (_monitor == null || _state >= StateClosed)
                {
                    return;
                }

                if (_state == StateActive)
                {
                    _monitor.Remove(this);
                }
                _monitor = _monitor.Acm(timeout, close, heartbeat);

                if (_monitor.GetACM().Timeout <= 0)
                {
                    _acmLastActivity = -1; // Disable the recording of last activity.
                }
                else if (_state == StateActive && _acmLastActivity == -1)
                {
                    _acmLastActivity = Time.CurrentMonotonicTimeMillis();
                }

                if (_state == StateActive)
                {
                    _monitor.Add(this);
                }
            }
        }

        /// <summary>
        /// Get the ACM parameters.
        /// </summary>
        /// <returns>The ACM parameters.</returns>
        public ACM GetACM()
        {
            lock (this)
            {
                return _monitor != null ? _monitor.GetACM() : new ACM(0, ACMClose.CloseOff, ACMHeartbeat.HeartbeatOff);
            }
        }

        public void AsyncRequestCanceled(OutgoingAsyncBase outAsync, System.Exception ex)
        {
            //
            // NOTE: This isn't called from a thread pool thread.
            //

            lock (this)
            {
                if (_state >= StateClosed)
                {
                    return; // The request has already been or will be shortly notified of the failure.
                }

                OutgoingMessage? o = _outgoingMessages.FirstOrDefault(m => m.OutAsync == outAsync);
                if (o != null)
                {
                    if (o.RequestId > 0)
                    {
                        _asyncRequests.Remove(o.RequestId);
                    }

                    //
                    // If the request is being sent, don't remove it from the send streams,
                    // it will be removed once the sending is finished.
                    //
                    if (o == _outgoingMessages.First!.Value)
                    {
                        o.Canceled();
                    }
                    else
                    {
                        o.Canceled();
                        _outgoingMessages.Remove(o);
                    }
                    if (outAsync.Exception(ex))
                    {
                        outAsync.InvokeExceptionAsync();
                    }
                    return;
                }

                if (outAsync is OutgoingAsync)
                {
                    foreach (KeyValuePair<int, OutgoingAsyncBase> kvp in _asyncRequests)
                    {
                        if (kvp.Value == outAsync)
                        {
                            _asyncRequests.Remove(kvp.Key);
                            if (outAsync.Exception(ex))
                            {
                                outAsync.InvokeExceptionAsync();
                            }
                            return;
                        }
                    }
                }
            }
        }

        internal IConnector Connector
        {
            get
            {
                Debug.Assert(_connector != null);
                return _connector; // No mutex protection necessary, _connector is immutable.
            }
        }

        /// <summary>Explicitly sets an object adapter that dispatches requests received over this connection.
        /// A client can invoke an operation on a server using a proxy, and then set an object adapter for the
        /// outgoing connection used by the proxy in order to receive callbacks. This is useful if the server
        /// cannot establish a connection back to the client, for example because of firewalls.</summary>
        /// <param name="adapter">The object adapter. This object adapter is automatically removed from the
        /// connection when it is deactivated.</param>.
        public void SetAdapter(ObjectAdapter? adapter)
        {
            if (adapter != null)
            {
                // We're locking both the object adapter and this connection (in this order) to ensure the adapter
                // gets cleared from this connection during the deactivation of the object adapter.
                adapter.ExecuteOnlyWhenActive(() =>
                    {
                        lock (this)
                        {
                            _adapter = adapter;
                        }
                    });
            }
            else
            {
                lock (this)
                {
                    if (_state <= StateNotValidated || _state >= StateClosing)
                    {
                        return;
                    }
                    _adapter = null;
                }
            }

            // We never change the thread pool with which we were initially registered, even if we add or remove an
            // object adapter.
        }

        /// <summary>
        /// Get the object adapter that dispatches requests for this
        /// connection.
        /// </summary>
        /// <returns>The object adapter that dispatches requests for the
        /// connection, or null if no adapter is set.</returns>
        ///
        public ObjectAdapter? GetAdapter()
        {
            lock (this)
            {
                return _adapter;
            }
        }

        /// <summary>
        /// Get the endpoint from which the connection was created.
        /// </summary>
        /// <returns>The endpoint from which the connection was created.</returns>
        public Endpoint Endpoint => _endpoint; // No mutex protection necessary, _endpoint is immutable.

        /// <summary>Creates a special "fixed" proxy that always uses this connection. This proxy can be used for
        /// callbacks from a server to a client if the server cannot directly establish a connection to the client,
        /// for example because of firewalls. In this case, the server would create a proxy using an already
        /// established connection from the client.</summary>
        /// <param name="identity">The identity for which a proxy is to be created.</param>
        /// <param name="factory">The proxy factory. Use INamePrx.Factory, where INamePrx is the desired proxy type.
        /// </param>
        /// <returns>A proxy that matches the given identity and uses this connection.</returns>
        public T CreateProxy<T>(Identity identity, ProxyFactory<T> factory) where T : class, IObjectPrx
            => factory(_communicator.CreateReference(identity, this));

        internal void SetAdapterImpl(ObjectAdapter adapter)
        {
            lock (this)
            {
                if (_state <= StateNotValidated || _state >= StateClosing)
                {
                    return;
                }
                _adapter = adapter;
            }
        }

        public void TimedOut()
        {
            lock (this)
            {
                if (_state <= StateNotValidated)
                {
                    SetState(StateClosed, new ConnectTimeoutException());
                }
                else if (_state < StateClosed)
                {
                    SetState(StateClosed, new ConnectionTimeoutException());
                }
            }
        }

        /// <summary>
        /// Return the connection type.
        /// This corresponds to the endpoint
        /// type, i.e., "tcp", "udp", etc.
        ///
        /// </summary>
        /// <returns>The type of the connection.</returns>
        public string Type() => _type; // No mutex lock, _type is immutable.

        /// <summary>
        /// Get the timeout for the connection.
        /// </summary>
        /// <returns>The connection's timeout.</returns>
        public int Timeout => _endpoint.Timeout; // No mutex protection necessary, _endpoint is immutable.

        /// <summary>
        /// Returns the connection information.
        /// </summary>
        /// <returns>The connection information.</returns>
        public ConnectionInfo GetConnectionInfo()
        {
            lock (this)
            {
                if (_state >= StateClosed)
                {
                    throw _exception!;
                }
                return InitConnectionInfo();
            }
        }

        /// <summary>
        /// Set the connection buffer receive/send size.
        /// </summary>
        /// <param name="rcvSize">The connection receive buffer size.
        /// </param>
        /// <param name="sndSize">The connection send buffer size.</param>
        public void SetBufferSize(int rcvSize, int sndSize)
        {
            lock (this)
            {
                if (_state >= StateClosed)
                {
                    throw _exception!;
                }
                _transceiver.SetBufferSize(rcvSize, sndSize);
                _info = null; // Invalidate the cached connection info
            }
        }

        /// <summary>
        /// Return a description of the connection as human readable text,
        /// suitable for logging or error messages.
        /// </summary>
        /// <returns>The description of the connection as human readable
        /// text.</returns>
        public override string ToString() => _desc; // No mutex lock, _desc is immutable.

        internal Connection(Communicator communicator,
                            IACMMonitor? monitor,
                            ITransceiver transceiver,
                            IConnector? connector,
                            Endpoint endpoint,
                            ObjectAdapter? adapter)
        {
            _communicator = communicator;
            _monitor = monitor;
            _transceiver = transceiver;
            _desc = transceiver.ToString()!;
            _type = transceiver.Transport();
            _connector = connector;
            _endpoint = endpoint;
            _adapter = adapter;
            _communicatorObserver = communicator.Observer;
            _logger = communicator.Logger; // Cached for better performance.
            _traceLevels = communicator.TraceLevels; // Cached for better performance.
            _warn = communicator.GetPropertyAsInt("Ice.Warn.Connections") > 0;
            _warnUdp = communicator.GetPropertyAsInt("Ice.Warn.Datagrams") > 0;

            if (_monitor != null && _monitor.GetACM().Timeout > 0)
            {
                _acmLastActivity = Time.CurrentMonotonicTimeMillis();
            }
            else
            {
                _acmLastActivity = -1;
            }
            _nextRequestId = 1;
            _messageSizeMax = adapter != null ? adapter.MessageSizeMax : communicator.MessageSizeMax;
            _dispatchCount = 0;
            _pendingIO = 0;
            _state = StateNotInitialized;

            _compressionLevel = communicator.GetPropertyAsInt("Ice.Compression.Level") ?? 1;
            if (_compressionLevel < 1)
            {
                _compressionLevel = 1;
            }
            else if (_compressionLevel > 9)
            {
                _compressionLevel = 9;
            }

            _taskScheduler = adapter != null ? adapter.TaskScheduler : communicator.TaskScheduler;
        }

        private async ValueTask Read()
        {
            while (true)
            {
                var readBuffer = new ArraySegment<byte>(new byte[256], 0, Ice1Definitions.HeaderSize);
                await _transceiver.ReadAsync(readBuffer).ConfigureAwait(false);

                // TODO: XXX: datagrams
                // if (_readBufferOffset < Ice1Definitions.HeaderSize)
                // {
                //     //
                //     // This situation is possible for small UDP packets.
                //     //
                //     throw new InvalidDataException($"received packet with only {_readBufferOffset} bytes");
                // }

                Ice1Definitions.CheckHeader(readBuffer.AsSpan(0, 8));
                int size = InputStream.ReadInt(readBuffer.Slice(10, 4));
                if (size < Ice1Definitions.HeaderSize)
                {
                    throw new InvalidDataException($"received ice1 frame with only {size} bytes");
                }

                if (size > _messageSizeMax)
                {
                    throw new InvalidDataException($"frame with {size} bytes exceeds Ice.MessageSizeMax value");
                }

                // TODO: XXX: datagrams
                // if (_endpoint.IsDatagram && size > _readBufferOffset)
                // {
                //     if (_warnUdp)
                //     {
                //         _logger.Warning($"maximum datagram size of {_readBufferOffset} exceeded");
                //     }
                //     _readBuffer = ArraySegment<byte>.Empty;
                //     _readBufferOffset = 0;
                //     _readHeader = true;
                //     return;
                // }

                lock (this)
                {
                    if (_state >= StateClosed)
                    {
                        return;
                    }

                    TraceReceivedAndUpdateObserver(readBuffer.Count);

                    if (_acmLastActivity > -1)
                    {
                        _acmLastActivity = Time.CurrentMonotonicTimeMillis();
                    }
                }

                if (size > readBuffer.Array!.Length)
                {
                    // Allocate a new array and copy the header over
                    var buffer = new ArraySegment<byte>(new byte[size], 0, size);
                    readBuffer.AsSpan().CopyTo(buffer.AsSpan(0, Ice1Definitions.HeaderSize));
                }
                else if (size > readBuffer.Count)
                {
                    readBuffer = new ArraySegment<byte>(readBuffer.Array!, 0, size);
                }
                Debug.Assert(size == readBuffer.Array!.Length);

                // Read the reminder of the message
                int offset = Ice1Definitions.HeaderSize;
                while (offset < size)
                {
                    int bytesReceived = await _transceiver.ReadAsync(readBuffer, offset).ConfigureAwait(false);
                    lock (this)
                    {
                        if (_state >= StateClosed)
                        {
                            return;
                        }

                        TraceReceivedAndUpdateObserver(bytesReceived);

                        if (_acmLastActivity > -1)
                        {
                            _acmLastActivity = Time.CurrentMonotonicTimeMillis();
                        }
                    }
                    offset += bytesReceived;
                }

                Current? current = null;
                IncomingRequestFrame? requestFrame = null;
                OutgoingAsyncBase? outAsyncSent = null;
                OutgoingAsyncBase? outAsyncResponse = null;
                HeartbeatCallback? heartbeatCallback = null;
                bool closing = false;
                byte compressionStatus;

                lock (this)
                {
                    if (_state >= StateClosed)
                    {
                        return;
                    }

                    // The magic and version fields have already been checked.
                    var messageType = (Ice1Definitions.MessageType)readBuffer[8];
                    compressionStatus = readBuffer[9];
                    if (compressionStatus == 2)
                    {
                        if (BZip2.IsLoaded)
                        {
                            readBuffer = BZip2.Decompress(readBuffer, Ice1Definitions.HeaderSize, _messageSizeMax);
                        }
                        else
                        {
                            throw new LoadException("compression not supported, bzip2 library not found");
                        }
                    }

                    switch (messageType)
                    {
                        case Ice1Definitions.MessageType.CloseConnectionMessage:
                        {
                            TraceUtil.TraceRecv(new InputStream(_communicator, readBuffer), _logger, _traceLevels);
                            if (_endpoint.IsDatagram)
                            {
                                if (_warn)
                                {
                                    _logger.Warning(
                                        $"ignoring close connection message for datagram connection:\n{_desc}");
                                }
                            }
                            else
                            {
                                SetState(StateClosingPending, new ConnectionClosedByPeerException());
                                closing = true;
                            }
                            break;
                        }

                        case Ice1Definitions.MessageType.RequestMessage:
                        {
                            if (_state >= StateClosing)
                            {
                                TraceUtil.Trace("received request during closing\n" +
                                                "(ignored by server, client will retry)",
                                                new InputStream(_communicator, readBuffer),
                                                _logger, _traceLevels);
                            }
                            else
                            {
                                TraceUtil.TraceRecv(new InputStream(_communicator, readBuffer), _logger, _traceLevels);
                                readBuffer = readBuffer.Slice(Ice1Definitions.HeaderSize);
                                int requestId = InputStream.ReadInt(readBuffer.AsSpan(0, 4));
                                requestFrame = new IncomingRequestFrame(_adapter!.Communicator, readBuffer.Slice(4));
                                current = new Current(_adapter!, requestFrame, requestId, this);
                                ++_dispatchCount;
                            }
                            break;
                        }

                        case Ice1Definitions.MessageType.RequestBatchMessage:
                        {
                            if (_state >= StateClosing)
                            {
                                TraceUtil.Trace("received batch request during closing\n" +
                                                "(ignored by server, client will retry)",
                                                new InputStream(_communicator, readBuffer),
                                                _logger, _traceLevels);
                            }
                            else
                            {
                                TraceUtil.TraceRecv(new InputStream(_communicator, readBuffer), _logger, _traceLevels);
                                int invokeNum = InputStream.ReadInt(readBuffer.AsSpan(Ice1Definitions.HeaderSize, 4));
                                if (invokeNum < 0)
                                {
                                    throw new InvalidDataException(
                                        $"received ice1 RequestBatchMessage with {invokeNum} batch requests");
                                }
                                Debug.Assert(false); // TODO: deal with batch requests
                            }
                            break;
                        }

                        case Ice1Definitions.MessageType.ReplyMessage:
                        {
                            TraceUtil.TraceRecv(new InputStream(_communicator, readBuffer), _logger, _traceLevels);
                            readBuffer = readBuffer.Slice(Ice1Definitions.HeaderSize);
                            int requestId = InputStream.ReadInt(readBuffer.AsSpan(0, 4));
                            if (_asyncRequests.TryGetValue(requestId, out OutgoingAsyncBase? outAsync))
                            {
                                _asyncRequests.Remove(requestId);

                                //
                                // If we just received the reply for a request which isn't acknowledge as
                                // sent yet, we queue the reply instead of processing it right away. It
                                // will be processed once the write callback is invoked for the message.
                                //
                                var response = new IncomingResponseFrame(_communicator, readBuffer.Slice(4));
                                OutgoingMessage? message = _outgoingMessages.First?.Value;
                                if (message != null && message.OutAsync == outAsync)
                                {
                                    if (message.Sent())
                                    {
                                        outAsyncSent = outAsync;
                                    }
                                }

                                if (outAsync.Response(response))
                                {
                                    outAsyncResponse = outAsync;
                                }

                                if (outAsyncSent != null || outAsyncResponse != null)
                                {
                                    ++_dispatchCount;
                                }
                                System.Threading.Monitor.PulseAll(this); // Notify threads blocked in close()
                            }
                            break;
                        }

                        case Ice1Definitions.MessageType.ValidateConnectionMessage:
                        {
                            TraceUtil.TraceRecv(new InputStream(_communicator, readBuffer), _logger, _traceLevels);
                            if (_heartbeatCallback != null)
                            {
                                heartbeatCallback = _heartbeatCallback;
                                ++_dispatchCount;
                            }
                            break;
                        }

                        default:
                        {
                            TraceUtil.Trace("received unknown message\n(invalid, closing connection)",
                                            new InputStream(_communicator, readBuffer), _logger, _traceLevels);
                            throw new InvalidDataException(
                                $"received ice1 frame with unknown message type `{messageType}'");
                        }
                    }
                }

                if (closing)
                {
                    // Wait for the transceiver closing. We're done once it returns.
                    await _transceiver.ClosingAsync(false, _exception).ConfigureAwait(false);
                    lock (this)
                    {
                        SetState(StateClosed);
                    }
                    return;
                }
                else if (_taskScheduler != null)
                {
                    // Dispatch on the configured task scheduler and continue reading.
                    RunTask(async () =>
                        await Dispatch(current, requestFrame, compressionStatus, outAsyncSent, outAsyncResponse,
                            heartbeatCallback));
                }
                // TODO: serialize support
                // else if (_serialize)
                // {
                //     // Dispatch on this thread and continue reading once the dispatch is done.
                //     await Dispatch(current, requestFrame, outAsync, heartbeatCallback);
                // }
                else
                {
                    // Start a read on another thread and dispatch on this thread, we're done
                    // with this thread once the dipatch completes.
                    StartIO(Read);
                    await Dispatch(current, requestFrame, compressionStatus, outAsyncSent, outAsyncResponse,
                        heartbeatCallback);
                    return;
                }
            }
        }

        // TODO: Consider adding RequestFrame and CompressionStatus to Ice.Current (could be internal)?
        private async ValueTask Dispatch(Current? current, IncomingRequestFrame? requestFrame, byte compressionStatus,
            OutgoingAsyncBase? outAsyncSent, OutgoingAsyncBase? outAsyncResponse, HeartbeatCallback? heartbeatCallback)
        {
            if (current != null)
            {
                await InvokeAsync(current, requestFrame!, compressionStatus).ConfigureAwait(false);
            }

            if (outAsyncSent != null)
            {
                outAsyncSent.InvokeSent();
            }
            if (outAsyncResponse != null)
            {
                outAsyncResponse.InvokeResponse();
            }

            if (heartbeatCallback != null)
            {
                try
                {
                    heartbeatCallback(this);
                }
                catch (Exception ex)
                {
                    _logger.Error($"connection callback exception:\n{ex}\n{_desc}");
                }
            }

            lock (this)
            {
                if (--_dispatchCount == 0)
                {
                    //
                    // Only initiate shutdown if not already done. It
                    // might have already been done if the sent callback
                    // or AMI callback was dispatched when the connection
                    // was already in the closing state.
                    //
                    if (_state == StateClosing)
                    {
                        try
                        {
                            InitiateShutdown();
                        }
                        catch (Exception ex)
                        {
                            SetState(StateClosed, ex);
                        }
                    }
                    else if (_state == StateFinished)
                    {
                        Reap();
                    }
                    System.Threading.Monitor.PulseAll(this);
                }
            }
        }

        private async Task InvokeAsync(Current current, IncomingRequestFrame requestFrame, byte compressionStatus)
        {
            IDispatchObserver? dispatchObserver = null;

            try
            {
                // Then notify and set dispatch observer, if any.
                ICommunicatorObserver? communicatorObserver = current.Adapter.Communicator.Observer;
                if (communicatorObserver != null)
                {
                    dispatchObserver = communicatorObserver.GetDispatchObserver(current, requestFrame!.Size);
                    dispatchObserver?.Attach();
                }

                OutgoingResponseFrame? response = null;
                try
                {
                    IObject? servant = current.Adapter.Find(current.Identity, current.Facet);
                    if (servant == null)
                    {
                        throw new ObjectNotExistException(current.Identity, current.Facet, current.Operation);
                    }

                    ValueTask<OutgoingResponseFrame> vt = servant.DispatchAsync(requestFrame!, current);
                    if (current.RequestId != 0)
                    {
                        response = await vt.ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    if (current.RequestId != 0)
                    {
                        RemoteException actualEx;
                        if (ex is RemoteException remoteEx && !remoteEx.ConvertToUnhandled)
                        {
                            actualEx = remoteEx;
                        }
                        else
                        {
                            actualEx = new UnhandledException(current.Identity, current.Facet, current.Operation,
                                ex);
                        }
                        Incoming.ReportException(actualEx, dispatchObserver, current);
                        response = new OutgoingResponseFrame(current, actualEx);
                    }
                }

                if (current.RequestId == 0)
                {
                    SendNoResponse();
                }
                else
                {
                    Debug.Assert(response != null);
                    dispatchObserver?.Reply(response.Size);
                    SendResponse(response, current.RequestId, compressionStatus);
                }
            }
            catch (Exception ex)
            {
                lock (this)
                {
                    SetState(StateClosed, ex);
                }
            }
            finally
            {
                dispatchObserver?.Detach();
            }
        }
        private void Finish()
        {
            if (!_initialized)
            {
                if (_communicator.TraceLevels.Network >= 2)
                {
                    var s = new StringBuilder("failed to ");
                    s.Append(_connector != null ? "establish" : "accept");
                    s.Append(" ");
                    s.Append(_endpoint.Name);
                    s.Append(" connection\n");
                    s.Append(ToString());
                    s.Append("\n");
                    s.Append(_exception);
                    _logger.Trace(_communicator.TraceLevels.NetworkCat, s.ToString());
                }
            }
            else if (_communicator.TraceLevels.Network >= 1)
            {
                var s = new StringBuilder("closed ");
                s.Append(_endpoint.Name);
                s.Append(" connection\n");
                s.Append(ToString());

                //
                // Trace the cause of unexpected connection closures
                //
                if (!(_exception is ConnectionClosedException ||
                      _exception is ConnectionIdleException ||
                      _exception is CommunicatorDestroyedException ||
                      _exception is ObjectAdapterDeactivatedException))
                {
                    s.Append("\n");
                    s.Append(_exception);
                }

                _logger.Trace(_communicator.TraceLevels.NetworkCat, s.ToString());
            }

            if (_outgoingMessages.Count > 0)
            {
                foreach (OutgoingMessage o in _outgoingMessages)
                {
                    o.Completed(_exception!);
                    if (o.RequestId > 0) // Make sure finished isn't called twice.
                    {
                        _asyncRequests.Remove(o.RequestId);
                    }
                }
                _outgoingMessages.Clear(); // Must be cleared before _requests because of Outgoing* references in OutgoingMessage
            }

            foreach (OutgoingAsyncBase o in _asyncRequests.Values)
            {
                if (o.Exception(_exception!))
                {
                    o.InvokeException();
                }
            }
            _asyncRequests.Clear();

            if (_closeCallback != null)
            {
                try
                {
                    _closeCallback(this);
                }
                catch (Exception ex)
                {
                    _logger.Error($"connection callback exception:\n{ex}\n{_desc}");
                }
                _closeCallback = null;
            }

            _heartbeatCallback = null;

            //
            // This must be done last as this will cause waitUntilFinished() to return (and communicator
            // objects such as the timer might be destroyed too).
            //
            lock (this)
            {
                SetState(StateFinished);

                if (_dispatchCount == 0)
                {
                    Reap();
                }
            }
        }

        private void SendResponse(OutgoingResponseFrame response, int requestId, byte compressionStatus)
        {
            lock (this)
            {
                Debug.Assert(_state > StateNotValidated);

                try
                {
                    if (--_dispatchCount == 0)
                    {
                        if (_state == StateFinished)
                        {
                            Reap();
                        }
                        System.Threading.Monitor.PulseAll(this);
                    }

                    if (_state >= StateClosed)
                    {
                        Debug.Assert(_exception != null);
                        throw _exception;
                    }

                    SendMessage(new OutgoingMessage(response, compressionStatus > 0, requestId));

                    if (_state == StateClosing && _dispatchCount == 0)
                    {
                        InitiateShutdown();
                    }
                }
                catch (Exception ex)
                {
                    SetState(StateClosed, ex);
                }
            }
        }

        private void SendNoResponse()
        {
            lock (this)
            {
                Debug.Assert(_state > StateNotValidated);

                try
                {
                    if (--_dispatchCount == 0)
                    {
                        if (_state == StateFinished)
                        {
                            Reap();
                        }
                        System.Threading.Monitor.PulseAll(this);
                    }

                    if (_state >= StateClosed)
                    {
                        Debug.Assert(_exception != null);
                        throw _exception;
                    }

                    if (_state == StateClosing && _dispatchCount == 0)
                    {
                        InitiateShutdown();
                    }
                }
                catch (System.Exception ex)
                {
                    SetState(StateClosed, ex);
                }
            }
        }

        private void SetState(int state, System.Exception ex)
        {
            //
            // If setState() is called with an exception, then only closed
            // and closing states are permissible.
            //
            Debug.Assert(state >= StateClosing);

            if (_state == state) // Don't switch twice.
            {
                return;
            }

            if (_exception == null)
            {
                //
                // If we are in closed state, an exception must be set.
                //
                Debug.Assert(_state != StateClosed);

                _exception = ex;

                //
                // We don't warn if we are not validated.
                //
                if (_warn && _validated)
                {
                    //
                    // Don't warn about certain expected exceptions.
                    //
                    if (!(_exception is ConnectionClosedException ||
                         _exception is ConnectionIdleException ||
                         _exception is CommunicatorDestroyedException ||
                         _exception is ObjectAdapterDeactivatedException ||
                         (_exception is ConnectionLostException && _state >= StateClosing)))
                    {
                        Warning("connection exception", _exception);
                    }
                }
            }

            //
            // We must set the new state before we notify requests of any
            // exceptions. Otherwise new requests may retry on a
            // connection that is not yet marked as closed or closing.
            //
            SetState(state);
        }

        private void SetState(int state)
        {
            //
            // We don't want to send close connection messages if the endpoint
            // only supports oneway transmission from client to server.
            //
            if (_endpoint.IsDatagram && state == StateClosing)
            {
                state = StateClosed;
            }

            //
            // Skip graceful shutdown if we are destroyed before validation.
            //
            if (_state <= StateNotValidated && state == StateClosing)
            {
                state = StateClosed;
            }

            if (_state == state) // Don't switch twice.
            {
                return;
            }

            try
            {
                switch (state)
                {
                    case StateNotInitialized:
                    {
                        Debug.Assert(false);
                        break;
                    }

                    case StateNotValidated:
                    {
                        if (_state != StateNotInitialized)
                        {
                            Debug.Assert(_state == StateClosed);
                            return;
                        }
                        break;
                    }

                    case StateActive:
                    {
                        // Can only switch from validated to active.
                        if (_state != StateNotValidated)
                        {
                            return;
                        }
                        StartIO(Read);
                        break;
                    }

                    case StateClosing:
                    case StateClosingPending:
                    {
                        // Can't change back from closing pending.
                        if (_state >= StateClosingPending)
                        {
                            return;
                        }
                        break;
                    }

                    case StateClosed:
                    {
                        if (_state == StateFinished)
                        {
                            return;
                        }
                        _transceiver.Close();
                        break;
                    }

                    case StateFinished:
                    {
                        Debug.Assert(_state == StateClosed);
                        _transceiver.Destroy();
                        break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logger.Error("unexpected connection exception:\n" + ex + "\n" + _transceiver.ToString());
            }

            //
            // We only register with the connection monitor if our new state
            // is StateActive. Otherwise we unregister with the connection
            // monitor, but only if we were registered before, i.e., if our
            // old state was StateActive.
            //
            if (_monitor != null)
            {
                if (state == StateActive)
                {
                    if (_acmLastActivity > -1)
                    {
                        _acmLastActivity = Time.CurrentMonotonicTimeMillis();
                    }
                    _monitor.Add(this);
                }
                else if (_state == StateActive)
                {
                    _monitor.Remove(this);
                }
            }

            if (_communicatorObserver != null)
            {
                ConnectionState oldState = ToConnectionState(_state);
                ConnectionState newState = ToConnectionState(state);
                if (oldState != newState)
                {
                    _observer = _communicatorObserver.GetConnectionObserver(InitConnectionInfo(), _endpoint,
                        newState, _observer);
                    if (_observer != null)
                    {
                        _observer.Attach();
                    }
                }
                if (_observer != null && state == StateClosed && _exception != null)
                {
                    if (!(_exception is ConnectionClosedException ||
                         _exception is ConnectionIdleException ||
                         _exception is CommunicatorDestroyedException ||
                         _exception is ObjectAdapterDeactivatedException ||
                         (_exception is ConnectionLostException && _state >= StateClosing)))
                    {
                        _observer.Failed(_exception.GetType().FullName!);
                    }
                }
            }
            _state = state;

            System.Threading.Monitor.PulseAll(this);
            if (_state == StateClosing && _dispatchCount == 0)
            {
                try
                {
                    InitiateShutdown();
                }
                catch (Exception ex)
                {
                    SetState(StateClosed, ex);
                }
            }
        }

        private void InitiateShutdown()
        {
            Debug.Assert(_state == StateClosing && _dispatchCount == 0);

            if (_shutdownInitiated)
            {
                return;
            }
            _shutdownInitiated = true;

            if (!_endpoint.IsDatagram)
            {
                //
                // Before we shut down, we send a close connection message.
                //
                if ((SendMessage(new OutgoingMessage(_closeConnectionMessage, false)) &
                    OutgoingAsyncBase.AsyncStatusSent) != 0)
                {
                    // TODO: XXX: SendMessage always returns Queued for now , this will need fixing
                    // once it allows sync writes.
                    Debug.Assert(false);
                    // SetState(StateClosingPending);

                    // //
                    // // Notify the transceiver of the graceful connection closure.
                    // //
                    // int op = _transceiver.Closing(true, _exception);
                    // if (op != 0)
                    // {
                    //     ScheduleTimeout(op);
                    //     ThreadPool.Register(this, op);
                    // }
                }
            }
        }

        private void SendHeartbeatNow()
        {
            Debug.Assert(_state == StateActive);

            if (!_endpoint.IsDatagram)
            {
                try
                {
                    SendMessage(new OutgoingMessage(_validateConnectionMessage, false));
                }
                catch (System.Exception ex)
                {
                    SetState(StateClosed, ex);
                    Debug.Assert(_exception != null);
                }
            }
        }
        private async ValueTask Write()
        {
            while (true)
            {
                bool closing = false;
                OutgoingMessage? message = null;
                int size = 0;
                List<ArraySegment<byte>>? writeBuffer = null;
                lock (this)
                {
                    if (_outgoingMessages.Count == 0)
                    {
                        // If all the messages were sent and we are in the closing state, we schedule
                        // the close timeout to wait for the peer to close the connection.
                        if (_state == StateClosing && _shutdownInitiated)
                        {
                            SetState(StateClosingPending);
                            closing = true;
                        }
                    }
                    else if (_state < StateClosingPending)
                    {
                        // Otherwise, prepare the next message for writing. The message isn't removed
                        // from the send queue because the connection needs to figure out which message
                        // is being sent in case the response is received before the WriteAsync returns.
                        // This is required to guarantee that the sent progress notification is called
                        // before the response is returned.
                        // TODO: Benoit: while it was useful with the callback API to guarantee such
                        // ordering, is it still useful with the async based API now? Could the user
                        // rely in the progress callback to be called before the ContinueWith
                        // continuation?
                        message = _outgoingMessages.First!.Value;
                        writeBuffer = DoCompress(message.OutgoingData!, message.OutgoingData!.GetByteCount(),
                            message.Compress);
                        size = writeBuffer.GetByteCount();
                        TraceUtil.TraceSend(_communicator, message.OutgoingData!.GetSegment(0, size).Array!, _logger,
                            _traceLevels);
                    }
                }

                if (message == null)
                {
                    if (closing)
                    {
                        await _transceiver.ClosingAsync(true, _exception);
                    }
                    // If there's no more messages to write, we're done.
                    return;
                }

                int offset = 0;
                OutgoingAsyncBase? outAsync = null;
                while (offset < size)
                {
                    int bytesSent = await _transceiver.WriteAsync(writeBuffer!, offset).ConfigureAwait(false);
                    lock (this)
                    {
                        // If we finished sending the message, remove it from the send queue and dispatch
                        // the progress notification or response if it was already received.
                        if (offset == size)
                        {
                            if (message.Sent())
                            {
                                outAsync = message.OutAsync;
                                ++_dispatchCount;
                            }
                            _outgoingMessages.RemoveFirst();
                        }

                        if (_state < StateClosed)
                        {
                            TraceSentAndUpdateObserver(bytesSent);

                            if (_acmLastActivity > -1)
                            {
                                _acmLastActivity = Time.CurrentMonotonicTimeMillis();
                            }
                        }
                    }
                    offset += bytesSent;
                }

                if (outAsync != null)
                {
                    RunTask(async () => await Dispatch(null, null, 0, outAsync, null, null));
                }
            }
        }

        private int SendMessage(OutgoingMessage message)
        {
            Debug.Assert(_state < StateClosed);
            // TODO: XXX: Refactor to write and await the calling thread to avoid having writing
            // on a thread pool thread
            _outgoingMessages.AddLast(message);
            if (_outgoingMessages.Count == 1)
            {
                StartIO(Write);
            }
            return OutgoingAsyncBase.AsyncStatusQueued;
        }

        private List<ArraySegment<byte>> DoCompress(List<ArraySegment<byte>> data, int size, bool compress)
        {
            if (BZip2.IsLoaded && compress && size >= 100)
            {
                List<ArraySegment<byte>>? compressedData =
                    BZip2.Compress(data, size, Ice1Definitions.HeaderSize, _compressionLevel);
                if (compressedData != null)
                {
                    return compressedData;
                }
            }

            ArraySegment<byte> header = data[0];
            // Write the compression status and the message size.
            header[9] = (byte)(BZip2.IsLoaded && compress ? 1 : 0);
            return data;
        }

        private struct MessageInfo
        {
            public ArraySegment<byte> Data;
            public int InvokeNum;
            public int RequestId;
            public byte Compress;
            public ObjectAdapter? Adapter;
            public OutgoingAsyncBase? OutAsync;
            public HeartbeatCallback HeartbeatCallback;
            public int MessageDispatchCount;
        }

        private ConnectionInfo InitConnectionInfo()
        {
            if (_state > StateNotInitialized && _info != null) // Update the connection info until it's initialized
            {
                return _info;
            }

            try
            {
                _info = _transceiver.GetInfo();
            }
            catch (System.Exception)
            {
                _info = new ConnectionInfo();
            }
            for (ConnectionInfo? info = _info; info != null; info = info.Underlying)
            {
                info.ConnectionId = _endpoint.ConnectionId;
                info.AdapterName = _adapter != null ? _adapter.Name : "";
                info.Incoming = _connector == null;
            }
            return _info;
        }

        private void Reap()
        {
            if (_monitor != null)
            {
                _monitor.Reap(this);
            }
            if (_observer != null)
            {
                _observer.Detach();
            }
        }

        private void RunTask(Action action)
        {
            // Use the configured task scheduler to run the task. DenyChildAttach is the default for Task.Run,
            // we use the same here.
            Task.Factory.StartNew(action, default, TaskCreationOptions.DenyChildAttach,
                _taskScheduler ?? TaskScheduler.Default);
        }

        private void StartIO(Func<ValueTask> operation)
        {
            RunTask(async () =>
            {
                lock (this)
                {
                    if (_state >= StateClosed)
                    {
                        return;
                    }
                    ++_pendingIO;
                }
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lock (this)
                    {
                        SetState(StateClosed, ex);
                    }
                }
                finally
                {
                    bool finish = false;
                    lock (this)
                    {
                        finish = --_pendingIO == 0 && _state >= StateClosed;
                    }
                    if (finish)
                    {
                        // No more pending IO and closed, it's time to terminate the connection
                        Finish();
                    }
                }
            });
        }

        private ConnectionState ToConnectionState(int state) => _connectionStateMap[state];

        private void Warning(string msg, System.Exception ex) => _logger.Warning($"{msg}:\n{ex}\n{_transceiver}");

        private void TraceSentAndUpdateObserver(int length)
        {
            if (_communicator.TraceLevels.Network >= 3 && length > 0)
            {
                var s = new StringBuilder("sent ");
                s.Append(length);
                s.Append(" bytes via ");
                s.Append(_endpoint.Name);
                s.Append("\n");
                s.Append(ToString());
                _logger.Trace(_communicator.TraceLevels.NetworkCat, s.ToString());
            }

            if (_observer != null && length > 0)
            {
                _observer.SentBytes(length);
            }
        }

        private void TraceReceivedAndUpdateObserver(int length)
        {
            if (_communicator.TraceLevels.Network >= 3 && length > 0)
            {
                var s = new StringBuilder("received ");
                s.Append(length);
                s.Append(" bytes via ");
                s.Append(_endpoint.Name);
                s.Append("\n");
                s.Append(ToString());
                _logger.Trace(_communicator.TraceLevels.NetworkCat, s.ToString());
            }

            if (_observer != null && length > 0)
            {
                _observer.ReceivedBytes(length);
            }
        }

        private class OutgoingMessage
        {
            internal OutgoingMessage(List<ArraySegment<byte>> requestData, bool compress)
            {
                OutgoingData = requestData;
                Compress = compress;
            }

            internal OutgoingMessage(OutgoingAsyncBase outgoing, List<ArraySegment<byte>> data, bool compress, int requestId)
            {
                OutAsync = outgoing;
                OutgoingData = data;
                Compress = compress;
                RequestId = requestId;
            }

            internal OutgoingMessage(OutgoingResponseFrame frame, bool compress, int requestId)
            {
                OutgoingData = Ice1Definitions.GetResponseData(frame, requestId);
                Compress = compress;
            }

            internal void Canceled()
            {
                Debug.Assert(OutAsync != null); // Only requests can timeout.
                OutAsync = null;
            }

            internal bool Sent()
            {
                OutgoingData = null;
                if (OutAsync != null && !InvokeSent)
                {
                    InvokeSent = OutAsync.Sent();
                    return InvokeSent;
                }
                return false;
            }

            internal void Completed(Exception ex)
            {
                if (OutAsync != null)
                {
                    if (OutAsync.Exception(ex))
                    {
                        OutAsync.InvokeException();
                    }
                }
                OutgoingData = null;
            }

            internal List<ArraySegment<byte>>? OutgoingData;
            internal TaskCompletionSource<bool>? WaitSent;  // The bool is ignored
            internal OutgoingAsyncBase? OutAsync;
            internal bool Compress;
            internal int RequestId;
            internal bool InvokeSent;
        }

        private const int StateNotInitialized = 0;
        private const int StateNotValidated = 1;
        private const int StateActive = 2;
        private const int StateClosing = 3;
        private const int StateClosingPending = 4;
        private const int StateClosed = 5;
        private const int StateFinished = 6;

        private readonly Communicator _communicator;
        private IACMMonitor? _monitor;
        private readonly ITransceiver _transceiver;
        private string _desc;
        private readonly string _type;
        private readonly IConnector? _connector;
        private readonly Endpoint _endpoint;

        private ObjectAdapter? _adapter;

        private readonly ILogger _logger;
        private readonly TraceLevels _traceLevels;
        private readonly bool _warn;
        private readonly bool _warnUdp;
        private long _acmLastActivity;

        private readonly int _compressionLevel;

        private int _nextRequestId;

        private readonly Dictionary<int, OutgoingAsyncBase> _asyncRequests = new Dictionary<int, OutgoingAsyncBase>();

        private System.Exception? _exception;

        private readonly int _messageSizeMax;

        private readonly LinkedList<OutgoingMessage> _outgoingMessages = new LinkedList<OutgoingMessage>();
        private int _pendingIO;

        private ICommunicatorObserver? _communicatorObserver;
        private IConnectionObserver? _observer;

        private int _dispatchCount;

        private int _state; // The current state.
        private bool _shutdownInitiated = false;
        private readonly TaskScheduler? _taskScheduler;
        private bool _initialized = false;
        private bool _validated = false;

        private ConnectionInfo? _info;

        private CloseCallback? _closeCallback;
        private HeartbeatCallback? _heartbeatCallback;

        private static readonly ConnectionState[] _connectionStateMap = new ConnectionState[] {
            ConnectionState.ConnectionStateValidating,   // StateNotInitialized
            ConnectionState.ConnectionStateValidating,   // StateNotValidated
            ConnectionState.ConnectionStateActive,       // StateActive
            ConnectionState.ConnectionStateClosing,      // StateClosing
            ConnectionState.ConnectionStateClosing,      // StateClosingPending
            ConnectionState.ConnectionStateClosed,       // StateClosed
            ConnectionState.ConnectionStateClosed,       // StateFinished
        };

        private static readonly List<ArraySegment<byte>> _validateConnectionMessage =
            new List<ArraySegment<byte>> { Ice1Definitions.ValidateConnectionMessage };
        private static readonly List<ArraySegment<byte>> _closeConnectionMessage =
            new List<ArraySegment<byte>> { Ice1Definitions.CloseConnectionMessage };
    }
}
