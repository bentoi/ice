//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using Ice;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace IceInternal
{
    public class CollocatedRequestHandler : IRequestHandler
    {
        public
        CollocatedRequestHandler(Reference @ref, Ice.ObjectAdapter adapter)
        {
            _reference = @ref;
            _adapter = adapter;
            _requestId = 0;
        }

        public IRequestHandler? Update(IRequestHandler previousHandler, IRequestHandler? newHandler) =>
            previousHandler == this ? newHandler : this;

        public int SendAsyncRequest(ProxyOutgoingAsyncBase outAsync) => outAsync.InvokeCollocated(this);

        public void AsyncRequestCanceled(OutgoingAsyncBase outAsync, Exception ex)
        {
            lock (this)
            {
                if (_sendAsyncRequests.TryGetValue(outAsync, out int requestId))
                {
                    if (requestId > 0)
                    {
                        _asyncRequests.Remove(requestId);
                    }
                    _sendAsyncRequests.Remove(outAsync);
                    if (outAsync.Exception(ex))
                    {
                        outAsync.InvokeExceptionAsync();
                    }
                    _adapter.DecDirectCount(); // invokeAll won't be called, decrease the direct count.
                    return;
                }
                if (outAsync is OutgoingAsync o)
                {
                    Debug.Assert(o != null);
                    foreach (KeyValuePair<int, OutgoingAsyncBase> e in _asyncRequests)
                    {
                        if (e.Value == o)
                        {
                            _asyncRequests.Remove(e.Key);
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

        public Connection? GetConnection() => null;

        public int InvokeAsyncRequest(ProxyOutgoingAsyncBase outAsync, bool synchronous)
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
                    outAsync.Cancelable(this); // This will throw if the request is canceled

                    if (!outAsync.IsOneway)
                    {
                        requestId = ++_requestId;
                        _asyncRequests.Add(requestId, outAsync);
                    }

                    _sendAsyncRequests.Add(outAsync, requestId);
                }
            }
            catch (Exception)
            {
                _adapter.DecDirectCount();
                throw;
            }

            outAsync.AttachCollocatedObserver(_adapter, requestId, outAsync.RequestFrame.Size);
            if (_adapter.TaskScheduler != null || !synchronous || outAsync.IsOneway || _reference.InvocationTimeout > 0)
            {
                // Don't invoke from the user thread if async or invocation timeout is set. We also don't dispatch
                // oneway from the user thread to match the non-collocated behavior where the oneway synchronous
                // request returns as soon as it's sent over the transport.
                Task.Factory.StartNew(
                    () =>
                    {
                        if (SentAsync(outAsync))
                        {
                            ValueTask vt = InvokeAllAsync(outAsync.RequestFrame, requestId);
                            // TODO: do something with the value task
                        }
                    }, default, TaskCreationOptions.DenyChildAttach, _adapter.TaskScheduler ?? TaskScheduler.Default);
            }
            else // Optimization: directly call invokeAll
            {
                if (SentAsync(outAsync))
                {
                    Debug.Assert(outAsync.RequestFrame != null);
                    ValueTask vt = InvokeAllAsync(outAsync.RequestFrame, requestId);
                    // TODO: do something with the value task
                }
            }
            return OutgoingAsyncBase.AsyncStatusQueued;
        }

        private bool SentAsync(OutgoingAsyncBase outAsync)
        {
            lock (this)
            {
                if (!_sendAsyncRequests.Remove(outAsync))
                {
                    return false; // The request timed-out.
                }

                if (!outAsync.Sent())
                {
                    return true;
                }
            }

            // Use the communicator's task scheduler or the default if the adapter and communicator are not
            // the same.
            if (_adapter.Communicator.TaskScheduler != _adapter.TaskScheduler)
            {
                Task.Factory.StartNew(() => outAsync.InvokeSent(), default, TaskCreationOptions.None,
                    _adapter.Communicator.TaskScheduler ?? TaskScheduler.Default).Wait();
            }
            else
            {
                outAsync.InvokeSent();
            }
            return true;
        }

        private async ValueTask InvokeAllAsync(OutgoingRequestFrame outgoingRequest, int requestId)
        {
            // The object adapter DirectCount was incremented by the caller and we are responsible to decrement it
            // upon completion.

            Ice.Instrumentation.IDispatchObserver? dispatchObserver = null;
            try
            {
                if (_adapter.Communicator.TraceLevels.Protocol >= 1)
                {
                    // TODO we need a better API for tracing
                    List<ArraySegment<byte>> requestData = Ice1Definitions.GetRequestData(outgoingRequest, requestId);
                    TraceUtil.TraceSend(_adapter.Communicator, requestData);
                }

                var incomingRequest = new IncomingRequestFrame(_adapter.Communicator, outgoingRequest);
                var current = new Current(_adapter, incomingRequest, requestId);

                // Then notify and set dispatch observer, if any.
                Ice.Instrumentation.ICommunicatorObserver? communicatorObserver = _adapter.Communicator.Observer;
                if (communicatorObserver != null)
                {
                    dispatchObserver = communicatorObserver.GetDispatchObserver(current, incomingRequest.Size);
                    dispatchObserver?.Attach();
                }

                bool amd = true;
                try
                {
                    IObject? servant = current.Adapter.Find(current.Identity, current.Facet);

                    if (servant == null)
                    {
                        amd = false;
                        throw new ObjectNotExistException(current.Identity, current.Facet, current.Operation);
                    }

                    ValueTask<OutgoingResponseFrame> vt = servant.DispatchAsync(incomingRequest, current);
                    amd = !vt.IsCompleted;
                    if (requestId != 0)
                    {
                        OutgoingResponseFrame response = await vt.ConfigureAwait(false);
                        dispatchObserver?.Reply(response.Size);
                        SendResponse(requestId, response, amd);
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
                        SendResponse(requestId, response, amd);
                    }
                }
            }
            catch (Exception ex)
            {
                HandleException(requestId, ex, false);
            }
            finally
            {
                dispatchObserver?.Detach();
                _adapter.DecDirectCount();
            }
        }

        private void SendResponse(int requestId, OutgoingResponseFrame responseFrame, bool amd)
        {
            OutgoingAsyncBase? outAsync;
            lock (this)
            {
                var responseBuffer = new ArraySegment<byte>(VectoredBufferExtensions.ToArray(
                        Ice1Definitions.GetResponseData(responseFrame, requestId)));
                if (_adapter.Communicator.TraceLevels.Protocol >= 1)
                {
                    TraceUtil.TraceRecv(_adapter.Communicator, responseBuffer);
                }
                responseBuffer = responseBuffer.Slice(Ice1Definitions.HeaderSize + 4);
                if (_asyncRequests.TryGetValue(requestId, out outAsync))
                {
                    if (!outAsync.Response(new IncomingResponseFrame(_adapter.Communicator, responseBuffer)))
                    {
                        outAsync = null;
                    }
                    _asyncRequests.Remove(requestId);
                }
            }

            if (outAsync != null)
            {
                if (amd || _adapter.Communicator.TaskScheduler != _adapter.TaskScheduler)
                {
                    Task.Factory.StartNew(() => outAsync.InvokeResponse(), default, TaskCreationOptions.None,
                        _adapter.Communicator.TaskScheduler ?? TaskScheduler.Default);
                }
                else
                {
                    outAsync.InvokeResponse();
                }
            }
        }

        private void HandleException(int requestId, Exception ex, bool amd)
        {
            if (requestId == 0)
            {
                return; // Ignore exception for oneway messages.
            }

            OutgoingAsyncBase? outAsync;
            lock (this)
            {
                if (_asyncRequests.TryGetValue(requestId, out outAsync))
                {
                    if (!outAsync.Exception(ex))
                    {
                        outAsync = null;
                    }
                    _asyncRequests.Remove(requestId);
                }
            }

            if (outAsync != null)
            {
                //
                // If called from an AMD dispatch, invoke asynchronously
                // the completion callback since this might be called from
                // the user code.
                //
                if (amd)
                {
                    outAsync.InvokeExceptionAsync();
                }
                else
                {
                    outAsync.InvokeException();
                }
            }
        }

        private readonly Reference _reference;
        private readonly Ice.ObjectAdapter _adapter;
        private int _requestId;
        private readonly Dictionary<OutgoingAsyncBase, int> _sendAsyncRequests = new Dictionary<OutgoingAsyncBase, int>();
        private readonly Dictionary<int, OutgoingAsyncBase> _asyncRequests = new Dictionary<int, OutgoingAsyncBase>();
    }
}
