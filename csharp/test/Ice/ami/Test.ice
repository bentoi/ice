//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#pragma once

#include <Ice/BuiltinSequences.ice>
#include <Ice/Identity.ice>

[[suppress-warning:reserved-identifier]]

module ZeroC::Ice::Test::AMI
{

exception TestIntfException
{
}

enum CloseMode
{
    Forcefully,
    Gracefully,
    GracefullyWithWait
}

interface TestIntf
{
    void op();
    void opWithPayload(Ice::ByteSeq seq);
    int opWithResult();
    void opWithUE()
        throws TestIntfException;
    void close(CloseMode mode);
    void sleep(int ms);
    [amd] void startDispatch();
    void finishDispatch();
    void shutdown();

    bool supportsAMD();
    bool supportsFunctionalTests();

    [amd] void opAsyncDispatch();
    [amd] int opWithResultAsyncDispatch();
    [amd] void opWithUEAsyncDispatch()
        throws TestIntfException;

    int set(int value);
}

module Outer::Inner
{
    interface TestIntf
    {
        int op(int i, out int j);
    }
}

}
