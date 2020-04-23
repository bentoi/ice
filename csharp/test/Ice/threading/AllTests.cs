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
        public static async ValueTask<ITestIntfPrx> allTests(TestHelper helper)
        {
            Communicator communicator = helper.Communicator()!;
            TestHelper.Assert(communicator != null);
            System.IO.TextWriter output = helper.GetWriter();

            {
                var proxy = ITestIntfPrx.Parse("test:" + helper.GetTestEndpoint(0), communicator);
                proxy.pingSync();
                await proxy.pingAsync();
            }

            {
                var proxy = ITestIntfPrx.Parse("test:" + helper.GetTestEndpoint(1), communicator);
                proxy.pingSync();
                await proxy.pingAsync();
            }

            {
                var proxy = ITestIntfPrx.Parse("test:" + helper.GetTestEndpoint(2), communicator);
                proxy.ping();
                await proxy.pingAsync();
            }

            return ITestIntfPrx.Parse("test:" + helper.GetTestEndpoint(0), communicator);
        }
    }
}
