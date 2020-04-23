//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using Test;

namespace Ice.threading
{
    public class Client : TestHelper
    {
        public override void Run(string[] args)
        {
            using var communicator = Initialize(ref args);
            AllTests.allTests(this).Result.shutdown();
        }

        public static int Main(string[] args) => TestDriver.RunTest<Client>(args);
    }
}
