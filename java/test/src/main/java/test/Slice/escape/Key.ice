//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#pragma once

[[java:package:test.Slice.escape]]

module abstract
{

enum assert
{
    boolean
}

struct break
{
    int case;
}

interface catch
{
    [amd] void checkedCast(int clone, out int continue);
}

interface default
{
    void do();
}

class else
{
    int if;
    default* equals;
    int final;
}

interface finalize : default, catch
{
}
sequence<assert> for;
dictionary<string, assert> goto;

exception hashCode
{
    int if;
}

exception import : hashCode
{
    int instanceof;
    int native;
}

const int switch = 0;
const int synchronized = 0;
const int this = 0;
const int throw = 0;
const int toString = 0;
const int try = 0;
const int uncheckedCast = 0;
const int volatile = 0;
const int wait = 0;
const int while = 0;
const int finally = 0;
const int getClass = 0;

}
