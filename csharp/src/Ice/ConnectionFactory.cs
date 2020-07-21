//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ZeroC.Ice.Instrumentation;

namespace ZeroC.Ice
{
    public interface IConnectionManager
    {
        IAcmMonitor AcmMonitor { get; }

        void Remove(Connection connection);
    }

    internal sealed class OutgoingConnectionFactory : IConnectionManager, IAsyncDisposable
    {
        public IAcmMonitor AcmMonitor { get; }

        private readonly Communicator _communicator;
        private readonly MultiDictionary<(IConnector, string), Connection> _connectionsByConnector =
            new MultiDictionary<(IConnector, string), Connection>();
        private readonly MultiDictionary<(Endpoint, string), Connection> _connectionsByEndpoint =
            new MultiDictionary<(Endpoint, string), Connection>();
        private Task? _disposeTask = null;
        private readonly object _mutex = new object();
        private readonly Dictionary<(IConnector, string), Task<Connection>> _pending =
            new Dictionary<(IConnector, string), Task<Connection>>();

        public async ValueTask DisposeAsync()
        {
            lock (_mutex)
            {
                _disposeTask ??= PerformDisposeAsync();
            }
            await _disposeTask.ConfigureAwait(false);

            async Task PerformDisposeAsync()
            {
                // Wait for connections to be closed.
                IEnumerable<Task> tasks =
                    _connectionsByConnector.Values.SelectMany(connections => connections).Select(connection =>
                        connection.GracefulCloseAsync(new CommunicatorDisposedException()));
                await Task.WhenAll(tasks).ConfigureAwait(false);

#if DEBUG
                // Ensure all the connections are removed
                Debug.Assert(_connectionsByConnector.Count == 0);
                Debug.Assert(_connectionsByEndpoint.Count == 0);
#endif
            }
        }

        public void Remove(Connection connection)
        {
            lock (_mutex)
            {
                _connectionsByConnector.Remove((connection.Connector, connection.ConnectionId), connection);
                foreach (Endpoint endpoint in connection.Endpoints)
                {
                    _connectionsByEndpoint.Remove((endpoint, connection.ConnectionId), connection);
                }
            }
        }

        internal OutgoingConnectionFactory(Communicator communicator)
        {
            _communicator = communicator;
            AcmMonitor = new ConnectionFactoryAcmMonitor(communicator, communicator.ClientAcm);
        }

