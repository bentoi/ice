//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System.Collections.Generic;

namespace Ice
{
    namespace Instrumentation
    {
        public partial interface IObserver
        {
            /// <summary>
            /// This method is called when the instrumented object is created
            /// or when the observer is attached to an existing object.
            /// </summary>
            void Attach();

            /// <summary>
            /// This method is called when the instrumented object is destroyed
            /// and as a result the observer detached from the object.
            /// </summary>
            void Detach();

            /// <summary>
            /// Notification of a failure.
            /// </summary>
            /// <param name="exceptionName">The name of the exception.</param>
            void Failed(string exceptionName);
        }

        public enum ThreadState
        {
            ThreadStateIdle,
            ThreadStateInUseForIO,
            ThreadStateInUseForUser,
            ThreadStateInUseForOther
        }

        public partial interface IThreadObserver : IObserver
        {
            /// <summary>
            /// Notification of thread state change.
            /// </summary>
            /// <param name="oldState">The previous thread state.
            ///
            /// </param>
            /// <param name="newState">The new thread state.</param>
            void StateChanged(ThreadState oldState, ThreadState newState);
        }

        public enum ConnectionState
        {
            ConnectionStateValidating,
            ConnectionStateActive,
            ConnectionStateClosing,
            ConnectionStateClosed
        }

        public partial interface IConnectionObserver : IObserver
        {
            /// <summary>
            /// Notification of sent bytes over the connection.
            /// </summary>
            /// <param name="num">The number of bytes sent.</param>
            void SentBytes(int num);

            /// <summary>
            /// Notification of received bytes over the connection.
            /// </summary>
            /// <param name="num">The number of bytes received.</param>
            void ReceivedBytes(int num);
        }

        public partial interface IDispatchObserver : IObserver
        {
            void RemoteException();

            /// <summary>
            /// Reply notification.
            /// </summary>
            /// <param name="size">The size of the reply.</param>
            void Reply(int size);
        }

        public partial interface IChildInvocationObserver : IObserver
        {
            /// <summary>
            /// Reply notification.
            /// </summary>
            /// <param name="size">The size of the reply.</param>
            void Reply(int size);
        }

        public partial interface IRemoteObserver : IChildInvocationObserver
        {
        }

        public partial interface ICollocatedObserver : IChildInvocationObserver
        {
        }

        public partial interface IInvocationObserver : IObserver
        {
            void Retried();
            void RemoteException();

            /// <summary>
            /// Get a remote observer for this invocation.
            /// </summary>
            /// <param name="con">The connection information.
            ///
            /// </param>
            /// <param name="endpt">The connection endpoint.
            ///
            /// </param>
            /// <param name="requestId">The ID of the invocation.
            ///
            /// </param>
            /// <param name="size">The size of the invocation.
            ///
            /// </param>
            /// <returns>The observer to instrument the remote invocation.</returns>
            IRemoteObserver? GetRemoteObserver(ConnectionInfo con, Endpoint endpt, int requestId, int size);

            /// <summary>
            /// Get a collocated observer for this invocation.
            /// </summary>
            /// <param name="adapter">The object adapter hosting the collocated Ice object.
            ///
            /// </param>
            /// <param name="requestId">The ID of the invocation.
            ///
            /// </param>
            /// <param name="size">The size of the invocation.
            ///
            /// </param>
            /// <returns>The observer to instrument the collocated invocation.</returns>
            ICollocatedObserver? GetCollocatedObserver(ObjectAdapter adapter, int requestId, int size);
        }

        public partial interface IObserverUpdater
        {
            /// <summary>
            /// Update connection observers associated with each of the Ice
            /// connection from the communicator and its object adapters.
            /// When called, this method goes through all the connections and
            /// for each connection CommunicatorObserver.getConnectionObserver
            /// is called. The implementation of getConnectionObserver has the
            /// possibility to return an updated observer if necessary.
            /// </summary>
            void UpdateConnectionObservers();

            /// <summary>
            /// Update thread observers associated with each of the Ice thread
            /// from the communicator and its object adapters.
            /// When called, this method goes through all the threads and for
            /// each thread CommunicatorObserver.getThreadObserver is
            /// called. The implementation of getThreadObserver has the
            /// possibility to return an updated observer if necessary.
            /// </summary>
            void UpdateThreadObservers();
        }

