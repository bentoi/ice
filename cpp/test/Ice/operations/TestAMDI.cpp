//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#include <Ice/Ice.h>
#include <TestAMDI.h>
#include <TestHelper.h>
#include <functional>
#include <iterator>

using namespace Ice;
using namespace Test;

using namespace std;

MyDerivedClassI::MyDerivedClassI() : _opByteSOnewayCallCount(0)
{
}

bool
MyDerivedClassI::ice_isA(string id, const Ice::Current& current) const
{
    test(current.mode == OperationMode::Nonmutating);
    return Test::MyDerivedClass::ice_isA(move(id), current);
}

void
MyDerivedClassI::ice_ping(const Ice::Current& current) const
{
    test(current.mode == OperationMode::Nonmutating);
    Test::MyDerivedClass::ice_ping(current);

}

std::vector<std::string>
MyDerivedClassI::ice_ids(const Ice::Current& current) const
{
    test(current.mode == OperationMode::Nonmutating);
    return Test::MyDerivedClass::ice_ids(current);
}

std::string
MyDerivedClassI::ice_id(const Ice::Current& current) const
{
    test(current.mode == OperationMode::Nonmutating);
    return Test::MyDerivedClass::ice_id(current);
}

class Thread_opVoid : public IceUtil::Thread
{
public:

    Thread_opVoid(function<void()> response) :
        _response(move(response))
    {
    }

    virtual void run()
    {
        _response();
    }

private:

    function<void()> _response;
};

void
MyDerivedClassI::shutdownAsync(function<void()> response,
                               function<void(exception_ptr)>,
                               const Ice::Current& current)
{
    {
        IceUtil::Mutex::Lock sync(_opVoidMutex);
        if(_opVoidThread)
        {
            _opVoidThread->getThreadControl().join();
            _opVoidThread = 0;
        }
    }

    current.adapter->getCommunicator()->shutdown();
    response();
}

void
MyDerivedClassI::supportsCompressAsync(std::function<void(bool)> response,
                                       std::function<void(std::exception_ptr)>, const Ice::Current&)
{
    response(true);
}

void
MyDerivedClassI::opVoidAsync(function<void()> response,
                             function<void(exception_ptr)>,
                             const Ice::Current& current)
{
    test(current.mode == OperationMode::Normal);

    IceUtil::Mutex::Lock sync(_opVoidMutex);
    if(_opVoidThread)
    {
        _opVoidThread->getThreadControl().join();
        _opVoidThread = 0;
    }

    _opVoidThread = new Thread_opVoid(response);
    _opVoidThread->start();
}

void
MyDerivedClassI::opByteAsync(Ice::Byte p1,
                             Ice::Byte p2,
                             function<void(Ice::Byte, Ice::Byte)> response,
                             function<void(exception_ptr)>,
                             const Ice::Current&)
{
    response(p1, p1 ^ p2);
}

void
MyDerivedClassI::opBoolAsync(bool p1,
                             bool p2,
                             function<void(bool, bool)> response,
                             function<void(exception_ptr)>,
                             const Ice::Current&)
{
    response(p2, p1);
}

void
MyDerivedClassI::opShortIntLongAsync(short p1,
                                     int p2,
                                     long long int p3,
                                     function<void(long long int, short, int, long long int)> response,
                                     function<void(exception_ptr)>,
                                     const Ice::Current&)
{
    response(p3, p1, p2, p3);
}

void
MyDerivedClassI::opFloatDoubleAsync(float p1,
                                    double p2,
                                    function<void(double, float, double)> response,
                                    function<void(exception_ptr)>,
                                    const Ice::Current&)
{
    response(p2, p1, p2);
}

void
MyDerivedClassI::opStringAsync(string p1,
                               string p2,
                               function<void(const string&, const string&)> response,
                               function<void(exception_ptr)>,
                               const Ice::Current&)
{
    response(p1 + " " + p2, p2 + " " + p1);
}

