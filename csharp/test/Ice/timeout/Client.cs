//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

namespace Ice.timeout
{
    public class Client : global::Test.TestHelper
    {
        public override void Run(string[] args)
        {
            var properties = CreateTestProperties(ref args);

            //
            // For this test, we want to disable retries.
            //
            properties["Ice.RetryIntervals"] = "-1";

            //
            // This test kills connections, so we don't want warnings.
            //
            properties["Ice.Warn.Connections"] = "0";

            //
            // Limit the send buffer size, this test relies on the socket
            // send() blocking after sending a given amount of data.
            //
            properties["Ice.TCP.SndSize"] = "50000";
            using var communicator = Initialize(properties);
            AllTests.allTests(this);
        }

        public static int Main(string[] args) => global::Test.TestDriver.RunTest<Client>(args);
    }
}
