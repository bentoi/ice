//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using Test;
using Ice.threading.Test;
using System.Threading.Tasks;

namespace Ice.threading
{
    public class Server : TestHelper
    {
        public override void Run(string[] args)
        {
            using var communicator = Initialize(ref args);

            var adapter = communicator.CreateObjectAdapterWithEndpoints("TestAdapter", GetTestEndpoint(0));
            adapter.Add("test", new TestIntf(TaskScheduler.Default));
            adapter.Activate();

            var schedulerPair = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 2);

            var adapter2 = communicator.CreateObjectAdapterWithEndpoints("TestAdapterExclusiveTS", GetTestEndpoint(1),
                taskScheduler: schedulerPair.ExclusiveScheduler);
            adapter2.Add("test", new TestIntf(schedulerPair.ExclusiveScheduler));
            adapter2.Activate();

            var adapter3 = communicator.CreateObjectAdapterWithEndpoints("TestAdapteConcurrentTS", GetTestEndpoint(2),
                taskScheduler: schedulerPair.ConcurrentScheduler);
            adapter3.Add("test", new TestIntf(schedulerPair.ConcurrentScheduler));
            adapter3.Activate();

            ServerReady();
            communicator.WaitForShutdown();
        }

        public static int Main(string[] args) => TestDriver.RunTest<Server>(args);
    }
}
