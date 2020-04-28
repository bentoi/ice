//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Test;
using Ice.threading.Test;

namespace Ice.threading
{
    public class AllTests
    {
        class Progress : IProgress<bool>
        {
            public TaskScheduler? Scheduler { get; private set; }
            private ManualResetEvent _event = new ManualResetEvent(false);

            public void WaitSent()
            {
                _event.WaitOne();
            }

            void IProgress<bool>.Report(bool value)
            {
                Scheduler = TaskScheduler.Current;
                _event.Set();
            }
        }
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
                var proxy = ITestIntfPrx.Parse("test:" + helper.GetTestEndpoint(i), communicator);

                proxy.pingSync();
                proxy.ping();

                await proxy.pingSyncAsync();
                TestHelper.Assert(TaskScheduler.Current == scheduler);
                await proxy.pingAsync();
                TestHelper.Assert(TaskScheduler.Current == scheduler);

                // The continuation set with ContinueWith is expected to be ran on the communicator's
                // task scheduler, ditto for the IProgress<bool> sent callbacks.
                Action<Task> checkScheduler = t =>
                {
                    if(TaskScheduler.Current != scheduler)
                    {
                        throw new TestFailedException("unexpected scheduler");
                    }
                };
                Progress progress;

                progress = new Progress();
                await proxy.pingSyncAsync(progress: progress).ContinueWith(checkScheduler,
                    TaskContinuationOptions.ExecuteSynchronously).ConfigureAwait(false);
                progress.WaitSent();
                TestHelper.Assert(progress.Scheduler == scheduler);
                // The continuation of the awaitable setup with ConfigureAwait(false) is ran by the default
                // scheduler, not the communicator's scheduler.
                TestHelper.Assert(TaskScheduler.Current == TaskScheduler.Default);

                progress = new Progress();
                await proxy.pingAsync(progress: progress).ContinueWith(checkScheduler,
                    TaskContinuationOptions.ExecuteSynchronously).ConfigureAwait(false);
                progress.WaitSent();
                TestHelper.Assert(progress.Scheduler == scheduler);
                // The continuation of the awaitable setup with ConfigureAwait(false) is ran by the default
                // scheduler, not the communicator's scheduler.
                TestHelper.Assert(TaskScheduler.Current == TaskScheduler.Default);
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
            output.Write("testing continuations with default task scheduler... ");
            TestHelper.Assert(communicator.TaskScheduler == null);
            await allTestsWithCommunicator(helper, communicator);
            output.WriteLine("ok");

            // Use the concurrent task scheduler
            output.Write("testing continuations with concurrent task scheduler... ");
            using (var comm = new Communicator(properties, taskScheduler: schedulers.ConcurrentScheduler))
            {
                TestHelper.Assert(comm.TaskScheduler == schedulers.ConcurrentScheduler);
                _ = Task.Factory.StartNew(async () => await allTestsWithCommunicator(helper, comm), default,
                    TaskCreationOptions.None, comm.TaskScheduler);
            }
            output.WriteLine("ok");

            // Use the exclusive task scheduler
            output.Write("testing continuations with exclusive task scheduler... ");
            using (var comm = new Communicator(properties, taskScheduler: schedulers.ExclusiveScheduler))
            {
                TestHelper.Assert(comm.TaskScheduler == schedulers.ExclusiveScheduler);
                _ = Task.Factory.StartNew(async () => await allTestsWithCommunicator(helper, comm), default,
                    TaskCreationOptions.None, comm.TaskScheduler);
            }
            output.WriteLine("ok");

            output.Write("testing server-side default task scheduler concurrency... ");
            {
                // With the default task scheduler, the concurrency is limited to the number of .NET thread pool
                // threads. The server sets up at least 50 threads in the .NET Thread pool we test this level
                // of concurrency but in theory it could be much higher.
                var proxy = ITestIntfPrx.Parse("test:" + helper.GetTestEndpoint(0), communicator);
                Task.WaitAll(Enumerable.Range(0, 200).Select(idx => proxy.concurrentAsync(20)).ToArray());
            }
            output.WriteLine("ok");

            output.Write("testing server-side exclusive task scheduler... ");
            {
                // With the exclusive task scheduler, at most one request can be dispatched concurrently.
                var proxy = ITestIntfPrx.Parse("test:" + helper.GetTestEndpoint(1), communicator);
                Task.WaitAll(Enumerable.Range(0, 10).Select(idx => proxy.concurrentAsync(1)).ToArray());
            }
            output.WriteLine("ok");

            output.Write("testing server-side concurrent task scheduler... ");
            {
                // With the concurrent task scheduler, at most 5 requests can be dispatched concurrently (this is
                // configured on the server side).
                var proxy = ITestIntfPrx.Parse("test:" + helper.GetTestEndpoint(2), communicator);
                Task.WaitAll(Enumerable.Range(0, 20).Select(idx => proxy.concurrentAsync(5)).ToArray());
            }
            output.WriteLine("ok");

            return ITestIntfPrx.Parse("test:" + helper.GetTestEndpoint(0), communicator);
        }
    }
}