void
MyDerivedClassI::opMyEnumAsync(Test::MyEnum p1,
                               function<void(Test::MyEnum, Test::MyEnum)> response,
                               function<void(exception_ptr)>,
                               const Ice::Current&)
{
    response(MyEnum::enum3, p1);
}

void
MyDerivedClassI::opMyClassAsync(shared_ptr<Test::MyClassPrx> p1,
                                function<void(const shared_ptr<Test::MyClassPrx>&,
                                               const shared_ptr<Test::MyClassPrx>&,
                                               const shared_ptr<Test::MyClassPrx>&)> response,
                                function<void(exception_ptr)>,
                                const Ice::Current& current)
{
    auto p2 = p1;
    auto p3 = uncheckedCast<Test::MyClassPrx>(current.adapter->createProxy(
                                                  stringToIdentity("noSuchIdentity")));
    response(uncheckedCast<Test::MyClassPrx>(current.adapter->createProxy(current.id)), p2, p3);
}

void
MyDerivedClassI::opStructAsync(Test::Structure p1,
                               Test::Structure p2,
                               function<void(const Test::Structure&, const Test::Structure&)> response,
                               function<void(exception_ptr)>,
                               const Ice::Current&)
{
    Test::Structure p3 = p1;
    p3.s.s = "a new string";
    response(p2, p3);
}

void
MyDerivedClassI::opByteSAsync(Test::ByteS p1,
                              Test::ByteS p2,
                              function<void(const Test::ByteS&, const Test::ByteS&)> response,
                              function<void(exception_ptr)>,
                              const Ice::Current&)
{
    Test::ByteS p3;
    p3.resize(p1.size());
    std::reverse_copy(p1.begin(), p1.end(), p3.begin());
    Test::ByteS r = p1;
    std::copy(p2.begin(), p2.end(), std::back_inserter(r));
    response(r, p3);
}

void
MyDerivedClassI::opBoolSAsync(Test::BoolS p1,
                              Test::BoolS p2,
                              function<void(const Test::BoolS&, const Test::BoolS&)> response,
                              function<void(exception_ptr)>,
                              const Ice::Current&)
{
    Test::BoolS p3 = p1;
    std::copy(p2.begin(), p2.end(), std::back_inserter(p3));
    Test::BoolS r;
    r.resize(p1.size());
    std::reverse_copy(p1.begin(), p1.end(), r.begin());
    response(r, p3);
}

void
MyDerivedClassI::opShortIntLongSAsync(Test::ShortS p1,
                                      Test::IntS p2,
                                      Test::LongS p3,
                                      function<void(const Test::LongS&,
                                                     const Test::ShortS&,
                                                     const Test::IntS&,
                                                     const Test::LongS&)> response,
                                      function<void(exception_ptr)>,
                                      const Ice::Current&)
{
    Test::ShortS p4 = p1;
    Test::IntS p5;
    p5.resize(p2.size());
    std::reverse_copy(p2.begin(), p2.end(), p5.begin());
    Test::LongS p6 = p3;
    std::copy(p3.begin(), p3.end(), std::back_inserter(p6));
    response(p3, p4, p5, p6);
}

void
MyDerivedClassI::opFloatDoubleSAsync(Test::FloatS p1,
                                     Test::DoubleS p2,
                                     function<void(const Test::DoubleS&,
                                                    const Test::FloatS&,
                                                    const Test::DoubleS&)> response,
                                     function<void(exception_ptr)>,
                                     const Ice::Current&)
{
    Test::FloatS p3 = p1;
    Test::DoubleS p4;
    p4.resize(p2.size());
    std::reverse_copy(p2.begin(), p2.end(), p4.begin());
    Test::DoubleS r = p2;
    std::copy(p1.begin(), p1.end(), std::back_inserter(r));
    response(r, p3, p4);
}

void
MyDerivedClassI::opStringSAsync(Test::StringS p1,
                                Test::StringS p2,
                                function<void(const Test::StringS&, const Test::StringS&)> response,
                                function<void(exception_ptr)>,
                                const Ice::Current&)
{
    Test::StringS p3 = p1;
    std::copy(p2.begin(), p2.end(), std::back_inserter(p3));
    Test::StringS r;
    r.resize(p1.size());
    std::reverse_copy(p1.begin(), p1.end(), r.begin());
    response(r, p3);
}

