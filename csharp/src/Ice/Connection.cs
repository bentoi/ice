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
        public async ValueTask StartAsync()
        {
            await _transceiver.InitializeAsync().ConfigureAwait(false);

            lock (this)
            {
                if (_state >= State.Closed)
                {
                    throw _exception!;
                }

                //
                // Update the connection description once the transceiver is initialized.
                //
                _desc = _transceiver.ToString()!;
                SetState(State.NotValidated);

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

            ArraySegment<byte> readBuffer = default;
            if (!_endpoint.IsDatagram) // Datagram connections are always implicitly validated.
            {
                if (_adapter != null) // The server side has the active role for connection validation.
                {
                    int sentBytes = await _transceiver.WriteAsync(_validateConnectionMessage).ConfigureAwait(false);
                    Debug.Assert(sentBytes == _validateConnectionMessage.GetByteCount());
                }
                else // The client side has the passive role for connection validation.
                {
                    readBuffer = new ArraySegment<byte>(new byte[Ice1Definitions.HeaderSize]);
                    int receivedBytes = await _transceiver.ReadAsync(readBuffer!).ConfigureAwait(false);
                    Debug.Assert(receivedBytes == Ice1Definitions.HeaderSize);

                    Ice1Definitions.CheckHeader(readBuffer!.AsSpan(0, 8));
                    var messageType = (Ice1Definitions.MessageType)readBuffer![8];
                    if (messageType != Ice1Definitions.MessageType.ValidateConnectionMessage)
                    {
                        throw new InvalidDataException(@$"received ice1 frame with message type `{messageType
                            }' before receiving the validate connection message");
                    }

                    int size = InputStream.ReadInt(readBuffer!.AsSpan(10, 4));
                    if (size != Ice1Definitions.HeaderSize)
                    {
                        throw new InvalidDataException(
                            $"received an ice1 frame with validate connection type and a size of `{size}' bytes");
                    }
                }
            }

            lock (this)
            {
                if (_state >= State.Closed)
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
                        TraceReceivedAndUpdateObserver(readBuffer.Count);
                        TraceUtil.TraceRecv(_communicator, readBuffer, _logger, _traceLevels);
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

                SetState(State.Active);
            }

            Debug.Assert ((_taskScheduler ?? TaskScheduler.Default) == TaskScheduler.Current);
            _ = RunIO(Read);
        }

        internal void Destroy(Exception ex)
        {
            lock (this)
            {
                SetState(State.Closing, ex);
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
                    SetState(State.Closed, new ConnectionClosedLocallyException("connection closed forcefully"));
                }
                else if (mode == ConnectionClose.Gracefully)
                {
                    SetState(State.Closing, new ConnectionClosedLocallyException("connection closed gracefully"));
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

                    SetState(State.Closing, new ConnectionClosedLocallyException("connection closed gracefully"));
                }
            }
        }

        internal bool Active
        {
            get
            {
                lock (this)
                {
                    return _state > State.NotValidated && _state < State.Closing;
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
                    Debug.Assert(_state >= State.Closing);
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
                while (_state < State.Finished || _dispatchCount > 0)
                {
                    System.Threading.Monitor.Wait(this);
                }

                Debug.Assert(_state == State.Finished);

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
                if (_state < State.NotValidated || _state > State.Closed)
                {
                    return;
                }

                _communicatorObserver = _communicator.Observer!;
                _observer = _communicatorObserver.GetConnectionObserver(InitConnectionInfo(), _endpoint,
                    _connectionStateMap[(int)_state], _observer);
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
                if (_state != State.Active)
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
                        SetState(State.Closed, new ConnectionTimeoutException());
                    }
                    else if (acm.Close != ACMClose.CloseOnInvocation &&
                            _dispatchCount == 0 && _asyncRequests.Count == 0)
                    {
                        //
                        // The connection is idle, close it.
                        //
                        SetState(State.Closing, new ConnectionIdleException());
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

                Debug.Assert(_state > State.NotValidated);
                Debug.Assert(_state < State.Closing);

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
                    SetState(State.Closed, ex);
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
        public void SetCloseCallback(Action<Connection> callback)
        {
            lock (this)
            {
                if (_state >= State.Closed)
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
        public void SetHeartbeatCallback(Action<Connection> callback)
        {
            lock (this)
            {
                if (_state >= State.Closed)
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

                if (_monitor == null || _state >= State.Closed)
                {
                    return;
                }

                if (_state == State.Active)
                {
                    _monitor.Remove(this);
                }
                _monitor = _monitor.Acm(timeout, close, heartbeat);

                if (_monitor.GetACM().Timeout <= 0)
                {
                    _acmLastActivity = -1; // Disable the recording of last activity.
                }
                else if (_state == State.Active && _acmLastActivity == -1)
                {
                    _acmLastActivity = Time.CurrentMonotonicTimeMillis();
                }

                if (_state == State.Active)
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
                if (_state >= State.Closed)
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
                    if (_state <= State.NotValidated || _state >= State.Closing)
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
                if (_state <= State.NotValidated || _state >= State.Closing)
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
                if (_state <= State.NotValidated)
                {
                    SetState(State.Closed, new ConnectTimeoutException());
                }
                else if (_state < State.Closed)
                {
                    SetState(State.Closed, new ConnectionTimeoutException());
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
                if (_state >= State.Closed)
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
                if (_state >= State.Closed)
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
            _state = State.NotInitialized;

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

            bool serialized = false; // TODO: per connection configuration?
            if (serialized)
            {
                Read = SerializedReadAndDispatch;
            }
            else
            {
                Read = ConcurrentReadAndDispatch;
            }
        }

        private struct IncomingMessage : IDisposable
        {
            public IncomingMessage(Connection connection, Current current, IncomingRequestFrame request,
                byte compressionStatus)
            {
                Connection = connection;
                IncomingRequest = (current, request, compressionStatus);
                OutgoingRequestSent = null;
                OutgoingRequestResponse = null;
                Heartbeat = null;
            }
            public IncomingMessage(Connection connection, OutgoingAsyncBase? sent, OutgoingAsyncBase? response)
            {
                Connection = connection;
                IncomingRequest = null;
                OutgoingRequestSent = sent;
                OutgoingRequestResponse = response;
                Heartbeat = null;
            }
            public IncomingMessage(Connection connection, Action<Connection> heartbeat)
            {
                Connection = connection;
                IncomingRequest = null;
                OutgoingRequestSent = null;
                OutgoingRequestResponse = null;
                Heartbeat = heartbeat;
            }

            public void Dispose()
            {
                Connection.DisposeIncomingMessage();
            }

            public Connection Connection;
            public ValueTuple<Current, IncomingRequestFrame, byte>? IncomingRequest;
            public OutgoingAsyncBase? OutgoingRequestSent;
            public OutgoingAsyncBase? OutgoingRequestResponse;
            public Action<Connection>? Heartbeat;
        };

        private async ValueTask<IncomingMessage?> ReadMessage()
        {
            var readBuffer = _endpoint.IsDatagram ?
                ArraySegment<byte>.Empty : new ArraySegment<byte>(new byte[256], 0, Ice1Definitions.HeaderSize);
            int offset = 0;
            while (offset < Ice1Definitions.HeaderSize)
            {
                int bytesReceived = await _transceiver.ReadAsync(readBuffer, offset).ConfigureAwait(false);
                lock (this)
                {
                    if (_state >= State.ClosingPending)
                    {
                        Debug.Assert(_exception != null);
                        throw _exception;
                    }

                    TraceReceivedAndUpdateObserver(bytesReceived);

                    if (_acmLastActivity > -1)
                    {
                        _acmLastActivity = Time.CurrentMonotonicTimeMillis();
                    }
                }
                offset += bytesReceived;
            }

            if (offset < Ice1Definitions.HeaderSize)
            {
                //
                // This situation is possible for small UDP packets.
                //
                throw new InvalidDataException($"received packet with only {offset} bytes");
            }

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

            if (_endpoint.IsDatagram && size > offset)
            {
                if (_warnUdp)
                {
                    _logger.Warning($"maximum datagram size of {offset} exceeded");
                }
                return null;
            }

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

            // Read the reminder of the message
            Debug.Assert(offset == Ice1Definitions.HeaderSize);
            while (offset < size)
            {
                int bytesReceived = await _transceiver.ReadAsync(readBuffer, offset).ConfigureAwait(false);
                lock (this)
                {
                    if (_state >= State.ClosingPending)
                    {
                        Debug.Assert(_exception != null);
                        throw _exception;
                    }

                    TraceReceivedAndUpdateObserver(bytesReceived);

                    if (_acmLastActivity > -1)
                    {
                        _acmLastActivity = Time.CurrentMonotonicTimeMillis();
                    }
                }
                offset += bytesReceived;
            }

            IncomingMessage? incomingMessage = null;

            lock (this)
            {
                if (_state >= State.Closed)
                {
                    Debug.Assert(_exception != null);
                    throw _exception;
                }

                // The magic and version fields have already been checked.
                var messageType = (Ice1Definitions.MessageType)readBuffer[8];
                byte compressionStatus = readBuffer[9];
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
                        TraceUtil.TraceRecv(_communicator, readBuffer, _logger, _traceLevels);
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
                            if (_state == State.ClosingPending)
                            {
                                SetState(State.Closed);
                            }
                            else
                            {
                                SetState(State.ClosingPending, new ConnectionClosedByPeerException());
                            }
                            Debug.Assert(_exception != null);
                            throw _exception;
                        }
                        break;
                    }

                    case Ice1Definitions.MessageType.RequestMessage:
                    {
                        if (_state >= State.Closing)
                        {
                            TraceUtil.Trace("received request during closing\n" +
                                            "(ignored by server, client will retry)",
                                            new InputStream(_communicator, readBuffer),
                                            _logger, _traceLevels);
                        }
                        else
                        {
                            TraceUtil.TraceRecv(_communicator, readBuffer, _logger, _traceLevels);
                            readBuffer = readBuffer.Slice(Ice1Definitions.HeaderSize);
                            int requestId = InputStream.ReadInt(readBuffer.AsSpan(0, 4));
                            var request = new IncomingRequestFrame(_adapter!.Communicator, readBuffer.Slice(4));
                            var current = new Current(_adapter!, request, requestId, this);
                            incomingMessage = new IncomingMessage(this, current, request, compressionStatus);
                        }
                        break;
                    }

                    case Ice1Definitions.MessageType.RequestBatchMessage:
                    {
                        if (_state >= State.Closing)
                        {
                            TraceUtil.Trace("received batch request during closing\n" +
                                            "(ignored by server, client will retry)",
                                            new InputStream(_communicator, readBuffer),
                                            _logger, _traceLevels);
                        }
                        else
                        {
                            TraceUtil.TraceRecv(_communicator, readBuffer, _logger, _traceLevels);
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
                        TraceUtil.TraceRecv(_communicator, readBuffer, _logger, _traceLevels);
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
                            OutgoingMessage? outgoingMessage = _outgoingMessages.First?.Value;
                            OutgoingAsyncBase? outAsyncSent = null;
                            OutgoingAsyncBase? outAsyncResponse = null;
                            if (outgoingMessage != null && outgoingMessage.OutAsync == outAsync)
                            {
                                if (outgoingMessage.Sent())
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
                                incomingMessage = new IncomingMessage(this, outAsyncSent, outAsyncResponse);
                            }

                            if (_asyncRequests.Count == 0)
                            {
                                System.Threading.Monitor.PulseAll(this); // Notify threads blocked in close()
                            }
                        }
                        break;
                    }

                    case Ice1Definitions.MessageType.ValidateConnectionMessage:
                    {
                        TraceUtil.TraceRecv(_communicator, readBuffer, _logger, _traceLevels);
                        if (_heartbeatCallback != null)
                        {
                            incomingMessage = new IncomingMessage(this, _heartbeatCallback);
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

                if (incomingMessage != null)
                {
                    ++_dispatchCount;
                }
            }

            return incomingMessage;
        }

        private async ValueTask<OutgoingMessage?> DispatchMessage(IncomingMessage incomingMessage)
        {
            if (incomingMessage.IncomingRequest != null)
            {
                var (current, request, compressionStatus) = incomingMessage.IncomingRequest ?? default;
                return await InvokeAsync(current, request, compressionStatus).ConfigureAwait(false);
            }
            else if (incomingMessage.Heartbeat != null)
            {
                try
                {
                    incomingMessage.Heartbeat(this);
                }
                catch (Exception ex)
                {
                    _logger.Error($"connection callback exception:\n{ex}\n{_desc}");
                }
                return null;
            }
            else
            {
                incomingMessage.OutgoingRequestSent?.InvokeSent();
                incomingMessage.OutgoingRequestResponse?.InvokeResponse();
                return null;
            }
        }

        private void DisposeIncomingMessage()
        {
            // We can't decrement the dispatch count right after the dispatch because the close connection
            // message must be sent once all the responses have been sent. This is why IncomingMessage is
            // Disposable, to decrement the dispatch count and eventually initiate the shutdown.
            lock (this)
            {
                Debug.Assert(_dispatchCount > 0);
                if (--_dispatchCount == 0)
                {
                    if (_state == State.Closing)
                    {
                        try
                        {
                            InitiateShutdown();
                        }
                        catch (Exception ex)
                        {
                            SetState(State.Closed, ex);
                        }
                    }
                    else if (_state == State.Finished)
                    {
                        Reap();
                    }
                    System.Threading.Monitor.PulseAll(this);
                }
            }
        }

        private async ValueTask<OutgoingMessage?> InvokeAsync(Current current, IncomingRequestFrame requestFrame,
            byte compressionStatus)
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

                if (current.RequestId != 0)
                {
                    dispatchObserver?.Reply(response!.Size);
                    return new OutgoingMessage(response!, compressionStatus > 0, current.RequestId);
                }
                else
                {
                    return null;
                }
            }
            finally
            {
                dispatchObserver?.Detach();
            }
        }

        private async ValueTask SerializedReadAndDispatch()
        {
            // The serialized read and dispatch read method waits for the message to be dispatched to
            // continue reading on the connection
            while (true)
            {
                using IncomingMessage? incomingMessage = await ReadMessage();
                if (incomingMessage != null)
                {
                    OutgoingMessage? outgoingMessage = await DispatchMessage(incomingMessage!.Value);
                    if (outgoingMessage != null)
                    {
                        // TODO: await on SendMessage when SendMessage is async. What is the value of calling
                        // SendMessage here and not directly from DispatchMessage?
                        SendMessage(outgoingMessage);
                    }
                }
            }
        }

        private async ValueTask ConcurrentReadAndDispatch()
        {
            // The concurrent read and dispatch doesn't wait for the message to be dispatched to start
            // reading a new message. We prefer to the dispatch the message from the continuation of
            // ReadMessage to ensure there's no addtional thread context switch between the Read and
            // Dispatch. We start a new asynchronous Read IO before dispatching.
            using IncomingMessage? incomingMessage = await ReadMessage();
            _ = RunIO(Read);
            if (incomingMessage != null)
            {
                OutgoingMessage? outgoingMessage = await DispatchMessage(incomingMessage!.Value);
                if (outgoingMessage != null)
                {
                    // TODO: await on SendMessage when SendMessage is async. What is the value of calling
                    // SendMessage here and not directly from DispatchMessage?
                    SendMessage(outgoingMessage);
                }
            }
        }

        private Func<ValueTask> Read { get; }

        private void Finish()
        {
            if (_outgoingMessages.Count > 0)
            {
                foreach (OutgoingMessage o in _outgoingMessages)
                {
                    o.Completed(_exception!);
                    if (o.RequestId > 0) // Make sure Completed isn't called twice.
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

            try
            {
                _closeCallback?.Invoke(this);
            }
            catch (Exception ex)
            {
                _logger.Error($"connection callback exception:\n{ex}\n{_desc}");
            }

            _closeCallback = null;
            _heartbeatCallback = null;

            //
            // This must be done last as this will cause waitUntilFinished() to return (and communicator
            // objects such as the timer might be destroyed too).
            //
            lock (this)
            {
                SetState(State.Finished);
            }
        }

        private void SetState(State state, System.Exception ex)
        {
            //
            // If setState() is called with an exception, then only closed
            // and closing State.s are permissible.
            //
            Debug.Assert(state >= State.Closing);

            if (_state == state) // Don't switch twice.
            {
                return;
            }

            if (_exception == null)
            {
                //
                // If we are in closed state, an exception must be set.
                //
                Debug.Assert(_state != State.Closed);

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
                         (_exception is ConnectionLostException && _state >= State.Closing)))
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

        private void SetState(State state)
        {
            //
            // We don't want to send close connection messages if the endpoint
            // only supports oneway transmission from client to server.
            //
            if (_endpoint.IsDatagram && state == State.Closing)
            {
                state = State.Closed;
            }

            //
            // Skip graceful shutdown if we are destroyed before validation.
            //
            if (_state <= State.NotValidated && state == State.Closing)
            {
                state = State.Closed;
            }

            if (_state == state) // Don't switch twice.
            {
                return;
            }

            try
            {
                switch (state)
                {
                    case State.NotInitialized:
                    {
                        Debug.Assert(false);
                        break;
                    }

                    case State.NotValidated:
                    {
                        Debug.Assert (_state == State.NotInitialized);
                        break;
                    }

                    case State.Active:
                    {
                        Debug.Assert(_state == State.NotValidated);
                        break;
                    }

                    case State.Closing:
                    case State.ClosingPending:
                    {
                        // Can't change back from closing pending.
                        if (_state >= State.ClosingPending)
                        {
                            return;
                        }
                        break;
                    }

                    case State.Closed:
                    {
                        Debug.Assert(_state < State.Closed);
                        // Close the transceiver, this should cause pending IO async calls to return.
                        _transceiver.Close();
                        break;
                    }

                    case State.Finished:
                    {
                        Debug.Assert(_state == State.NotInitialized || _state == State.Closed);
                        if (_communicator.TraceLevels.Network >= 1)
                        {
                            var s = new StringBuilder();
                            if (_state > State.NotInitialized)
                            {
                                s.Append("closed ");
                            }
                            else if (_connector != null)
                            {
                                s.Append("failed to establish ");
                            }
                            else
                            {

                                s.Append("failed to accept ");
                            }
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

                        _transceiver.Destroy();

                        if (_dispatchCount == 0)
                        {
                            Reap();
                        }
                        break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logger.Error("unexpected connection exception:\n" + ex + "\n" + _transceiver.ToString());
            }

            // We only register with the connection monitor if our new state
            // is State.Active. Otherwise we unregister with the connection
            // monitor, but only if we were registered before, i.e., if our
            // old state was State.Active.
            // TODO: Benoit: we should probably keep using ACM while in the
            // validation/closing states now that we no longer have timeouts.
            if (_monitor != null)
            {
                if (state == State.Active)
                {
                    if (_acmLastActivity > -1)
                    {
                        _acmLastActivity = Time.CurrentMonotonicTimeMillis();
                    }
                    _monitor.Add(this);
                }
                else if (_state == State.Active)
                {
                    _monitor.Remove(this);
                }
            }

            if (_communicatorObserver != null)
            {
                ConnectionState oldState = _connectionStateMap[(int)_state];
                ConnectionState newState = _connectionStateMap[(int)state];
                if (oldState != newState)
                {
                    _observer = _communicatorObserver.GetConnectionObserver(InitConnectionInfo(), _endpoint,
                        newState, _observer);
                    if (_observer != null)
                    {
                        _observer.Attach();
                    }
                }
                if (_observer != null && state == State.Closed && _exception != null)
                {
                    if (!(_exception is ConnectionClosedException ||
                         _exception is ConnectionIdleException ||
                         _exception is CommunicatorDestroyedException ||
                         _exception is ObjectAdapterDeactivatedException ||
                         (_exception is ConnectionLostException && _state >= State.Closing)))
                    {
                        _observer.Failed(_exception.GetType().FullName!);
                    }
                }
            }
            _state = state;

            System.Threading.Monitor.PulseAll(this);

            if (_state == State.Closing && _dispatchCount == 0)
            {
                try
                {
                    InitiateShutdown();
                }
                catch (Exception ex)
                {
                    SetState(State.Closed, ex);
                }
            }

            // TODO: Benoit: do we really need to wait for pending IO to return?
            if (_state == State.Closed && _pendingIO == 0)
            {
                if (_outgoingMessages.Count == 0 && _asyncRequests.Count == 0 && _closeCallback == null)
                {
                    // Optimization: if there's no user callbacks to call, finish the connection now.
                    SetState(State.Finished);
                }
                else
                {
                    // Otherwise, schedule a task to call Finish()
                    // TODO: Benoit: is scheduling a task necessary?
                    RunTask(Finish);
                }
            }
        }

        private void InitiateShutdown()
        {
            Debug.Assert(_state == State.Closing && _dispatchCount == 0);

            if (!_endpoint.IsDatagram)
            {
                //
                // Before we shut down, we send a close connection message.
                //
                if ((SendMessage(new OutgoingMessage(_closeConnectionMessage, false)) &
                    OutgoingAsyncBase.AsyncStatusSent) != 0)
                {
                    // TODO: Benoit: SendMessage always returns Queued for now , this will need fixing
                    // to allow synchronous writes and awaitable SendAsyncRequest
                    Debug.Assert(false);
                    // SetState(State.ClosingPending);

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
            Debug.Assert(_state == State.Active);

            if (!_endpoint.IsDatagram)
            {
                try
                {
                    SendMessage(new OutgoingMessage(_validateConnectionMessage, false));
                }
                catch (System.Exception ex)
                {
                    SetState(State.Closed, ex);
                    Debug.Assert(_exception != null);
                }
            }
        }
        private async ValueTask Write()
        {
            while (true)
            {
                OutgoingMessage? message = null;
                int size = 0;
                List<ArraySegment<byte>>? writeBuffer = null;
                lock (this)
                {
                    if (_state > State.Closing || _outgoingMessages.Count == 0)
                    {
                        // If all the messages were sent and the close connection message has been
                        // sent, we switch to ClosingPending
                        if (_state == State.Closing && _dispatchCount == 0)
                        {
                            SetState(State.ClosingPending);
                        }
                        return;
                    }

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

                Debug.Assert(message != null);

                int offset = 0;
                OutgoingAsyncBase? outAsync = null;
                while (offset < size)
                {
                    int bytesSent = await _transceiver.WriteAsync(writeBuffer!, offset).ConfigureAwait(false);
                    offset += bytesSent;

                    lock (this)
                    {
                        // If we finished sending the message, remove it from the send queue and dispatch
                        // the progress notification or response if it was already received.
                        if (offset == size)
                        {
                            if (message.Sent())
                            {
                                outAsync = message.OutAsync;
                                if (outAsync != null)
                                {
                                    ++_dispatchCount;
                                }
                            }
                            _outgoingMessages.RemoveFirst();
                        }

                        if (_state < State.Closed)
                        {
                            TraceSentAndUpdateObserver(bytesSent);

                            if (_acmLastActivity > -1)
                            {
                                _acmLastActivity = Time.CurrentMonotonicTimeMillis();
                            }
                        }
                    }
                }

                if (outAsync != null)
                {
                    //  Dispatch sent callback
                    // TODO: Benoit: should serialize apply here to wait for the progress report to be
                    // dispatched to user code before sending the next message from the queue?
                    RunTask(async () => {
                        using var message = new IncomingMessage(this, sent: outAsync, response: null);
                        await DispatchMessage(message);
                    });
                }
            }
        }

        private int SendMessage(OutgoingMessage message)
        {
            Debug.Assert(_state < State.Closed);
            // TODO: Benoit: Refactor to write and await the calling thread to avoid having writing
            // on a thread pool thread
            _outgoingMessages.AddLast(message);
            if (_outgoingMessages.Count == 1)
            {
                RunTask(async () => { await RunIO(Write); });
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

        private ConnectionInfo InitConnectionInfo()
        {
            if (_state > State.NotInitialized && _info != null) // Update the connection info until it's initialized
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

        private void RunTask(Action action)
        {
            // Use the configured task scheduler to run the task. DenyChildAttach is the default for Task.Run,
            // we use the same here.
            Task.Factory.StartNew(action, default, TaskCreationOptions.DenyChildAttach,
                _taskScheduler ?? TaskScheduler.Default);
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

        private async ValueTask RunIO(Func<ValueTask> ioFunc)
        {
            lock (this)
            {
                if (_state >= State.ClosingPending)
                {
                    return;
                }
                ++_pendingIO;
            }

            try
            {
                await ioFunc();
            }
            catch (Exception ex)
            {
                lock (this)
                {
                    SetState(State.Closed, ex);
                }
            }

            bool finish = false;
            bool closing = false;
            lock (this)
            {
                --_pendingIO;

                // TODO: Benoit: Simplify the closing logic with the transport refactoring
                if (_state == State.ClosingPending && _pendingIO <= 1)
                {
                    ++_pendingIO;
                    closing = true;
                }
                else if (_state == State.Closed && _pendingIO == 0)
                {
                    finish = true;
                }
            }

            if (closing)
            {
                try
                {
                    bool canRead = ioFunc == Read || _pendingIO == 0;
                    bool canWrite = ioFunc == Write || _pendingIO == 0;
                    await _transceiver.ClosingAsync(_exception, canRead, canWrite);
                }
                catch (Exception)
                {
                }
                lock (this)
                {
                    SetState(State.Closed);
                    finish = --_pendingIO == 0;
                }
            }

            if (finish)
            {
                // No more pending IO and closed, it's time to terminate the connection
                Finish();
            }
        }

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
            internal OutgoingAsyncBase? OutAsync;
            internal bool Compress;
            internal int RequestId;
            internal bool InvokeSent;
        }

        private enum State
        {
            NotInitialized,
            NotValidated,
            Active,
            Closing,
            ClosingPending,
            Closed,
            Finished
        };

        private readonly Communicator _communicator;
        private IACMMonitor? _monitor;
        private readonly ITransceiver _transceiver;
        private string _desc;
        private readonly string _type;
        private readonly IConnector? _connector;
        private readonly Endpoint _endpoint;

        private ObjectAdapter? _adapter;
        private readonly TaskScheduler? _taskScheduler;

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

        private State _state; // The current state.
        private bool _validated = false;
        private ConnectionInfo? _info;

        private Action<Connection>? _closeCallback;
        private Action<Connection>? _heartbeatCallback;

        private static readonly ConnectionState[] _connectionStateMap = new ConnectionState[] {
            ConnectionState.ConnectionStateValidating,   // State.NotInitialized
            ConnectionState.ConnectionStateValidating,   // State.NotValidated
            ConnectionState.ConnectionStateActive,       // State.Active
            ConnectionState.ConnectionStateClosing,      // State.Closing
            ConnectionState.ConnectionStateClosing,      // State.ClosingPending
            ConnectionState.ConnectionStateClosed,       // State.Closed
            ConnectionState.ConnectionStateClosed,       // State.Finished
        };

        private static readonly List<ArraySegment<byte>> _validateConnectionMessage =
            new List<ArraySegment<byte>> { Ice1Definitions.ValidateConnectionMessage };
        private static readonly List<ArraySegment<byte>> _closeConnectionMessage =
            new List<ArraySegment<byte>> { Ice1Definitions.CloseConnectionMessage };
    }
}
