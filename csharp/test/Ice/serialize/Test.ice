//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#pragma once

[[3.7]]

#include <Ice/BuiltinSequences.ice>

[[cs:typeid-namespace:ZeroC.Ice.serialize.TypeId]]

[cs:namespace:ZeroC.Ice.serialize]
module Test
{

enum MyEnum
{
    enum1,
    enum2,
    enum3
}

class MyClass;

struct ValStruct
{
    bool bo;
    byte by;
    short sh;
    int i;
    long l;
    MyEnum e;
}

interface MyInterface
{
    void op();
}

sequence<MyInterface*> ProxySeq;

[clr:property]
struct RefStruct
{
    string s;
    string sp;
    MyClass c;
    MyInterface* p;
    ProxySeq seq;
}

sequence<ValStruct> ValStructS;
[clr:generic:List]
sequence<ValStruct> ValStructList;
[clr:generic:LinkedList]
sequence<ValStruct> ValStructLinkedList;
[clr:generic:Stack]
sequence<ValStruct> ValStructStack;
[clr:generic:Queue]
sequence<ValStruct> ValStructQueue;

dictionary<int, string> IntStringD;
dictionary<int, ValStruct> IntValStructD;
dictionary<int, MyInterface*> IntProxyD;
[clr:generic:SortedDictionary]
dictionary<int, string> IntStringSD;

class Base
{
    bool bo;
    byte by;
    short sh;
    int i;
    long l;
    MyEnum e;
}

class MyClass : Base
{
    MyClass c;
    Object o;
    ValStruct s;
}

exception MyException
{
    string name;
    byte b;
    short s;
    int i;
    long l;
    ValStruct vs;
    RefStruct rs;
    MyClass c;
    MyInterface* p;

    ValStructS vss;
    ValStructList vsl;
    ValStructLinkedList vsll;
    ValStructStack vssk;
    ValStructQueue vsq;

    IntStringD isd;
    IntValStructD ivd;
    IntProxyD ipd;
    IntStringSD issd;

    tag(1) string? optName;
    tag(2) int? optInt;
    tag(3) ValStruct? optValStruct;
    tag(4) RefStruct? optRefStruct;
    tag(5) MyEnum? optEnum;
    tag(6) MyClass? optClass;
    tag(7) MyInterface* optProxy;
}

}