void
MyDerivedClassI::opByteSSAsync(Test::ByteSS p1,
                               Test::ByteSS p2,
                               function<void(const Test::ByteSS&, const Test::ByteSS&)> response,
                               function<void(exception_ptr)>,
                               const Ice::Current&)
{
    Test::ByteSS p3;
    p3.resize(p1.size());
    std::reverse_copy(p1.begin(), p1.end(), p3.begin());
    Test::ByteSS r = p1;
    std::copy(p2.begin(), p2.end(), std::back_inserter(r));
    response(r, p3);
}

void
MyDerivedClassI::opBoolSSAsync(Test::BoolSS p1,
                               Test::BoolSS p2,
                               function<void(const Test::BoolSS&, const Test::BoolSS&)> response,
                               function<void(exception_ptr)>,
                               const Ice::Current&)
{
    auto p3 = p1;
    std::copy(p2.begin(), p2.end(), std::back_inserter(p3));
    Test::BoolSS r;
    r.resize(p1.size());
    std::reverse_copy(p1.begin(), p1.end(), r.begin());
    response(r, p3);
}

void
MyDerivedClassI::opShortIntLongSSAsync(Test::ShortSS p1,
                                       Test::IntSS p2,
                                       Test::LongSS p3,
                                       function<void(const Test::LongSS&,
                                                      const Test::ShortSS&,
                                                      const Test::IntSS&,
                                                      const Test::LongSS&)> response,
                                       function<void(exception_ptr)>,
                                       const Ice::Current&)
{
    auto p4 = p1;
    Test::IntSS p5;
    p5.resize(p2.size());
    std::reverse_copy(p2.begin(), p2.end(), p5.begin());
    auto p6 = p3;
    std::copy(p3.begin(), p3.end(), std::back_inserter(p6));
    response(p3, p4, p5, p6);
}

void
MyDerivedClassI::opFloatDoubleSSAsync(Test::FloatSS p1,
                                      Test::DoubleSS p2,
                                      function<void(const Test::DoubleSS&,
                                                     const Test::FloatSS&,
                                                     const Test::DoubleSS&)> response,
                                      function<void(exception_ptr)>,
                                      const Ice::Current&)
{
    Test::FloatSS p3 = p1;
    Test::DoubleSS p4;
    p4.resize(p2.size());
    std::reverse_copy(p2.begin(), p2.end(), p4.begin());
    Test::DoubleSS r = p2;
    std::copy(p2.begin(), p2.end(), std::back_inserter(r));
    response(r, p3, p4);
}

void
MyDerivedClassI::opStringSSAsync(Test::StringSS p1,
                                 Test::StringSS p2,
                                 function<void(const Test::StringSS&, const Test::StringSS&)> response,
                                 function<void(exception_ptr)>,
                                 const Ice::Current&)
{
    Test::StringSS p3 = p1;
    std::copy(p2.begin(), p2.end(), std::back_inserter(p3));
    Test::StringSS r;
    r.resize(p2.size());
    std::reverse_copy(p2.begin(), p2.end(), r.begin());
    response(r, p3);
}

void
MyDerivedClassI::opStringSSSAsync(Test::StringSSS p1, Test::StringSSS p2,
                                  function<void(const Test::StringSSS&, const Test::StringSSS&)> response,
                                  function<void(exception_ptr)>,
                                  const Ice::Current&)
{
    Test::StringSSS p3 = p1;
    std::copy(p2.begin(), p2.end(), std::back_inserter(p3));
    Test::StringSSS r;
    r.resize(p2.size());
    std::reverse_copy(p2.begin(), p2.end(), r.begin());
    response(r, p3);
}