        internal async ValueTask<Connection> CreateAsync(
            IReadOnlyList<Endpoint> endpoints,
            bool hasMore,
            EndpointSelectionType selType,
            string connectionId)
        {
            Debug.Assert(endpoints.Count > 0);

            lock (_mutex)
            {
                if (_disposeTask != null)
                {
                    throw new CommunicatorDisposedException();
                }

                // Try to find a connection to one of the given endpoints. Ignore the endpoint compression flag to
                // lookup for the connection.
                foreach (Endpoint endpoint in endpoints)
                {
                    if (_connectionsByEndpoint.TryGetValue((endpoint, connectionId),
                                                            out ICollection<Connection>? connectionList))
                    {
                        if (connectionList.FirstOrDefault(connection => connection.Active) is Connection connection)
                        {
                            return connection;
                        }
                    }
                }
            }

            // For each endpoint, obtain the set of connectors. This might block if DNS lookups are required to
            // resolve an endpoint hostname into connector addresses.
            var connectors = new List<(IConnector, Endpoint)>();
            foreach (Endpoint endpoint in endpoints)
            {
                try
                {
                    foreach (IConnector connector in await endpoint.ConnectorsAsync(selType).ConfigureAwait(false))
                    {
                        connectors.Add((connector, endpoint));
                    }
                }
                catch (CommunicatorDisposedException)
                {
                    throw; // No need to continue
                }
                catch (Exception ex)
                {
                    bool last = endpoint == endpoints[endpoints.Count - 1];

                    TraceLevels traceLevels = _communicator.TraceLevels;
                    if (traceLevels.Network >= 2)
                    {
                        _communicator.Logger.Trace(traceLevels.NetworkCategory, last ?
                            $"couldn't resolve endpoint host and no more endpoints to try\n{ex}" :
                            $"couldn't resolve endpoint host, trying next endpoint\n{ex}");
                    }

                    if (connectors.Count == 0 && last)
                    {
                        // If this was the last endpoint and we didn't manage to get a single connector, we're done.
                        throw;
                    }
                }
            }

            // Wait for connection establishment to one or some of the connectors to complete.
            while (true)
            {
                var connectTasks = new List<Task<Connection>>();
                var tried = new HashSet<IConnector>();
                lock (_mutex)
                {
                    if (_disposeTask != null)
                    {
                        throw new CommunicatorDisposedException();
                    }

                    // Search for pending connects for the set of connectors which weren't already tried.
                    foreach ((IConnector connector, Endpoint endpoint) in connectors)
                    {
                        if (_connectionsByConnector.TryGetValue((connector, connectionId),
                                                                out ICollection<Connection>? connectionList))
                        {
                            if (connectionList.FirstOrDefault(connection => connection.Active) is Connection connection)
                            {
                                return connection;
                            }
                        }

                        if (_pending.TryGetValue((connector, connectionId), out Task<Connection>? task))
                        {
                            connectTasks.Add(task);
                            tried.Add(connector);
                        }
                    }

                    // We didn't find pending connects for the remaining connectors so we can try to establish
                    // a connection to them.
                    if (tried.Count == 0)
                    {
                        Task<Connection> connectTask = ConnectAsync(connectors, connectionId, hasMore);
                        if (connectTask.IsCompleted)
                        {
                            try
                            {
                                return connectTask.Result;
                            }
                            catch (AggregateException ex)
                            {
                                Debug.Assert(ex.InnerException != null);
                                throw ExceptionUtil.Throw(ex.InnerException);
                            }
                        }

                        foreach ((IConnector connector, Endpoint endpoint) in connectors)
                        {
                            // Use TryAdd in case there are duplicates.
                            Debug.Assert(!_pending.ContainsKey((connector, connectionId)) ||
                                         connectors.Count(v => v.Equals((connector, endpoint))) > 1);
                            _pending.TryAdd((connector, connectionId), connectTask);
                            tried.Add(connector);
                        }
                        connectTasks.Add(connectTask);
                    }
                }

                // Wait for the first successful connection establishment
                Task<Connection> completedTask;
                do
                {
                    completedTask = await Task.WhenAny(connectTasks).ConfigureAwait(false);
                    if (completedTask.IsCompletedSuccessfully)
                    {
                        lock (_mutex)
                        {
                            Connection connection = completedTask.Result;
                            foreach ((IConnector connector, Endpoint endpoint) in connectors)
                            {
                                // If the connection was established for another endpoint but to the same connector,
                                // we ensure to also associate the connection with this endpoint.
                                if (connection.Connector.Equals(connector) && !connection.Endpoints.Contains(endpoint))
                                {
                                    Debug.Assert(connection.ConnectionId == connectionId);
                                    connection.Endpoints.Add(endpoint);
                                    _connectionsByEndpoint.Add((endpoint, connectionId), connection);
                                    break;
                                }
                            }
                            return connection;
                        }
                    }
                    connectTasks.Remove(completedTask);
                }
                while (connectTasks.Count > 0);

                // Remove the connectors we tried from the set of remaining connectors
                connectors.RemoveAll(((IConnector Connector, Endpoint Endpoint) tuple) =>
                    tried.Contains(tuple.Connector));

                // If there are no more connectors to try, we failed to establish a connection and we raise the
                // failure.
                if (connectors.Count == 0)
                {
                    Debug.Assert(completedTask.IsFaulted);
                    return await completedTask.ConfigureAwait(false);
                }
            }
        }

        internal void RemoveAdapter(ObjectAdapter adapter)
        {
            lock (_mutex)
            {
                if (_disposeTask != null)
                {
                    return;
                }

                foreach (ICollection<Connection> connectionList in _connectionsByConnector.Values)
                {
                    foreach (Connection connection in connectionList)
                    {
                        connection.ClearAdapter(adapter);
                    }
                }
            }
        }

