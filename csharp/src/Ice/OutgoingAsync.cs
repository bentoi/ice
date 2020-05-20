//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using ZeroC.Ice;
using ZeroC.Ice.Instrumentation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace IceInternal
{

    public abstract class Outgoing
    {
        public readonly bool IsOneway;
        protected readonly Communicator Communicator;
        protected readonly bool Synchronous;
        protected bool IsSent;
        protected IInvocationObserver? Observer;
        protected IChildInvocationObserver? ChildObserver;
        protected readonly CancellationToken CancellationToken;
        private bool _alreadySent;
        private Exception? _cancellationException;
        private ICancellationHandler? _cancellationHandler;
        private Exception? _exception;
        private readonly IProgress<bool>? _progress;

        public void AttachRemoteObserver(ConnectionInfo info, Endpoint endpt, int requestId, int size)
        {
            IInvocationObserver? observer = GetObserver();
            if (observer != null)
            {
                ChildObserver = observer.GetRemoteObserver(info, endpt, requestId, size);
                if (ChildObserver != null)
                {
                    ChildObserver.Attach();
                }
            }
        }

        public void AttachCollocatedObserver(ObjectAdapter adapter, int requestId, int size)
        {
            IInvocationObserver? observer = GetObserver();
            if (observer != null)
            {
                ChildObserver = observer.GetCollocatedObserver(adapter, requestId, size);
                if (ChildObserver != null)
                {
                    ChildObserver.Attach();
                }
            }
        }

        public void Cancelable(ICancellationHandler handler)
        {
            lock (this)
            {
                if (_cancellationException != null)
                {
                    try
                    {
                        throw _cancellationException;
                    }
                    catch (Exception)
                    {
                        _cancellationException = null;
                        throw;
                    }
                }
                _cancellationHandler = handler;
            }
        }

        // TODO: add more details in message
        public void Cancel() => Cancel(new OperationCanceledException("invocation on remote Ice object canceled"));

        public virtual bool Exception(Exception ex) => ExceptionImpl(ex);

        public void InvokeException()
        {
            Debug.Assert(_exception != null);
            SetException(_exception);

            Observer?.Detach();
        }

        public void InvokeSent()
        {
            try
            {
                if (_progress != null && !_alreadySent)
                {
                    // TODO: Add back support for sentSynchronously to the IProgress.Report callback?
                    _progress.Report(false);
                }
            }
            catch (Exception ex)
            {
                // TODO: this is only used to report exceptions from IProgress.Report... fix the property name?
                if (Communicator.GetPropertyAsBool("Ice.Warn.AMICallback") ?? true)
                {
                    Communicator.Logger.Warning("exception raised by AMI callback:\n" + ex);
                }
            }

            if (IsOneway)
            {
                InvokeResponse();
            }
        }

        public void InvokeResponse()
        {
            if (_exception != null)
            {
                InvokeException();
                return;
            }

            SetResult();

            Observer?.Detach();
        }

        protected abstract void SetException(Exception ex);
        protected abstract void SetResult();

        protected Outgoing(Communicator communicator, bool oneway, bool synchronous, IProgress<bool>? progress,
            CancellationToken cancel)
        {
            Communicator = communicator;
            IsOneway = oneway;
            Synchronous = synchronous;

            IsSent = false;

            _alreadySent = false;

            CancellationToken = cancel;
            _progress = progress;
            if (CancellationToken.CanBeCanceled)
            {
                CancellationToken.Register(Cancel);
            }
        }

        public abstract List<ArraySegment<byte>> GetRequestData(int requestId);

        public virtual bool Sent()
        {
            lock (this)
            {
                _alreadySent = IsSent;
                IsSent = true;
                if (IsOneway)
                {
                    if (ChildObserver != null)
                    {
                        ChildObserver.Detach();
                        ChildObserver = null;
                    }
                    _cancellationHandler = null;

                    if (Synchronous)
                    {
                        Debug.Assert(_progress == null);
                        SetResult();
                        Observer?.Detach();
                        return false;
                    }
                }

                // Invoke the sent callback only if not already invoked.
                bool invoke = IsOneway || (_progress != null && !_alreadySent);
                if (!invoke && IsOneway)
                {
                    Observer?.Detach();
                }
                return invoke;
            }
        }

        protected virtual bool ExceptionImpl(Exception ex)
        {
            lock (this)
            {
                _exception = ex;

                if (ChildObserver != null)
                {
                    ChildObserver.Failed(ex.GetType().FullName ?? "System.Exception");
                    ChildObserver.Detach();
                    ChildObserver = null;
                }
                _cancellationHandler = null;

                if (Observer != null)
                {
                    Observer.Failed(ex.GetType().FullName ?? "System.Exception");
                }

                if (Synchronous)
                {
                    SetException(ex);
                    Observer?.Detach();
                    return false;
                }
                else
                {
                    return true;
                }
            }
        }

        protected virtual bool ResponseImpl()
        {
            lock (this)
            {
                _cancellationHandler = null;

                if (Synchronous)
                {
                    SetResult();
                    Observer?.Detach();
                    return false;
                }
                else
                {
                    return true;
                }
            }
        }

        protected void Cancel(Exception ex)
        {
            ICancellationHandler handler;
            {
                lock (this)
                {
                    if (_cancellationHandler == null)
                    {
                        _cancellationException = ex;
                        return;
                    }
                    handler = _cancellationHandler;
                }
            }
            handler.AsyncRequestCanceled(this, ex);
        }

        protected IInvocationObserver? GetObserver() => Observer;
    }

    //
    // Base class for proxy based invocations. This class handles the
    // retry for proxy invocations. It also ensures the child observer is
    // correct notified of failures and make sure the retry task is
    // correctly canceled when the invocation completes.
    //
    public abstract class ProxyOutgoing : Outgoing, ITimerTask
    {
        protected bool IsIdempotent;
        protected readonly IObjectPrx Proxy;
        protected IRequestHandler? Handler;
        private int _cnt;
        public abstract void InvokeRemote(Connection connection, bool compress);
        public abstract void InvokeCollocated(CollocatedRequestHandler handler);

        public override bool Exception(Exception exc)
        {
            if (ChildObserver != null)
            {
                ChildObserver.Failed(exc.GetType().FullName ?? "System.Exception");
                ChildObserver.Detach();
                ChildObserver = null;
            }

            //
            // NOTE: at this point, synchronization isn't needed, no other threads should be
            // calling on the callback.
            //
            try
            {
                //
                // It's important to let the retry queue do the retry even if
                // the retry interval is 0. This method can be called with the
                // connection locked so we can't just retry here.
                //
                Communicator.AddRetryTask(this, Proxy.IceHandleException(exc, Handler, IsIdempotent, IsSent, ref _cnt));
                return false;
            }
            catch (Exception ex)
            {
                return ExceptionImpl(ex); // No retries, we're done
            }
        }

        public void RetryException()
        {
            try
            {
                //
                // It's important to let the retry queue do the retry. This is
                // called from the connect request handler and the retry might
                // require could end up waiting for the flush of the
                // connection to be done.
                //

                // Clear request handler and always retry.
                if (!Proxy.IceReference.IsFixed)
                {
                    Proxy.IceReference.UpdateRequestHandler(Handler, null);
                }
                Communicator.AddRetryTask(this, 0);
            }
            catch (Exception ex)
            {
                if (Exception(ex))
                {
                    Task.Run(InvokeException);
                }
            }
        }

        public void Retry() => _ = InvokeImpl(true);

        protected ProxyOutgoing(IObjectPrx prx, bool oneway, bool synchronous, IProgress<bool>? progress,
            CancellationToken cancel)
            : base(prx.Communicator, oneway, synchronous, progress, cancel)
        {
            Proxy = prx;
            _cnt = 0;
        }

        protected async ValueTask InvokeImpl(bool retried)
        {
            try
            {
                if (!retried)
                {
                    int invocationTimeout = Proxy.IceReference.InvocationTimeout;
                    if (invocationTimeout > 0)
                    {
                        Communicator.Timer().Schedule(this, invocationTimeout);
                    }
                }
                else if (Observer != null)
                {
                    Observer.Retried();
                }

                while (true)
                {
                    try
                    {
                        IsSent = false;
                        Handler =
                            await Proxy.IceReference.GetRequestHandlerAsync(CancellationToken).ConfigureAwait(false);
                        Handler.SendAsyncRequest(this);
                        return; // We're done!
                    }
                    catch (RetryException)
                    {
                        // Clear request handler and always retry.
                        if (!Proxy.IceReference.IsFixed)
                        {
                            Proxy.IceReference.UpdateRequestHandler(Handler, null);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ChildObserver != null)
                        {
                            ChildObserver.Failed(ex.GetType().FullName ?? "System.Exception");
                            ChildObserver.Detach();
                            ChildObserver = null;
                        }
                        int interval = Proxy.IceHandleException(ex, Handler, IsIdempotent, IsSent, ref _cnt);
                        if (interval > 0)
                        {
                            Communicator.AddRetryTask(this, interval);
                            return;
                        }
                        else if (Observer != null)
                        {
                            Observer.Retried();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                //
                // If called from the user thread we re-throw, the exception will be caught by the caller.
                //
                if (!retried)
                {
                    throw;
                }
                else if (ExceptionImpl(ex)) // No retries, we're done
                {
                    await Task.Run(InvokeException);
                }
            }
        }

        public override bool Sent()
        {
            if (IsOneway)
            {
                if (Proxy.IceReference.InvocationTimeout != -1)
                {
                    Communicator.Timer().Cancel(this);
                }
            }
            return base.Sent();
        }

        protected override bool ExceptionImpl(Exception ex)
        {
            if (Proxy.IceReference.InvocationTimeout != -1)
            {
                Communicator.Timer().Cancel(this);
            }
            return base.ExceptionImpl(ex);
        }

        protected override bool ResponseImpl()
        {
            if (Proxy.IceReference.InvocationTimeout != -1)
            {
                Communicator.Timer().Cancel(this);
            }
            return base.ResponseImpl();
        }

        // TODO: add facet and operation to message
        public void RunTimerTask()
            => Cancel(new TimeoutException($"invocation on remote Ice object `{Proxy.Identity}' timed out"));

        protected IReadOnlyDictionary<string, string> ProxyAndCurrentContext()
        {
            IReadOnlyDictionary<string, string> context;

            if (Proxy.Context.Count == 0)
            {
                context = Proxy.Communicator.CurrentContext;
            }
            else if (Proxy.Communicator.CurrentContext.Count == 0)
            {
                context = Proxy.Context;
            }
            else
            {
                var combinedContext = new Dictionary<string, string>(Proxy.Communicator.CurrentContext);
                foreach ((string key, string value) in Proxy.Context)
                {
                    combinedContext[key] = value;  // the proxy Context entry prevails.
                }
                context = combinedContext;
            }
            return context;
        }
    }

    //
    // Class for handling Slice operation invocations
    //
    public class InvokeOutgoing : ProxyOutgoing
    {
        public OutgoingRequestFrame RequestFrame { get; protected set; }
        public IncomingResponseFrame? ResponseFrame { get; protected set; }
        public override List<ArraySegment<byte>> GetRequestData(int requestId) =>
            Ice1Definitions.GetRequestData(RequestFrame, requestId);

        private readonly TaskCompletionSource<IncomingResponseFrame> _taskCompletionSource;

        public InvokeOutgoing(IObjectPrx prx, OutgoingRequestFrame requestFrame, bool oneway, bool synchronous,
            IProgress<bool>? progress = null, CancellationToken cancel = default)
            : base(prx, oneway, synchronous, progress, cancel)
        {
            RequestFrame = requestFrame;
            IsIdempotent = requestFrame.IsIdempotent;
            _taskCompletionSource = new TaskCompletionSource<IncomingResponseFrame>();
        }

        protected override void SetResult() => _taskCompletionSource.SetResult(ResponseFrame!);

        protected override void SetException(Exception ex) => _taskCompletionSource.SetException(ex);

        public override bool Sent()
        {
            if (IsOneway)
            {
                ResponseFrame = new IncomingResponseFrame(Communicator, Ice1Definitions.EmptyResponsePayload);
            }
            return base.Sent();
        }

        public bool Response(IncomingResponseFrame responseFrame)
        {
            ResponseFrame = responseFrame;

            Debug.Assert(!IsOneway); // Can only be called for twoways.

            if (ChildObserver != null)
            {
                ChildObserver.Reply(ResponseFrame.Size);
                ChildObserver.Detach();
                ChildObserver = null;
            }

            try
            {
                switch (ResponseFrame.ReplyStatus)
                {
                    case ReplyStatus.OK:
                    {
                        break;
                    }
                    case ReplyStatus.UserException:
                    {
                        if (Observer != null)
                        {
                            Observer.RemoteException();
                        }
                        break;
                    }
                    case ReplyStatus.ObjectNotExistException:
                    case ReplyStatus.FacetNotExistException:
                    case ReplyStatus.OperationNotExistException:
                    {
                        throw ResponseFrame.ReadDispatchException();
                    }
                    case ReplyStatus.UnknownException:
                    case ReplyStatus.UnknownLocalException:
                    case ReplyStatus.UnknownUserException:
                    {
                        throw ResponseFrame.ReadUnhandledException();
                    }
                }
                return ResponseImpl();
            }
            catch (Exception ex)
            {
                return Exception(ex);
            }
        }

        public override void InvokeRemote(Connection connection, bool compress) =>
            connection.SendAsyncRequest(this, compress);

        public override void InvokeCollocated(CollocatedRequestHandler handler) =>
            handler.InvokeAsyncRequest(this, Synchronous);

        internal async ValueTask<IncomingResponseFrame> Invoke()
        {
            IReadOnlyDictionary<string, string>? context = RequestFrame.Context ?? ProxyAndCurrentContext();
            Observer = ObserverHelper.GetInvocationObserver(Proxy, RequestFrame.Operation, context);

            InvocationMode mode = Proxy.IceReference.InvocationMode;
            if (mode == InvocationMode.BatchOneway || mode == InvocationMode.BatchDatagram)
            {
                Debug.Assert(false); // not implemented
                return null;
            }

            if (mode == InvocationMode.Datagram && !IsOneway)
            {
                throw new InvalidOperationException("cannot make two-way call on a datagram proxy");
            }

            await InvokeImpl(false).ConfigureAwait(false);
            return await _taskCompletionSource.Task.ConfigureAwait(false);
        }
    }
}