void
MyDerivedClassI::opByteBoolDAsync(Test::ByteBoolD p1, Test::ByteBoolD p2,
                                  function<void(const Test::ByteBoolD&, const Test::ByteBoolD&)> response,
                                  function<void(exception_ptr)>,
                                  const Ice::Current&)
{
    Test::ByteBoolD p3 = p1;
    Test::ByteBoolD r = p1;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opShortIntDAsync(Test::ShortIntD p1, Test::ShortIntD p2,
                                  function<void(const Test::ShortIntD&, const Test::ShortIntD&)> response,
                                  function<void(exception_ptr)>,
                                  const Ice::Current&)
{
    Test::ShortIntD p3 = p1;
    Test::ShortIntD r = p1;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opLongFloatDAsync(Test::LongFloatD p1, Test::LongFloatD p2,
                                   function<void(const Test::LongFloatD&, const Test::LongFloatD&)> response,
                                   function<void(exception_ptr)>,
                                   const Ice::Current&)
{
    Test::LongFloatD p3 = p1;
    Test::LongFloatD r = p1;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opStringStringDAsync(Test::StringStringD p1, Test::StringStringD p2,
                                      function<void(const Test::StringStringD&, const Test::StringStringD&)> response,
                                      function<void(exception_ptr)>,
                                      const Ice::Current&)
{
    Test::StringStringD p3 = p1;
    Test::StringStringD r = p1;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opStringMyEnumDAsync(Test::StringMyEnumD p1, Test::StringMyEnumD p2,
                                      function<void(const Test::StringMyEnumD&, const Test::StringMyEnumD&)> response,
                                      function<void(exception_ptr)>,
                                      const Ice::Current&)
{
    Test::StringMyEnumD p3 = p1;
    Test::StringMyEnumD r = p1;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opMyEnumStringDAsync(Test::MyEnumStringD p1, Test::MyEnumStringD p2,
                                      function<void(const Test::MyEnumStringD&, const Test::MyEnumStringD&)> response,
                                      function<void(exception_ptr)>,
                                      const Ice::Current&)
{
    Test::MyEnumStringD p3 = p1;
    Test::MyEnumStringD r = p1;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opMyStructMyEnumDAsync(Test::MyStructMyEnumD p1, Test::MyStructMyEnumD p2,
                                        function<void(const Test::MyStructMyEnumD&,
                                                       const Test::MyStructMyEnumD&)> response,
                                        function<void(exception_ptr)>,
                                        const Ice::Current&)
{
    Test::MyStructMyEnumD p3 = p1;
    Test::MyStructMyEnumD r = p1;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opByteBoolDSAsync(Test::ByteBoolDS p1,
                                   Test::ByteBoolDS p2,
                                   function<void(const Test::ByteBoolDS&, const Test::ByteBoolDS&)> response,
                                   function<void(exception_ptr)>,
                                   const Ice::Current&)
{
    Test::ByteBoolDS p3 = p2;
    std::copy(p1.begin(), p1.end(), std::back_inserter(p3));
    Test::ByteBoolDS r;
    r.resize(p1.size());
    std::reverse_copy(p1.begin(), p1.end(), r.begin());
    response(r, p3);
}

void
MyDerivedClassI::opShortIntDSAsync(Test::ShortIntDS p1,
                                   Test::ShortIntDS p2,
                                   function<void(const Test::ShortIntDS&, const Test::ShortIntDS&)> response,
                                   function<void(exception_ptr)>,
                                   const Ice::Current&)
{
    Test::ShortIntDS p3 = p2;
    std::copy(p1.begin(), p1.end(), std::back_inserter(p3));
    Test::ShortIntDS r;
    r.resize(p1.size());
    std::reverse_copy(p1.begin(), p1.end(), r.begin());
    response(r, p3);
}

void
MyDerivedClassI::opLongFloatDSAsync(Test::LongFloatDS p1,
                                    Test::LongFloatDS p2,
                                    function<void(const Test::LongFloatDS&, const Test::LongFloatDS&)> response,
                                    function<void(exception_ptr)>,
                                    const Ice::Current&)
{
    Test::LongFloatDS p3 = p2;
    std::copy(p1.begin(), p1.end(), std::back_inserter(p3));
    Test::LongFloatDS r;
    r.resize(p1.size());
    std::reverse_copy(p1.begin(), p1.end(), r.begin());
    response(r, p3);
}

void
MyDerivedClassI::opStringStringDSAsync(Test::StringStringDS p1,
                                       Test::StringStringDS p2,
                                       function<void(const Test::StringStringDS&, const Test::StringStringDS&)> response,
                                       function<void(exception_ptr)>,
                                       const Ice::Current&)
{
    Test::StringStringDS p3 = p2;
    std::copy(p1.begin(), p1.end(), std::back_inserter(p3));
    Test::StringStringDS r;
    r.resize(p1.size());
    std::reverse_copy(p1.begin(), p1.end(), r.begin());
    response(r, p3);
}

void
MyDerivedClassI::opStringMyEnumDSAsync(Test::StringMyEnumDS p1,
                                       Test::StringMyEnumDS p2,
                                       function<void(const Test::StringMyEnumDS&, const Test::StringMyEnumDS&)> response,
                                       function<void(exception_ptr)>,
                                       const Ice::Current&)
{
    Test::StringMyEnumDS p3 = p2;
    std::copy(p1.begin(), p1.end(), std::back_inserter(p3));
    Test::StringMyEnumDS r;
    r.resize(p1.size());
    std::reverse_copy(p1.begin(), p1.end(), r.begin());
    response(r, p3);
}

void
MyDerivedClassI::opMyEnumStringDSAsync(Test::MyEnumStringDS p1,
                                       Test::MyEnumStringDS p2,
                                       function<void(const Test::MyEnumStringDS&, const Test::MyEnumStringDS&)> response,
                                       function<void(exception_ptr)>,
                                       const Ice::Current&)
{
    Test::MyEnumStringDS p3 = p2;
    std::copy(p1.begin(), p1.end(), std::back_inserter(p3));
    Test::MyEnumStringDS r;
    r.resize(p1.size());
    std::reverse_copy(p1.begin(), p1.end(), r.begin());
    response(r, p3);
}

void
MyDerivedClassI::opMyStructMyEnumDSAsync(Test::MyStructMyEnumDS p1,
                                         Test::MyStructMyEnumDS p2,
                                         function<void(const Test::MyStructMyEnumDS&,
                                                        const Test::MyStructMyEnumDS&)> response,
                                         function<void(exception_ptr)>,
                                         const Ice::Current&)
{
    Test::MyStructMyEnumDS p3 = p2;
    std::copy(p1.begin(), p1.end(), std::back_inserter(p3));
    Test::MyStructMyEnumDS r;
    r.resize(p1.size());
    std::reverse_copy(p1.begin(), p1.end(), r.begin());
    response(r, p3);
}

void
MyDerivedClassI::opByteByteSDAsync(Test::ByteByteSD p1,
                                   Test::ByteByteSD p2,
                                   function<void(const Test::ByteByteSD&, const Test::ByteByteSD&)> response,
                                   function<void(exception_ptr)>,
                                   const Ice::Current&)
{
    Test::ByteByteSD p3 = p2;
    Test::ByteByteSD r;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opBoolBoolSDAsync(Test::BoolBoolSD p1,
                                   Test::BoolBoolSD p2,
                                   function<void(const Test::BoolBoolSD&, const Test::BoolBoolSD&)> response,
                                   function<void(exception_ptr)>,
                                   const Ice::Current&)
{
    Test::BoolBoolSD p3 = p2;
    Test::BoolBoolSD r;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opShortShortSDAsync(Test::ShortShortSD p1,
                                     Test::ShortShortSD p2,
                                     function<void(const Test::ShortShortSD&, const Test::ShortShortSD&)> response,
                                     function<void(exception_ptr)>,
                                     const Ice::Current&)
{
    Test::ShortShortSD p3 = p2;
    Test::ShortShortSD r;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opIntIntSDAsync(Test::IntIntSD p1,
                                 Test::IntIntSD p2,
                                 function<void(const Test::IntIntSD&, const Test::IntIntSD&)> response,
                                 function<void(exception_ptr)>,
                                 const Ice::Current&)
{
    Test::IntIntSD p3 = p2;
    Test::IntIntSD r;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opLongLongSDAsync(Test::LongLongSD p1,
                                   Test::LongLongSD p2,
                                   function<void(const Test::LongLongSD&, const Test::LongLongSD&)> response,
                                   function<void(exception_ptr)>,
                                   const Ice::Current&)
{
    Test::LongLongSD p3 = p2;
    Test::LongLongSD r;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opStringFloatSDAsync(Test::StringFloatSD p1,
                                      Test::StringFloatSD p2,
                                      function<void(const Test::StringFloatSD&, const Test::StringFloatSD&)> response,
                                      function<void(exception_ptr)>,
                                      const Ice::Current&)
{
    Test::StringFloatSD p3 = p2;
    Test::StringFloatSD r;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opStringDoubleSDAsync(Test::StringDoubleSD p1,
                                       Test::StringDoubleSD p2,
                                       function<void(const Test::StringDoubleSD&,
                                                      const Test::StringDoubleSD&)> response,
                                       function<void(exception_ptr)>,
                                       const Ice::Current&)
{
    Test::StringDoubleSD p3 = p2;
    Test::StringDoubleSD r;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opStringStringSDAsync(Test::StringStringSD p1,
                                       Test::StringStringSD p2,
                                       function<void(const Test::StringStringSD&,
                                                      const Test::StringStringSD&)> response,
                                       function<void(exception_ptr)>,
                                       const Ice::Current&)
{
    Test::StringStringSD p3 = p2;
    Test::StringStringSD r;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opMyEnumMyEnumSDAsync(Test::MyEnumMyEnumSD p1,
                                       Test::MyEnumMyEnumSD p2,
                                       function<void(const Test::MyEnumMyEnumSD&,
                                                      const Test::MyEnumMyEnumSD&)> response,
                                       function<void(exception_ptr)>,
                                       const Ice::Current&)
{
    Test::MyEnumMyEnumSD p3 = p2;
    Test::MyEnumMyEnumSD r;
    std::set_union(p1.begin(), p1.end(), p2.begin(), p2.end(), std::inserter(r, r.end()));
    response(r, p3);
}

void
MyDerivedClassI::opIntSAsync(Test::IntS s,
                             function<void(const Test::IntS&)> response,
                             function<void(exception_ptr)>,
                             const Ice::Current&)
{
    Test::IntS r;
    std::transform(s.begin(), s.end(), std::back_inserter(r), std::negate<int>());
    response(r);
}

void
MyDerivedClassI::opByteSOnewayAsync(Test::ByteS,
                                    function<void()> response,
                                    function<void(exception_ptr)>,
                                    const Ice::Current&)
{
    IceUtil::Mutex::Lock sync(_mutex);
    ++_opByteSOnewayCallCount;
    response();
}

void
MyDerivedClassI::opByteSOnewayCallCountAsync(function<void(int)> response,
                                             function<void(exception_ptr)>,
                                             const Ice::Current&)
{
    IceUtil::Mutex::Lock sync(_mutex);
    response(_opByteSOnewayCallCount);
    _opByteSOnewayCallCount = 0;
}

void
MyDerivedClassI::opContextAsync(function<void(const Ice::Context&)> response,
                                function<void(exception_ptr)>,
                                const Ice::Current& current)
{
    Test::StringStringD r = current.ctx;
    response(r);
}

void
MyDerivedClassI::opDoubleMarshalingAsync(Ice::Double p1,
                                         Test::DoubleS p2,
                                         function<void()> response,
                                         function<void(exception_ptr)>,
                                         const Ice::Current&)
{
    Ice::Double d = 1278312346.0 / 13.0;
    test(p1 == d);
    for(unsigned int i = 0; i < p2.size(); ++i)
    {
        test(p2[i] == d);
    }
    response();
}

void
MyDerivedClassI::opIdempotentAsync(function<void()> response,
                                   function<void(exception_ptr)>,
                                   const Ice::Current& current)
{
    test(current.mode == OperationMode::Idempotent);
    response();
}

void
MyDerivedClassI::opNonmutatingAsync(function<void()> response,
                                    function<void(exception_ptr)>,
                                    const Ice::Current& current)
{
    test(current.mode == OperationMode::Nonmutating);
    response();
}

void
MyDerivedClassI::opDerivedAsync(function<void()> response,
                                function<void(exception_ptr)>,
                                const Ice::Current&)
{
    response();
}

void
MyDerivedClassI::opByte1Async(Ice::Byte b,
                              function<void(Ice::Byte)> response,
                              function<void(exception_ptr)>,
                              const Ice::Current&)
{
    response(b);
}

void
MyDerivedClassI::opShort1Async(Ice::Short s,
                               function<void(Ice::Short)> response,
                               function<void(exception_ptr)>,
                               const Ice::Current&)
{
    response(s);
}

void
MyDerivedClassI::opInt1Async(Ice::Int i,
                             function<void(Ice::Int)> response,
                             function<void(exception_ptr)>,
                             const Ice::Current&)
{
    response(i);
}

void
MyDerivedClassI::opLong1Async(Ice::Long l,
                              function<void(Ice::Long)> response,
                              function<void(exception_ptr)>,
                              const Ice::Current&)
{
    response(l);
}

void
MyDerivedClassI::opFloat1Async(Ice::Float f,
                               function<void(Ice::Float)> response,
                               function<void(exception_ptr)>,
                               const Ice::Current&)
{
    response(f);
}

void
MyDerivedClassI::opDouble1Async(Ice::Double d,
                                function<void(Ice::Double)> response,
                                function<void(exception_ptr)>,
                                const Ice::Current&)
{
    response(d);
}

void
MyDerivedClassI::opString1Async(string s,
                                function<void(const string&)> response,
                                function<void(exception_ptr)>,
                                const Ice::Current&)
{
    response(s);
}

void
MyDerivedClassI::opStringS1Async(Test::StringS seq,
                                 function<void(const Test::StringS&)> response,
                                 function<void(exception_ptr)>,
                                 const Ice::Current&)
{
    response(seq);
}

void
MyDerivedClassI::opByteBoolD1Async(Test::ByteBoolD dict,
                                   function<void(const Test::ByteBoolD&)> response,
                                   function<void(exception_ptr)>,
                                   const Ice::Current&)
{
    response(dict);
}

void
MyDerivedClassI::opStringS2Async(Test::StringS seq,
                                 function<void(const Test::StringS&)> response,
                                 function<void(exception_ptr)>,
                                 const Ice::Current&)
{
    response(seq);
}

void
MyDerivedClassI::opByteBoolD2Async(Test::ByteBoolD dict,
                                   function<void(const Test::ByteBoolD&)> response,
                                   function<void(exception_ptr)>,
                                   const Ice::Current&)
{
    response(dict);
}

void
MyDerivedClassI::opMyStruct1Async(Test::MyStruct1 s,
                                  function<void(const Test::MyStruct1&)> response,
                                  function<void(exception_ptr)>,
                                  const Ice::Current&)
{
    response(s);
}

void
MyDerivedClassI::opMyClass1Async(shared_ptr<Test::MyClass1> c,
                                 function<void(const shared_ptr<Test::MyClass1>&)> response,
                                 function<void(exception_ptr)>,
                                 const Ice::Current&)
{
    response(c);
}

void
MyDerivedClassI::opStringLiteralsAsync(function<void(const Test::StringS&)> response,
                                       function<void(exception_ptr)>,
                                       const Ice::Current&)
{
    Test::StringS data;
    data.push_back(Test::s0);
    data.push_back(Test::s1);
    data.push_back(Test::s2);
    data.push_back(Test::s3);
    data.push_back(Test::s4);
    data.push_back(Test::s5);
    data.push_back(Test::s6);
    data.push_back(Test::s7);
    data.push_back(Test::s8);
    data.push_back(Test::s9);
    data.push_back(Test::s10);

    data.push_back(Test::sw0);
    data.push_back(Test::sw1);
    data.push_back(Test::sw2);
    data.push_back(Test::sw3);
    data.push_back(Test::sw4);
    data.push_back(Test::sw5);
    data.push_back(Test::sw6);
    data.push_back(Test::sw7);
    data.push_back(Test::sw8);
    data.push_back(Test::sw9);
    data.push_back(Test::sw10);

    data.push_back(Test::ss0);
    data.push_back(Test::ss1);
    data.push_back(Test::ss2);
    data.push_back(Test::ss3);
    data.push_back(Test::ss4);
    data.push_back(Test::ss5);

    data.push_back(Test::su0);
    data.push_back(Test::su1);
    data.push_back(Test::su2);

    response(data);
}

void
MyDerivedClassI::opWStringLiteralsAsync(function<void(const Test::WStringS&)> response,
                                        function<void(exception_ptr)>,
                                        const Ice::Current&)
{
    Test::WStringS data;
    data.push_back(Test::ws0);
    data.push_back(Test::ws1);
    data.push_back(Test::ws2);
    data.push_back(Test::ws3);
    data.push_back(Test::ws4);
    data.push_back(Test::ws5);
    data.push_back(Test::ws6);
    data.push_back(Test::ws7);
    data.push_back(Test::ws8);
    data.push_back(Test::ws9);
    data.push_back(Test::ws10);

    data.push_back(Test::wsw0);
    data.push_back(Test::wsw1);
    data.push_back(Test::wsw2);
    data.push_back(Test::wsw3);
    data.push_back(Test::wsw4);
    data.push_back(Test::wsw5);
    data.push_back(Test::wsw6);
    data.push_back(Test::wsw7);
    data.push_back(Test::wsw8);
    data.push_back(Test::wsw9);
    data.push_back(Test::wsw10);

    data.push_back(Test::wss0);
    data.push_back(Test::wss1);
    data.push_back(Test::wss2);
    data.push_back(Test::wss3);
    data.push_back(Test::wss4);
    data.push_back(Test::wss5);

    data.push_back(Test::wsu0);
    data.push_back(Test::wsu1);
    data.push_back(Test::wsu2);

    response(data);
}

void
MyDerivedClassI::opMStruct1Async(function<void(const OpMStruct1MarshaledResult&)> response,
                                 function<void(std::exception_ptr)>,
                                 const Ice::Current& current)
{
    Test::Structure s;
    s.e = MyEnum::enum1; // enum must be initialized
    response(OpMStruct1MarshaledResult(s, current));
}

void
MyDerivedClassI::opMStruct2Async(Test::Structure p1,
                                 function<void(const OpMStruct2MarshaledResult&)> response,
                                 function<void(std::exception_ptr)>,
                                 const Ice::Current& current)
{
    response(OpMStruct2MarshaledResult(p1, p1, current));
}

void
MyDerivedClassI::opMSeq1Async(function<void(const OpMSeq1MarshaledResult&)> response,
                              function<void(std::exception_ptr)>,
                              const Ice::Current& current)
{
    response(OpMSeq1MarshaledResult(Test::StringS(), current));
}

void
MyDerivedClassI::opMSeq2Async(Test::StringS p1,
                              function<void(const OpMSeq2MarshaledResult&)> response,
                              function<void(std::exception_ptr)>,
                              const Ice::Current& current)
{
    response(OpMSeq2MarshaledResult(p1, p1, current));
}

void
MyDerivedClassI::opMDict1Async(function<void(const OpMDict1MarshaledResult&)> response,
                               function<void(std::exception_ptr)>,
                               const Ice::Current& current)
{
    response(OpMDict1MarshaledResult(Test::StringStringD(), current));
}

void
MyDerivedClassI::opMDict2Async(Test::StringStringD p1,
                               function<void(const OpMDict2MarshaledResult&)> response,
                               function<void(std::exception_ptr)>,
                               const Ice::Current& current)
{
    response(OpMDict2MarshaledResult(p1, p1, current));
}
