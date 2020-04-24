//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using Ice;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ice.threading.Test;

namespace Ice.threading
{
    public sealed class TestIntf : ITestIntf
    {
        private TaskScheduler _scheduler;

        public TestIntf(TaskScheduler scheduler)
        {
            _scheduler = scheduler;
        }

        public void pingSync(Current current)
        {
            if (TaskScheduler.Current != _scheduler)
            {
                throw new TestFailedException("unexpected task scheduler from pingSync");
            }
        }

        public async ValueTask pingAsync(Current current)
        {
            if (TaskScheduler.Current != _scheduler)
            {
                throw new TestFailedException("unexpected task scheduler from pingAsync before await");
            }
            await Task.Delay(1);
            if (TaskScheduler.Current != _scheduler)
            {
                throw new TestFailedException("unexpected task scheduler from pingAsync after await");
            }
        }

        public void shutdown(Current current) => current.Adapter.Communicator.Shutdown();
    }
}
