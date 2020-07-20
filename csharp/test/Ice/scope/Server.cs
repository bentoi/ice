//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System.Collections.Generic;
using System.Threading.Tasks;
using Test;

namespace ZeroC.Ice.Test.Scope
{
    public class Server : TestHelper
    {
        private class I1 : II
        {
            public (S, S) OpS(S s1, Current current) => (s1, s1);

            public (IEnumerable<S>, IEnumerable<S>) OpSSeq(S[] s1, Current current) => (s1, s1);

            public (IReadOnlyDictionary<string, S>, IReadOnlyDictionary<string, S>)
            OpSMap(Dictionary<string, S> s1, Current current) => (s1, s1);

            public (C?, C?) OpC(C? c1, Current current) => (c1, c1);

            public (IEnumerable<C?>, IEnumerable<C?>) OpCSeq(C?[] c1, Current current) => (c1, c1);

            public (IReadOnlyDictionary<string, C?>, IReadOnlyDictionary<string, C?>)
            OpCMap(Dictionary<string, C?> c1, Current current) => (c1, c1);

            public E1 OpE1(E1 e1, Current current) => e1;

            public S1 OpS1(S1 s1, Current current) => s1;

            public C1? OpC1(C1? c1, Current current) => c1;

            public void Shutdown(Current current) => current.Adapter.Communicator.ShutdownAsync();
        }

        private class I2 : Inner.II
        {
            public (Inner.Inner2.S, Inner.Inner2.S)
            OpS(Inner.Inner2.S s1, Current current) => (s1, s1);

            public (IEnumerable<Inner.Inner2.S>, IEnumerable<Inner.Inner2.S>)
            OpSSeq(Inner.Inner2.S[] s1, Current current) => (s1, s1);

            public (IReadOnlyDictionary<string, Inner.Inner2.S>, IReadOnlyDictionary<string, Inner.Inner2.S>)
            OpSMap(Dictionary<string, Inner.Inner2.S> s1, Current current) => (s1, s1);

            public (Inner.Inner2.C?, Inner.Inner2.C?)
            OpC(Inner.Inner2.C? c1, Current current) => (c1, c1);

            public (IEnumerable<Inner.Inner2.C?>, IEnumerable<Inner.Inner2.C?>)
            OpCSeq(Inner.Inner2.C?[] c1, Current current) => (c1, c1);

            public (IReadOnlyDictionary<string, Inner.Inner2.C?>, IReadOnlyDictionary<string, Inner.Inner2.C?>)
            OpCMap(Dictionary<string, Inner.Inner2.C?> c1, Current current) => (c1, c1);

            public void Shutdown(Current current) => current.Adapter.Communicator.ShutdownAsync();
        }

        private class I3 : Inner.Inner2.II
        {
            public (Inner.Inner2.S, Inner.Inner2.S)
            OpS(Inner.Inner2.S s1, Current current) => (s1, s1);

            public (IEnumerable<Inner.Inner2.S>, IEnumerable<Inner.Inner2.S>)
            OpSSeq(Inner.Inner2.S[] s1, Current current) => (s1, s1);

            public (IReadOnlyDictionary<string, Inner.Inner2.S>, IReadOnlyDictionary<string, Inner.Inner2.S>)
            OpSMap(Dictionary<string, Inner.Inner2.S> s1, Current current) => (s1, s1);

            public (Inner.Inner2.C?, Inner.Inner2.C?)
            OpC(Inner.Inner2.C? c1, Current current) => (c1, c1);

            public (IEnumerable<Inner.Inner2.C?>, IEnumerable<Inner.Inner2.C?>)
            OpCSeq(Inner.Inner2.C?[] c1, Current current) => (c1, c1);

            public (IReadOnlyDictionary<string, Inner.Inner2.C?>, IReadOnlyDictionary<string, Inner.Inner2.C?>)
            OpCMap(Dictionary<string, Inner.Inner2.C?> c1, Current current) => (c1, c1);

            public void Shutdown(Current current) => current.Adapter.Communicator.ShutdownAsync();
        }

        private class I4 : Inner.Test.Inner2.II
        {
            public (S, S) OpS(S s1, Current current) => (s1, s1);

            public (IEnumerable<S>, IEnumerable<S>) OpSSeq(S[] s1, Current current) => (s1, s1);

            public (IReadOnlyDictionary<string, S>, IReadOnlyDictionary<string, S>)
            OpSMap(Dictionary<string, S> s1, Current current) => (s1, s1);

            public (C?, C?) OpC(C? c1, Current current) => (c1, c1);

            public (IEnumerable<C?>, IEnumerable<C?>) OpCSeq(C?[] c1, Current current) => (c1, c1);

            public (IReadOnlyDictionary<string, C?>, IReadOnlyDictionary<string, C?>)
            OpCMap(Dictionary<string, C?> c1, Current current) => (c1, c1);

            public void Shutdown(Current current) => current.Adapter.Communicator.ShutdownAsync();
        }

        public override async Task RunAsync(string[] args)
        {
            await using Communicator communicator = Initialize(ref args);
            communicator.SetProperty("TestAdapter.Endpoints", GetTestEndpoint(0));
            ObjectAdapter adapter = communicator.CreateObjectAdapter("TestAdapter");
            adapter.Add("i1", new I1());
            adapter.Add("i2", new I2());
            adapter.Add("i3", new I3());
            adapter.Add("i4", new I4());
            await adapter.ActivateAsync();
            ServerReady();
            await communicator.WaitForShutdownAsync();
        }

        public static Task<int> Main(string[] args) => TestDriver.RunTestAsync<Server>(args);
    }
}
