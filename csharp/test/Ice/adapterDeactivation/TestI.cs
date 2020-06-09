//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

namespace ZeroC.Ice.Test.AdapterDeactivation
{
    public sealed class TestIntf : ITestIntf
    {
        public void transient(Current current)
        {
            ObjectAdapter adapter = current.Adapter.Communicator.CreateObjectAdapterWithEndpoints(
                "TransientTestAdapter", "default");
            adapter.Activate();
            adapter.Destroy();
        }

        public void deactivate(Current current)
        {
            current.Adapter.Deactivate();
            System.Threading.Thread.Sleep(100);
        }
    }
}
