//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using Test;
using Ice.threading.Test;
using System.Threading.Tasks;

namespace Ice.threading
{
    public class Collocated : TestHelper
    {
        public override void Run(string[] args)
        {
            using var communicator = Initialize(ref args);

            var adapter = communicator.CreateObjectAdapter("TestAdapter");
            adapter.Add("test", new TestIntf(TaskScheduler.Default));

            var schedulerPair = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 2);

            var adapter2 = communicator.CreateObjectAdapter("TestAdapterExclusiveTS",
                taskScheduler: schedulerPair.ExclusiveScheduler);
            adapter2.Add("test", new TestIntf(schedulerPair.ExclusiveScheduler));

            var adapter3 = communicator.CreateObjectAdapterWithEndpoints("TestAdapteConcurrentTS",
                taskScheduler: schedulerPair.ConcurrentScheduler);
            adapter3.Add("test", new TestIntf(schedulerPair.ConcurrentScheduler));

            _ = AllTests.allTests(this);
        }

        public static int Main(string[] args) => TestDriver.RunTest<Collocated>(args);
    }
}
