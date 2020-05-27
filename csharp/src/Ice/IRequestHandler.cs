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
        void SendRequestAsync(InvokeOutgoing outgoing);

        ZeroC.Ice.Connection? GetConnection();
    }
}