        public partial interface ICommunicatorObserver
        {
            /// <summary>
            /// This method should return an observer for the given endpoint
            /// information and connector.
            /// The Ice run-time calls this method
            /// for each connection establishment attempt.
            ///
            /// </summary>
            /// <param name="endpt">The endpoint.
            ///
            /// </param>
            /// <param name="connector">The description of the connector. For IP
            /// transports, this is typically the IP address to connect to.
            ///
            /// </param>
            /// <returns>The observer to instrument the connection establishment.</returns>
            IObserver? GetConnectionEstablishmentObserver(Endpoint endpt, string connector);

            /// <summary>
            /// This method should return an observer for the given endpoint
            /// information.
            /// The Ice run-time calls this method to resolve an
            /// endpoint and obtain the list of connectors.
            ///
            /// For IP endpoints, this typically involves doing a DNS lookup to
            /// obtain the IP addresses associated with the DNS name.
            ///
            /// </summary>
            /// <param name="endpt">The endpoint.
            ///
            /// </param>
            /// <returns>The observer to instrument the endpoint lookup.</returns>
            IObserver? GetEndpointLookupObserver(Endpoint endpt);

            /// <summary>
            /// This method should return a connection observer for the given
            /// connection.
            /// The Ice run-time calls this method for each new
            /// connection and for all the Ice communicator connections when
            /// ObserverUpdater.updateConnectionObservers is called.
            ///
            /// </summary>
            /// <param name="c">The connection information.
            ///
            /// </param>
            /// <param name="e">The connection endpoint.
            ///
            /// </param>
            /// <param name="s">The state of the connection.
            ///
            /// </param>
            /// <param name="o">The old connection observer if one is already set or a
            /// null reference otherwise.
            ///
            /// </param>
            /// <returns>The connection observer to instrument the connection.</returns>
            IConnectionObserver? GetConnectionObserver(ConnectionInfo c, Endpoint e, ConnectionState s, IConnectionObserver? o);

            /// <summary>
            /// This method should return a thread observer for the given
            /// thread.
            /// The Ice run-time calls this method for each new thread
            /// and for all the Ice communicator threads when
            /// ObserverUpdater.updateThreadObservers is called.
            ///
            /// </summary>
            /// <param name="parent">The parent of the thread.
            ///
            /// </param>
            /// <param name="id">The ID of the thread to observe.
            ///
            /// </param>
            /// <param name="s">The state of the thread.
            ///
            /// </param>
            /// <param name="o">The old thread observer if one is already set or a
            /// null reference otherwise.
            ///
            /// </param>
            /// <returns>The thread observer to instrument the thread.</returns>
            IThreadObserver? GetThreadObserver(string parent, string id, ThreadState s, IThreadObserver? o);

            /// <summary>
            /// This method should return an invocation observer for the given
            /// invocation.
            /// The Ice run-time calls this method for each new
            /// invocation on a proxy.
            ///
            /// </summary>
            /// <param name="prx">The proxy used for the invocation.
            ///
            /// </param>
            /// <param name="operation">The name of the invocation.
            ///
            /// </param>
            /// <param name="ctx">The context specified by the user.
            ///
            /// </param>
            /// <returns>The invocation observer to instrument the invocation.</returns>
            IInvocationObserver? GetInvocationObserver(IObjectPrx? prx, string operation,
                                                       IReadOnlyDictionary<string, string> ctx);

            /// <summary>
            /// This method should return a dispatch observer for the given
            /// dispatch.
            /// The Ice run-time calls this method each time it
            /// receives an incoming invocation to be dispatched for an Ice
            /// object.
            ///
            /// </summary>
            /// <param name="c">The current object as provided to the Ice servant
            /// dispatching the invocation.
            ///
            /// </param>
            /// <param name="size">The size of the dispatch.
            ///
            /// </param>
            /// <returns>The dispatch observer to instrument the dispatch.</returns>
            IDispatchObserver? GetDispatchObserver(Current c, int size);

            /// <summary>
            /// The Ice run-time calls this method when the communicator is
            /// initialized.
            /// The add-in implementing this interface can use
            /// this object to get the Ice run-time to re-obtain observers for
            /// observed objects.
            ///
            /// </summary>
            /// <param name="updater">The observer updater object.</param>
            void SetObserverUpdater(IObserverUpdater? updater);
        }
    }
}
