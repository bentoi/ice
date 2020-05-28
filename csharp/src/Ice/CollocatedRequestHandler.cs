//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using ZeroC.Ice.Instrumentation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ZeroC.Ice
{
    internal class CollocatedRequestHandler : IRequestHandler
    {
        public
        CollocatedRequestHandler(Reference reference, ObjectAdapter adapter)
        {
            _reference = reference;
            _adapter = adapter;
            _requestId = 0;
        }

        public void AsyncRequestCanceled(Outgoing outgoing, Exception ex)
        {
            lock (this)
            {
                if (_sendAsyncRequests.TryGetValue(outgoing, out int requestId))
                {
                    if (requestId > 0)
                    {
                        _requests.Remove(requestId);
                    }
                    _sendAsyncRequests.Remove(outgoing);
                    if (outgoing.Exception(ex))
                    {
                        Task.Run(outgoing.InvokeException);
                    }
                    _adapter.DecDirectCount(); // invokeAll won't be called, decrease the direct count.
                    return;
                }
                if (outgoing is InvokeOutgoing o)
                {
                    Debug.Assert(o != null);
                    foreach (KeyValuePair<int, InvokeOutgoing> e in _requests)
                    {
                        if (e.Value == o)
                        {
                            _requests.Remove(e.Key);
                            if (outgoing.Exception(ex))
                            {
                                Task.Run(outgoing.InvokeException);
                            }
                            return;
                        }
                    }
                }
            }
        }

        public Connection? GetConnection() => null;

        public void SendRequestAsync(InvokeOutgoing outgoing)
        {
            //
            // Increase the direct count to prevent the thread pool from being destroyed before
            // invokeAll is called. This will also throw if the object adapter has been deactivated.
            //
            _adapter.IncDirectCount();

            int requestId = 0;
            try
            {
                lock (this)
                {
                    outgoing.Cancelable(this); // This will throw if the request is canceled

                    if (!outgoing.IsOneway)
                    {
                        requestId = ++_requestId;
                        _requests.Add(requestId, outgoing);
                    }

                    _sendAsyncRequests.Add(outgoing, requestId);
                }
            }
            catch (Exception)
            {
                _adapter.DecDirectCount();
                throw;
            }

            outgoing.AttachCollocatedObserver(_adapter, requestId, outgoing.RequestFrame.Size);
            if (_adapter.TaskScheduler != null || !outgoing.Synchronous || outgoing.IsOneway ||
                _reference.InvocationTimeout > 0)
            {
                // Don't invoke from the user thread if async or invocation timeout is set. We also don't dispatch
                // oneway from the user thread to match the non-collocated behavior where the oneway synchronous
                // request returns as soon as it's sent over the transport.
                Task.Factory.StartNew(
                    () =>
                    {
                        if (SentAsync(outgoing))
                        {
                            ValueTask vt = InvokeAllAsync(outgoing.RequestFrame, requestId);
                            // TODO: do something with the value task
                        }
                    }, default, TaskCreationOptions.None, _adapter.TaskScheduler ?? TaskScheduler.Default);
            }
            else // Optimization: directly call invokeAll
            {
                if (SentAsync(outgoing))
                {
                    Debug.Assert(outgoing.RequestFrame != null);
                    ValueTask vt = InvokeAllAsync(outgoing.RequestFrame, requestId);
                    // TODO: do something with the value task
                }
            }
        }

        private bool SentAsync(Outgoing outgoing)
        {
            lock (this)
            {
                if (!_sendAsyncRequests.Remove(outgoing))
                {
                    return false; // The request timed-out.
                }

                if (!outgoing.Sent())
                {
                    return true;
                }
            }
            // The progress callback is always called from the default task scheduler
            Task.Run(outgoing.InvokeSent);
            return true;
        }

        private async ValueTask InvokeAllAsync(OutgoingRequestFrame outgoingRequest, int requestId)
        {
            // The object adapter DirectCount was incremented by the caller and we are responsible to decrement it
            // upon completion.

            IDispatchObserver? dispatchObserver = null;
            try
            {
                if (_adapter.Communicator.TraceLevels.Protocol >= 1)
                {
                    ProtocolTrace.TraceCollocatedFrame(
                        _adapter.Communicator,
                        (byte)Ice1Definitions.FrameType.Request,
                        requestId,
                        outgoingRequest);
                }

                var incomingRequest = new IncomingRequestFrame(_adapter.Communicator, outgoingRequest);
                var current = new Current(_adapter, incomingRequest, requestId);

                // Then notify and set dispatch observer, if any.
                ICommunicatorObserver? communicatorObserver = _adapter.Communicator.Observer;
                if (communicatorObserver != null)
                {
                    dispatchObserver = communicatorObserver.GetDispatchObserver(current, incomingRequest.Size);
                    dispatchObserver?.Attach();
                }

                try
                {
                    IObject? servant = current.Adapter.Find(current.Identity, current.Facet);

                    if (servant == null)
                    {
                        throw new ObjectNotExistException(current.Identity, current.Facet, current.Operation);
                    }

                    ValueTask<OutgoingResponseFrame> vt = servant.DispatchAsync(incomingRequest, current);
                    if (requestId != 0)
                    {
                        OutgoingResponseFrame response = await vt.ConfigureAwait(false);
                        dispatchObserver?.Reply(response.Size);
                        SendResponse(requestId, response);
                    }
                }
                catch (Exception ex)
                {
                    if (requestId != 0)
                    {
                        RemoteException actualEx;
                        if (ex is RemoteException remoteEx && !remoteEx.ConvertToUnhandled)
                        {
                            actualEx = remoteEx;
                        }
                        else
                        {
                            actualEx = new UnhandledException(current.Identity, current.Facet, current.Operation, ex);
                        }

                        Incoming.ReportException(actualEx, dispatchObserver, current);
                        var response = new OutgoingResponseFrame(current, actualEx);
                        dispatchObserver?.Reply(response.Size);
                        SendResponse(requestId, response);
                    }
                }
            }
            catch (Exception ex)
            {
                HandleException(requestId, ex);
            }
            finally
            {
                dispatchObserver?.Detach();
                _adapter.DecDirectCount();
            }
        }

        private void SendResponse(int requestId, OutgoingResponseFrame outgoingResponseFrame)
        {
            InvokeOutgoing? outgoing;
            lock (this)
            {
                var incomingResponseFrame = new IncomingResponseFrame(
                    _adapter.Communicator,
                    VectoredBufferExtensions.ToArray(outgoingResponseFrame.Data));
                if (_adapter.Communicator.TraceLevels.Protocol >= 1)
                {
                    ProtocolTrace.TraceCollocatedFrame(
                        _adapter.Communicator,
                        (byte)Ice1Definitions.FrameType.Reply,
                        requestId,
                        incomingResponseFrame);
                }
                if (_requests.TryGetValue(requestId, out outgoing))
                {
                    if (!outgoing.Response(incomingResponseFrame))
                    {
                        outgoing = null;
                    }
                    _requests.Remove(requestId);
                }
            }

            if (outgoing != null)
            {
                outgoing.InvokeResponse();
            }
        }

        private void HandleException(int requestId, Exception ex)
        {
            if (requestId == 0)
            {
                return; // Ignore exception for oneway messages.
            }

            InvokeOutgoing? outgoing;
            lock (this)
            {
                if (_requests.TryGetValue(requestId, out outgoing))
                {
                    if (!outgoing.Exception(ex))
                    {
                        outgoing = null;
                    }
                    _requests.Remove(requestId);
                }
            }

            if (outgoing != null)
            {
                outgoing.InvokeException();
            }
        }

        private readonly Reference _reference;
        private readonly ObjectAdapter _adapter;
        private int _requestId;
        private readonly Dictionary<Outgoing, int> _sendAsyncRequests = new Dictionary<Outgoing, int>();
        private readonly Dictionary<int, InvokeOutgoing> _requests = new Dictionary<int, InvokeOutgoing>();
    }
}
