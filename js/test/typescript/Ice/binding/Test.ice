//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#pragma once

[[js:es6-module]]

module Test
{

interface TestIntf
{
    string getAdapterName();
}

interface RemoteObjectAdapter
{
    TestIntf* getTestIntf();

    void deactivate();
}

interface RemoteCommunicator
{
    RemoteObjectAdapter* createObjectAdapter(string name, string endpoints);

    void deactivateObjectAdapter(RemoteObjectAdapter* adapter);

    void shutdown();
}

}
