//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

namespace ZeroC.Ice
{
    public interface ICancellationHandler
    {
        void AsyncRequestCanceled(Outgoing outgoing, System.Exception ex);
    }

    public interface IRequestHandler : ICancellationHandler
    {
        void SendRequestAsync(InvokeOutgoing outgoing);

        Connection? GetConnection();
    }
}
