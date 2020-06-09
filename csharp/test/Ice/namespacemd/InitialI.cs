//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

namespace ZeroC.Ice.Test.NamespaceMD
{
    public class Initial : IInitial
    {
        public NoNamespace.C1 getNoNamespaceC2AsC1(Current current) => new NoNamespace.C2();

        public NoNamespace.C2 getNoNamespaceC2AsC2(Current current) => new NoNamespace.C2();

        public WithNamespace.C1 getWithNamespaceC2AsC1(Current current) => new WithNamespace.C2();

        public WithNamespace.C2 getWithNamespaceC2AsC2(Current current) => new WithNamespace.C2();

        public void shutdown(Current current) => current.Adapter.Communicator.Shutdown();

        public void throwNoNamespaceE2AsE1(Current current) => throw new NoNamespace.E2();

        public void throwNoNamespaceE2AsE2(Current current) => throw new NoNamespace.E2();

        public void throwNoNamespaceNotify(Current current) => throw new NoNamespace.@notify();

        public void throwWithNamespaceE2AsE1(Current current) => throw new WithNamespace.E2();

        public void throwWithNamespaceE2AsE2(Current current) => throw new WithNamespace.E2();
    }
}
