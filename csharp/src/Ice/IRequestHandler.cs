//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

namespace IceInternal
{
    public interface ICancellationHandler
    {
        void AsyncRequestCanceled(Outgoing outgoing, System.Exception ex);
    }

    public interface IRequestHandler : ICancellationHandler
    {
        IRequestHandler? Update(IRequestHandler previousHandler, IRequestHandler? newHandler);

        void SendAsyncRequest(ProxyOutgoing outgoing);

        ZeroC.Ice.Connection? GetConnection();
    }
}
