//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using Test;
using Ice.threading.Test;

namespace Ice.threading
{
    public class AllTests
    {
        public static async ValueTask allTestsWithCommunicator(TestHelper helper, Ice.Communicator communicator)
        {
            System.IO.TextWriter output = helper.GetWriter();

            // Scheduler expected to be the current scheduler for continuations
            TaskScheduler scheduler = communicator.TaskScheduler ?? TaskScheduler.Default;

            TestHelper.Assert(TaskScheduler.Current == scheduler);

            // Run tests on the 3 server endpoints where each endpoint matches an object adapter with different
            // scheduler settings
            for(int i = 0; i < 3; ++i)
            {
                try
                {
                    var proxy = ITestIntfPrx.Parse("test:" + helper.GetTestEndpoint(i), communicator);

                    proxy.pingSync();
                    proxy.ping();

                    await proxy.pingSyncAsync();
                    TestHelper.Assert(TaskScheduler.Current == scheduler);
                    await proxy.pingAsync();
                    TestHelper.Assert(TaskScheduler.Current == scheduler);

                    Action<Task> checkScheduler = t =>
                    {
                        if(TaskScheduler.Current != scheduler)
                        {
                            throw new TestFailedException("unexpected scheduler");
                        }
                    };

                    await proxy.pingSyncAsync().ContinueWith(checkScheduler,
                        TaskContinuationOptions.ExecuteSynchronously).ConfigureAwait(false);
                    TestHelper.Assert(TaskScheduler.Current == TaskScheduler.Default);
                    await proxy.pingAsync().ContinueWith(checkScheduler,
                        TaskContinuationOptions.ExecuteSynchronously).ConfigureAwait(false);
                    TestHelper.Assert(TaskScheduler.Current == TaskScheduler.Default);
                }
                catch (TestFailedException ex)
                {
                    output.WriteLine("test failed on the server side: " + ex.reason);
                    TestHelper.Assert(false);
                }
            }
        }

        public static async ValueTask<ITestIntfPrx> allTests(TestHelper helper)
        {
            Communicator communicator = helper.Communicator()!;
            TestHelper.Assert(communicator != null);

            var schedulers = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 2);
            var properties = communicator.GetProperties();

            // Use the Default task scheduler
            System.IO.TextWriter output = helper.GetWriter();
            output.Write("testing with default task scheduler... ");
            TestHelper.Assert(communicator.TaskScheduler == null);
            await allTestsWithCommunicator(helper, communicator);
            output.WriteLine("ok");

            // Use the concurrent task scheduler
            output.Write("testing with concurrent task scheduler... ");
            using (var comm = new Communicator(properties, taskScheduler: schedulers.ConcurrentScheduler))
            {
                TestHelper.Assert(comm.TaskScheduler == schedulers.ConcurrentScheduler);
                _ = Task.Factory.StartNew(async () => await allTestsWithCommunicator(helper, comm), default,
                    TaskCreationOptions.None, comm.TaskScheduler);
            }
            output.WriteLine("ok");

            // Use the exclusive task scheduler
            output.Write("testing with exclusive task scheduler... ");
            using (var comm = new Communicator(properties, taskScheduler: schedulers.ExclusiveScheduler))
            {
                TestHelper.Assert(comm.TaskScheduler == schedulers.ExclusiveScheduler);
                _ = Task.Factory.StartNew(async () => await allTestsWithCommunicator(helper, comm), default,
                    TaskCreationOptions.None, comm.TaskScheduler);
            }
            output.WriteLine("ok");

            return ITestIntfPrx.Parse("test:" + helper.GetTestEndpoint(0), communicator);
        }
    }
}
