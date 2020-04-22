//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using System.Diagnostics;
using System.Collections.Generic;
using Test;
using Ice.perf.Test;

namespace Ice.perf
{
    public class AllTests
    {
    //     class Tester
    //     {
    //         private System.IO.TextWriter _out;
    //         private Stopwatch _watch = new Stopwatch();
    //         Tester(System.IO.TextWriter out)
    //         {
    //             _out = out;
    //         }

    //     };

        public static void RunTest(System.IO.TextWriter output, int repetitions, string name, Action invocation,
            Action warmUpInvocation)
        {
            output.Write($"testing {name}... ");
            output.Flush();
            for (int i = 0; i < 10000; i++)
            {
                warmUpInvocation();
            }
            var watch = new Stopwatch();
            watch.Start();
            for (int i = 0; i < repetitions; i++)
            {
                invocation();
            }
            watch.Stop();
            output.WriteLine($"{watch.ElapsedMilliseconds / (float)repetitions}ms");
        }

        public static void RunTest(System.IO.TextWriter output, int repetitions, string name, Action invocation)
        {
            RunTest(output, repetitions, name, invocation, invocation);
        }

        public static void RunTest<T>(System.IO.TextWriter output, int repetitions, string name,
            Action<ReadOnlyMemory<T>> invocation, int size) where T : struct
        {
            var seq = new T[size];
            var emptySeq = new T[0];
            RunTest(output, repetitions, name, () => invocation(seq), () => invocation(emptySeq));
        }

        public static void RunTest<T>(System.IO.TextWriter output, int repetitions, string name,
            Func<int, IEnumerable<T>> invocation, int size) where T : struct
        {
            RunTest(output, repetitions, name, () => invocation(size), () => invocation(0));
        }

        public static IPerformancePrx allTests(TestHelper helper)
        {
            Communicator communicator = helper.Communicator()!;
            TestHelper.Assert(communicator != null);
            System.IO.TextWriter output = helper.GetWriter();

#if DEBUG
            output.WriteLine("warning: runnig performance test built with DEBUG");
#endif

            var perf = IPerformancePrx.Parse("perf:" + helper.GetTestEndpoint(0), communicator);

            RunTest(output, 100000, "latency", () => perf.IcePing());
            RunTest<byte>(output, 1000, "sending byte sequence", v => perf.sendBytes(v), ByteSeqSize.value);
            RunTest<byte>(output, 1000, "received byte sequence", sz => perf.receiveBytes(sz), ByteSeqSize.value);

            return perf;
        }
    }
}