        internal void SetRouterInfo(RouterInfo routerInfo)
        {
            ObjectAdapter? adapter = routerInfo.Adapter;
            IReadOnlyList<Endpoint> endpoints;
            try
            {
                ValueTask<IReadOnlyList<Endpoint>> task = routerInfo.GetClientEndpointsAsync();
                endpoints = task.IsCompleted ? task.Result : task.AsTask().Result;
            }
            catch (AggregateException ex)
            {
                Debug.Assert(ex.InnerException != null);
                throw ExceptionUtil.Throw(ex.InnerException);
            }

            // Search for connections to the router's client proxy endpoints, and update the object adapter for
            // such connections, so that callbacks from the router can be received over such connections.
            foreach (Endpoint endpoint in endpoints)
            {
                try
                {
                    foreach (IConnector connector in
                        endpoint.ConnectorsAsync(EndpointSelectionType.Ordered).AsTask().Result)
                    {
                        lock (_mutex)
                        {
                            if (_disposeTask != null)
                            {
                                throw new CommunicatorDisposedException();
                            }

                            if (_connectionsByConnector.TryGetValue((connector, routerInfo.Router.ConnectionId),
                                out ICollection<Connection>? connections))
                            {
                                foreach (Connection connection in connections)
                                {
                                    connection.Adapter = adapter;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore
                }
            }
        }

        internal void UpdateConnectionObservers()
        {
            lock (_mutex)
            {
                foreach (ICollection<Connection> connections in _connectionsByConnector.Values)
                {
                    foreach (Connection c in connections)
                    {
                        c.UpdateObserver();
                    }
                }
            }
        }

        private async Task<Connection> ConnectAsync(
            IReadOnlyList<(IConnector Connector, Endpoint Endpoint)> connectors,
            string connectionId,
            bool hasMore)
        {
            Debug.Assert(connectors.Count > 0);

            try
            {
                for (int i = 0; i < connectors.Count; ++i)
                {
                    (IConnector connector, Endpoint endpoint) = connectors[i];

                    IObserver? observer =
                        _communicator.Observer?.GetConnectionEstablishmentObserver(endpoint, connector.ToString()!);
                    try
                    {
                        observer?.Attach();

                        if (_communicator.TraceLevels.Network >= 2)
                        {
                            _communicator.Logger.Trace(_communicator.TraceLevels.NetworkCategory,
                                $"trying to establish {endpoint.TransportName} connection to {connector}");
                        }

                        Connection connection;
                        lock (_mutex)
                        {
                            if (_disposeTask != null)
                            {
                                throw new CommunicatorDisposedException();
                            }

                            // TODO: support other binary connections
                            var binaryConnection = new Ice1BinaryConnection(connector.Connect(), endpoint, null);

                            connection = endpoint.CreateConnection(this,
                                                                   binaryConnection,
                                                                   connector,
                                                                   connectionId,
                                                                   null);

                            _connectionsByConnector.Add((connector, connectionId), connection);
                            _connectionsByEndpoint.Add((endpoint, connectionId), connection);
                        }

                        await connection.StartAsync().ConfigureAwait(false);
                        return connection;
                    }
                    catch (CommunicatorDisposedException ex)
                    {
                        observer?.Failed(ex.GetType().FullName ?? "System.Exception");
                        throw; // No need to continue
                    }
                    catch (Exception ex)
                    {
                        observer?.Failed(ex.GetType().FullName ?? "System.Exception");

                        bool last = i == connectors.Count - 1;

                        TraceLevels traceLevels = _communicator.TraceLevels;
                        if (traceLevels.Network >= 2)
                        {
                            _communicator.Logger.Trace(traceLevels.NetworkCategory,
                                $"failed to establish {endpoint.TransportName} connection to {connector} " +
                                (last && !hasMore ?
                                    $"and no more endpoints to try\n{ex}" :
                                    $"trying next endpoint\n{ex}"));
                        }

                        if (last)
                        {
                            // If it's the last connector to try and we couldn't establish the connection, we're done.
                            throw;
                        }
                    }
                    finally
                    {
                        observer?.Detach();
                    }
                }
            }
            finally
            {
                lock (_mutex)
                {
                    foreach ((IConnector connector, Endpoint _) in connectors)
                    {
                        _pending.Remove((connector, connectionId));
                    }
                }
            }

            // The loop either raised an exception on the last connector or returned if the connection establishment
            // succeeded.
            Debug.Assert(false);
            return null!;
        }

        private class MultiDictionary<TKey, TValue> : Dictionary<TKey, ICollection<TValue>> where TKey : notnull
        {
            public void Add(TKey key, TValue value)
            {
                if (!TryGetValue(key, out ICollection<TValue>? list))
                {
                    list = new List<TValue>();
                    Add(key, list);
                }
                list.Add(value);
            }

            public void Remove(TKey key, TValue value)
            {
                ICollection<TValue> list = this[key];
                list.Remove(value);
                if (list.Count == 0)
                {
                    Remove(key);
                }
            }
        }
    }

    internal sealed class IncomingConnectionFactory : IConnectionManager, IAsyncDisposable
    {
        public IAcmMonitor AcmMonitor { get; }

        private readonly IAcceptor? _acceptor;
        private readonly ObjectAdapter _adapter;
        private readonly Communicator _communicator;
        private readonly HashSet<Connection> _connections = new HashSet<Connection>();
        private Task? _disposeTask = null;
        private readonly Endpoint _endpoint;
        private readonly object _mutex = new object();
        private readonly Endpoint? _publishedEndpoint;
        private readonly ITransceiver? _transceiver;
        private readonly bool _warn;

        public async ValueTask DisposeAsync()
        {
            lock (_mutex)
            {
                _disposeTask ??= PerformDisposeAsync();
            }
            await _disposeTask.ConfigureAwait(false);

            async Task PerformDisposeAsync()
            {
                // Close the acceptor
                if (_acceptor != null)
                {
                    if (_communicator.TraceLevels.Network >= 1)
                    {
                        _communicator.Logger.Trace(_communicator.TraceLevels.NetworkCategory,
                            $"stopping to accept {_endpoint.TransportName} connections at {_acceptor}");
                    }

                    _acceptor.Dispose();
                }

                // Wait for all the connections to be closed
                IEnumerable<Task> tasks = _connections.Select(
                    connection => connection.GracefulCloseAsync(
                        new ObjectDisposedException($"{typeof(ObjectAdapter).FullName}:{_adapter.Name}")));
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        public void Remove(Connection connection)
        {
            lock (_mutex)
            {
                _connections.Remove(connection);
            }
        }

        public override string ToString()
        {
            if (_transceiver != null)
            {
                return _transceiver.ToString()!;
            }
            else
            {
                return _acceptor!.ToString()!;
            }
        }

        // TODO: add support for accepting connections for multiple endpoints listening on the same TCP port and split
        // this factory in 3 different specialization: UdpIncomingConnectionFactory, TcpIncomingConnectionFactory and
        // QuicConnectionFactory.
        internal IncomingConnectionFactory(
            ObjectAdapter adapter,
            Endpoint endpoint,
            Endpoint? publish,
            Acm acm)
        {
            _communicator = adapter.Communicator;
            _endpoint = endpoint;
            _publishedEndpoint = publish;
            _adapter = adapter;
            _warn = _communicator.GetPropertyAsBool("Ice.Warn.Connections") ?? false;
            AcmMonitor = new ConnectionFactoryAcmMonitor(_communicator, acm);

            try
            {
                _transceiver = _endpoint.GetTransceiver();
                if (_transceiver != null)
                {
                    if (_communicator.TraceLevels.Network >= 2)
                    {
                        _communicator.Logger.Trace(_communicator.TraceLevels.NetworkCategory,
                            $"attempting to bind to {_endpoint.TransportName} socket\n{_transceiver}");
                    }
                    _endpoint = _transceiver.Bind();

                    var binaryConnection = new Ice1BinaryConnection(_transceiver, _endpoint, _adapter);
                    Connection connection = _endpoint.CreateConnection(this, binaryConnection, null, "", _adapter);
                    connection.Acm = Acm.Disabled; // Don't enable ACM for this connection
                    _ = connection.StartAsync();
                    _connections.Add(connection);
                }
                else
                {
                    // TODO: support for multiple endpoints for the same acceptor and not one acceptor per endpoint
                    _acceptor = new TcpAcceptor((TcpEndpoint)_endpoint, _adapter);

                    _endpoint = _acceptor!.Endpoint;

                    if (_communicator.TraceLevels.Network >= 1)
                    {
                        _communicator.Logger.Trace(_communicator.TraceLevels.NetworkCategory,
                            $"listening for {_endpoint.TransportName} connections\n{_acceptor!.ToDetailedString()}");
                    }
                }
            }
            catch (Exception)
            {
                //
                // Clean up.
                //
                try
                {
                    _transceiver?.Close();
                    _acceptor?.Dispose();
                }
                catch
                {
                    // Ignore
                }

                _connections.Clear();

                throw;
            }
        }

        internal void Activate()
        {
            lock (_mutex)
            {
                Debug.Assert(_disposeTask == null);
                if (_acceptor != null)
                {
                    if (_communicator.TraceLevels.Network >= 1)
                    {
                        _communicator.Logger.Trace(_communicator.TraceLevels.NetworkCategory,
                            $"accepting {_endpoint.TransportName} connections at {_acceptor}");
                    }

                    // Start the asynchronous operation from the thread pool to prevent eventually accepting
                    // synchronously new connections from this thread.
                    if (_adapter.TaskScheduler != null)
                    {
                        Task.Factory.StartNew(AcceptAsync, default, TaskCreationOptions.None, _adapter.TaskScheduler);
                    }
                    else
                    {
                        Task.Run(AcceptAsync);
                    }
                }
            }
        }

        internal Endpoint Endpoint()
        {
            if (_publishedEndpoint != null)
            {
                return _publishedEndpoint;
            }
            return _endpoint;
        }
        internal bool IsLocal(Endpoint endpoint)
        {
            if (_publishedEndpoint != null && endpoint.IsLocal(_publishedEndpoint))
            {
                return true;
            }
            return endpoint.IsLocal(_endpoint);
        }

        internal void UpdateConnectionObservers()
        {
            lock (_mutex)
            {
                foreach (Connection connection in _connections)
                {
                    connection.UpdateObserver();
                }
            }
        }

        private async ValueTask AcceptAsync()
        {
            while (true)
            {
                ITransceiver transceiver;
                try
                {
                    // We don't use ConfigureAwait(false) on purpose. We want to ensure continuations execute on the
                    // object adapter scheduler if an adapter scheduler is set.
                    transceiver = await _acceptor!.AcceptAsync();
                }
                catch (Exception ex)
                {
                    // If Accept failed because the acceptor has been closed, just return, we're done. Otherwise
                    // we print an error and wait for one second to avoid running in a tight loop in case the
                    // failures occurs immediately again. Failures here are unexpected and could be considered
                    // fatal.
                    lock (_mutex)
                    {
                        if (_disposeTask != null)
                        {
                            return;
                        }
                    }
                    _communicator.Logger.Error($"failed to accept connection:\n{ex}\n{_acceptor}");
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    continue;
                }

                Connection connection;
                lock (_mutex)
                {
                    Debug.Assert(transceiver != null);
                    if (_disposeTask != null)
                    {
                        try
                        {
                            transceiver.Destroy();
                        }
                        catch
                        {
                        }
                        return;
                    }

                    if (_communicator.TraceLevels.Network >= 2)
                    {
                        _communicator.Logger.Trace(_communicator.TraceLevels.NetworkCategory,
                            $"trying to accept {_endpoint.TransportName} connection\n{transceiver}");
                    }

                    // TODO: initialize the transceiver and figure out the Ice protocol from the initialization (for
                    // tcp connection we read the first few bytes, for ssl we rely on APLN, for ws we rely on the
                    // Sec-WebSocket-Protocol header field).

                    var binaryConnection = new Ice1BinaryConnection(transceiver, _endpoint, _adapter);
                    try
                    {
                        connection = _endpoint.CreateConnection(this, binaryConnection, null, "", _adapter);
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            transceiver.Destroy();
                        }
                        catch
                        {
                            // Ignore
                        }

                        if (_warn)
                        {
                            _communicator.Logger.Warning($"connection exception:\n{ex}\n{_acceptor}");
                        }
                        continue;
                    }

                    _connections.Add(connection);
                }

                Debug.Assert(connection != null);
                try
                {
                    // We don't wait for the connection to be activated. This could take a while for some transports
                    // such as TLS based transports where the handshake requires few round trips between the client
                    // and server.
                    _ = connection.StartAsync();
                }
                catch (ObjectDisposedException)
                {
                    // Ignore
                }
                catch (Exception ex)
                {
                    if (_communicator.TraceLevels.Network >= 2)
                    {
                        _communicator.Logger.Trace(_communicator.TraceLevels.NetworkCategory,
                            $"failed to accept {_endpoint.TransportName} connection\n{connection}\n{ex}");
                    }
                }
            }
        }
    }
}
