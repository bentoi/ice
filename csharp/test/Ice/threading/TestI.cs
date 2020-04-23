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
                throw new TestFailed("unexpected task scheduler");
            }
        }

        public async ValueTask pingAsync(Current current)
        {
            if (TaskScheduler.Current != _scheduler)
            {
                throw new TestFailed("unexpected task scheduler");
            }
            await Task.Delay(1);
            if (TaskScheduler.Current != _scheduler)
            {
                throw new TestFailed("unexpected task scheduler");
            }
        }

        public void shutdown(Current current) => current.Adapter.Communicator.Shutdown();
    }
}
