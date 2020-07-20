//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

namespace ZeroC.Ice.Test.Operations
{
    public class Oneways
    {
        internal static void oneways(IMyClassPrx p)
        {
            p = p.Clone(oneway: true);
            p.IcePing();
            p.OpVoid();
            p.OpIdempotent();
            p.OpOneway();
            p.OpOnewayMetadata();
            p.OpByte(0xff, 0x0f);
        }
    }
}
