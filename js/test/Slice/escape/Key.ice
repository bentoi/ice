//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#pragma once

module await
{

enum var
{
    base
}

struct break
{
    int while;
}

interface case
{
    [amd] void catch(int checked, out int continue);
}

interface typeof
{
    void default();
}

class delete
{
    int if;
    case* else;
    int export;
}

interface explicit : typeof, case
{
}

dictionary<string, break> while;

class package
{
    tag(1) break? for;
    tag(2) var? goto;
    tag(3) explicit* if;
    tag(5) while? internal;
    tag(7) string? debugger;
    tag(8) explicit* null;
}

interface optionalParams
{
    tag(1) break? for(tag(2) var? goto,
                          tag(3) explicit* if,
                          tag(5) while? internal,
                          tag(7) string? namespace,
                          tag(8) explicit* null);

    [amd]
    tag(1) break? continue(tag(2) var? goto,
                               tag(3) explicit* if,
                               tag(5) while? internal,
                               tag(7) string? namespace,
                               tag(8) explicit* null);

    tag(1) break? in(out tag(2) var? goto,
                         out tag(3) explicit* if,
                         out tag(5) while? internal,
                         out tag(7) string? namespace,
                         out tag(8) explicit* null);

    [amd]
    tag(1) break? foreach(out tag(2) var? goto,
                              out tag(3) explicit* if,
                              out tag(5) while? internal,
                              out tag(7) string? namespace,
                              out tag(8) explicit* null);
}

exception fixed
{
    int for;
}

exception foreach : fixed
{
    int goto;
    int if;
}

exception BaseMethods
{
    int Data;
    int HelpLink;
    int InnerException;
    int Message;
    int Source;
    int StackTrace;
    int TargetSite;
    int HResult;
    int Equals;
    int GetBaseException;
    int GetHashCode;
    int GetObjectData;
    int GetType;
    int ReferenceEquals;
    int ToString;
}

const int protected = 0;
const int public = 0;

//
// System as inner module.
//
module System
{

interface Test
{
    void op();
}

}

}

//
// System as outer module.
//
module System
{

interface Test
{
    void op();
}

}
