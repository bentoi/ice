//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#pragma once

[[normalize-case]]
[[suppress-warning:reserved-identifier]]

module ZeroC::Ice::Test::DefaultServant
{
    interface MyObject
    {
        string getName();
    }
}
