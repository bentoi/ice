//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#include <Ice/Ice.h>
#include <TestHelper.h>
#include <TestI.h>

//
// For 'Ice::Communicator::addObjectFactory()' deprecation
//
#if defined(_MSC_VER)
#   pragma warning( disable : 4996 )
#elif defined(__GNUC__)
#   pragma GCC diagnostic ignored "-Wdeprecated-declarations"
#endif

using namespace std;
using namespace Test;

template<typename T>
function<shared_ptr<T>(string)> makeFactory()
{
    return [](string)
        {
            return make_shared<T>();
        };
}

class MyObjectFactory : public Ice::ObjectFactory
{
public:
    MyObjectFactory() : _destroyed(false)
    {
    }

    ~MyObjectFactory()
    {
        assert(_destroyed);
    }

    virtual Ice::ValuePtr create(const string&)
    {
        return nullptr;
    }

    virtual void destroy()
    {
        _destroyed = true;
    }
private:
    bool _destroyed;
};

class Collocated : public Test::TestHelper
{
public:

    void run(int, char**);
};

void
Collocated::run(int argc, char** argv)
{
    Ice::PropertiesPtr properties = createTestProperties(argc, argv);
    properties->setProperty("Ice.Warn.Dispatch", "0");
    Ice::CommunicatorHolder communicator = initialize(argc, argv, properties);

    communicator->getValueFactoryManager()->add(makeFactory<BI>(), "::Test::B");
    communicator->getValueFactoryManager()->add(makeFactory<CI>(), "::Test::C");
    communicator->getValueFactoryManager()->add(makeFactory<DI>(), "::Test::D");
    communicator->getValueFactoryManager()->add(makeFactory<EI>(), "::Test::E");
    communicator->getValueFactoryManager()->add(makeFactory<FI>(), "::Test::F");
    communicator->getValueFactoryManager()->add(makeFactory<II>(), "::Test::I");
    communicator->getValueFactoryManager()->add(makeFactory<JI>(), "::Test::J");
    communicator->addObjectFactory(make_shared<MyObjectFactory>(), "TestOF");

    communicator->getProperties()->setProperty("TestAdapter.Endpoints", getTestEndpoint());
    Ice::ObjectAdapterPtr adapter = communicator->createObjectAdapter("TestAdapter");
    adapter->add(std::make_shared<InitialI>(adapter), Ice::stringToIdentity("initial"));
    adapter->add(std::make_shared<TestIntfI>(), Ice::stringToIdentity("test"));
    adapter->add(std::make_shared<F2I>(), Ice::stringToIdentity("F21"));
    adapter->add(std::make_shared<UnexpectedObjectExceptionTestI>(), Ice::stringToIdentity("uoet"));
    InitialPrxPtr allTests(Test::TestHelper*);
    InitialPrxPtr initial = allTests(this);
    // We must call shutdown even in the collocated case for cyclic dependency cleanup
    initial->shutdown();
}

DEFINE_TEST(Collocated)
