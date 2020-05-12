//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#pragma once

#include <Ice/BuiltinSequences.ice>

[[java:package:test.Ice.exceptions]]
module Test
{

interface Empty
{
}

interface Thrower;

exception A
{
    int aMem;
}

exception B : A
{
    int bMem;
}

exception C : B
{
    int cMem;
}

exception D
{
    int dMem;
}

interface Thrower
{
    void shutdown();
    bool supportsUndeclaredExceptions();
    bool supportsAssertException();

    void throwAasA(int a) throws A;
    void throwAorDasAorD(int a) throws A, D;
    void throwBasA(int a, int b) throws A;
    void throwCasA(int a, int b, int c) throws A;
    void throwBasB(int a, int b) throws B;
    void throwCasB(int a, int b, int c) throws B;
    [java:UserException] void throwCasC(int a, int b, int c) throws C;

    void throwUndeclaredA(int a);
    void throwUndeclaredB(int a, int b);
    [java:UserException] void throwUndeclaredC(int a, int b, int c);
    void throwLocalException();
    void throwNonIceException();
    void throwAssertException();
    Ice::ByteSeq throwMemoryLimitException(Ice::ByteSeq seq);

    idempotent void throwLocalExceptionIdempotent();

    void throwAfterResponse();
    void throwAfterException() throws A;
}

interface WrongOperation
{
    void noSuchOperation();
}

}
