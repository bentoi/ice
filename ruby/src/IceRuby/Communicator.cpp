// **********************************************************************
//
// Copyright (c) 2003-2018 ZeroC, Inc. All rights reserved.
//
// This copy of Ice is licensed to you under the terms described in the
// ICE_LICENSE file included in this distribution.
//
// **********************************************************************

#include <Communicator.h>
#include <ImplicitContext.h>
#include <Logger.h>
#include <Properties.h>
#include <Proxy.h>
#include <Types.h>
#include <Util.h>
#include <ValueFactoryManager.h>
#include <IceUtil/DisableWarnings.h>
#include <Ice/Communicator.h>
#include <Ice/Initialize.h>
#include <Ice/Locator.h>
#include <Ice/ObjectFactory.h>
#include <Ice/Properties.h>
#include <Ice/Router.h>

using namespace std;
using namespace IceRuby;

static VALUE _communicatorClass;

typedef map<Ice::CommunicatorPtr, VALUE> CommunicatorMap;
static CommunicatorMap _communicatorMap;

extern "C"
void
IceRuby_Communicator_mark(Ice::CommunicatorPtr* p)
{
    assert(p);
    try
    {
        ValueFactoryManagerPtr vfm = ValueFactoryManagerPtr::dynamicCast((*p)->getValueFactoryManager());
        assert(vfm);
        vfm->markSelf();
    }
    catch(const Ice::CommunicatorDestroyedException&)
    {
        // Ignore. This is expected.
    }
}

extern "C"
void
IceRuby_Communicator_free(Ice::CommunicatorPtr* p)
{
    assert(p);
    delete p;
}

namespace
{

class CommunicatorDestroyer
{
public:

    CommunicatorDestroyer(const Ice::CommunicatorPtr& c) : _communicator(c) {}

    ~CommunicatorDestroyer()
    {
        _communicator->destroy();
    }

private:

    Ice::CommunicatorPtr _communicator;
};

}

extern "C"
VALUE
IceRuby_initialize(int argc, VALUE* argv, VALUE /*self*/)
{
    ICE_RUBY_TRY
    {
        //
        // The argument options are:
        //
        // Ice::initialize()
        // Ice::initialize(args)
        // Ice::initialize(initData)
        // Ice::initialize(configFile)
        // Ice::initialize(args, initData)
        // Ice::initialize(args, configFile)
        //
        // An implicit block is optional.
        //

        if(argc > 2)
        {
            throw RubyException(rb_eArgError, "invalid number of arguments to Ice::initialize");
        }

        volatile VALUE initDataCls = callRuby(rb_path2class, "Ice::InitializationData");
        volatile VALUE args = Qnil, initData = Qnil, configFile = Qnil;

        if(argc >= 1)
        {
            if(isArray(argv[0]))
            {
                args = argv[0];
            }
            else if(callRuby(rb_obj_is_instance_of, argv[0], initDataCls) == Qtrue)
            {
                initData = argv[0];
            }
            else if(TYPE(argv[0]) == T_STRING)
            {
                configFile = argv[0];
            }
            else
            {
                throw RubyException(rb_eTypeError,
                    "initialize expects an argument list, InitializationData or a configuration filename");
            }
        }

        if(argc >= 2)
        {
            if(isArray(argv[1]))
            {
                if(!NIL_P(args))
                {
                    throw RubyException(rb_eTypeError, "unexpected array argument to initialize");
                }
                args = argv[1];
            }
            else if(callRuby(rb_obj_is_instance_of, argv[1], initDataCls) == Qtrue)
            {
                if(!NIL_P(initData))
                {
                    throw RubyException(rb_eTypeError, "unexpected InitializationData argument to initialize");
                }
                initData = argv[1];
            }
            else if(TYPE(argv[1]) == T_STRING)
            {
                if(!NIL_P(configFile))
                {
                    throw RubyException(rb_eTypeError, "unexpected string argument to initialize");
                }
                configFile = argv[1];
            }
            else
            {
                throw RubyException(rb_eTypeError,
                    "initialize expects an argument list, InitializationData or a configuration filename");
            }
        }

        if(!NIL_P(initData) && !NIL_P(configFile))
        {
            throw RubyException(rb_eTypeError,
                "initialize accepts either Ice.InitializationData or a configuration filename");
        }

        Ice::StringSeq seq;
        if(!NIL_P(args) && !arrayToStringSeq(args, seq))
        {
            throw RubyException(rb_eTypeError, "invalid array argument to Ice::initialize");
        }

        Ice::InitializationData data;
        if(!NIL_P(initData))
        {
            volatile VALUE properties = callRuby(rb_iv_get, initData, "@properties");
            volatile VALUE logger = callRuby(rb_iv_get, initData, "@logger");

            if(!NIL_P(properties))
            {
                data.properties = getProperties(properties);
            }

            if(!NIL_P(logger))
            {
                throw RubyException(rb_eArgError, "custom logger is not supported");
            }
        }

        //
        // Insert the program name (stored in the Ruby global variable $0) as the first
        // element of the sequence.
        //
        volatile VALUE progName = callRuby(rb_gv_get, "$0");
        seq.insert(seq.begin(), getString(progName));

        data.compactIdResolver = new IdResolver;
        data.valueFactoryManager = new ValueFactoryManager;

        if(!data.properties)
        {
            data.properties = Ice::createProperties();
        }

        if(!NIL_P(configFile))
        {
            data.properties->load(getString(configFile));
        }

        if(!NIL_P(args))
        {
            data.properties = Ice::createProperties(seq, data.properties);
        }

        //
        // Remaining command line options are passed to the communicator
        // as an argument vector in case they contain plugin properties.
        //
        int ac = static_cast<int>(seq.size());
        char** av = new char*[ac + 1];
        int i = 0;
        for(Ice::StringSeq::const_iterator s = seq.begin(); s != seq.end(); ++s, ++i)
        {
            av[i] = strdup(s->c_str());
        }
        av[ac] = 0;

        Ice::CommunicatorPtr communicator;
        try
        {
            if(!NIL_P(args))
            {
                communicator = Ice::initialize(ac, av, data);
            }
            else
            {
                communicator = Ice::initialize(data);
            }
        }
        catch(const Ice::LocalException& ex)
        {
            cerr << ex << endl;
            for(i = 0; i < ac + 1; ++i)
            {
                free(av[i]);
            }
            delete[] av;

            throw;
        }
        catch(...)
        {
            for(i = 0; i < ac + 1; ++i)
            {
                free(av[i]);
            }
            delete[] av;

            throw;
        }

        //
        // Replace the contents of the given argument list with the filtered arguments.
        //
        if(!NIL_P(args))
        {
            callRuby(rb_ary_clear, args);

            //
            // We start at index 1 in order to skip the element that we inserted earlier.
            //
            for(i = 1; i < ac; ++i)
            {
                volatile VALUE str = createString(av[i]);
                callRuby(rb_ary_push, args, str);
            }
        }

        for(i = 0; i < ac + 1; ++i)
        {
            free(av[i]);
        }
        delete[] av;

        VALUE result = Data_Wrap_Struct(_communicatorClass, IceRuby_Communicator_mark,
                                        IceRuby_Communicator_free, new Ice::CommunicatorPtr(communicator));

        CommunicatorMap::iterator p = _communicatorMap.find(communicator);
        if(p != _communicatorMap.end())
        {
            _communicatorMap.erase(p);
        }
        _communicatorMap.insert(CommunicatorMap::value_type(communicator, reinterpret_cast<const VALUE&>(result)));

        //
        // If an implicit block was provided, yield to the block and pass it the communicator instance.
        // We destroy the communicator after the block is finished, and return the result of the block.
        //
        if(rb_block_given_p())
        {
            CommunicatorDestroyer destroyer(communicator);
            //
            // Examine the arity of the block procedure. If it accepts one argument, pass it the
            // communicator. If it accepts two arguments, pass it the communicator and the
            // argument vector.
            //
            VALUE proc = callRuby(rb_block_proc);
            int arity = rb_proc_arity(proc);
            if(arity == 1)
            {
                return callRuby(rb_yield, result);
            }
            else if(arity == 2)
            {
                VALUE blockArgs = createArray(2);
                RARRAY_ASET(blockArgs, 0, result);
                RARRAY_ASET(blockArgs, 1, args);
                return callRuby(rb_yield, blockArgs);
            }
            else
            {
                throw RubyException(rb_eArgError, "block must accept one or two arguments");
            }
        }
        else
        {
            return result;
        }
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_stringToIdentity(VALUE /*self*/, VALUE str)
{
    ICE_RUBY_TRY
    {
        string s = getString(str);
        Ice::Identity ident = Ice::stringToIdentity(s);
        return createIdentity(ident);
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_identityToString(int argc, VALUE* argv, VALUE /*self*/)
{
    ICE_RUBY_TRY
    {
        if(argc < 1 || argc > 2)
        {
            throw RubyException(rb_eArgError, "wrong number of arguments");
        }

        Ice::Identity ident = getIdentity(argv[0]);

        Ice::ToStringMode toStringMode = Ice::Unicode;
        if(argc == 2)
        {
            volatile VALUE modeValue = callRuby(rb_funcall, argv[1], rb_intern("to_i"), 0);
            assert(TYPE(modeValue) == T_FIXNUM);
            toStringMode = static_cast<Ice::ToStringMode>(FIX2LONG(modeValue));
        }

        string str = Ice::identityToString(ident, toStringMode);
        return createString(str);
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_destroy(VALUE self)
{
    Ice::CommunicatorPtr p = getCommunicator(self);

    ValueFactoryManagerPtr vfm = ValueFactoryManagerPtr::dynamicCast(p->getValueFactoryManager());
    assert(vfm);

    ICE_RUBY_TRY
    {
        p->destroy();
    }
    ICE_RUBY_CATCH

    vfm->destroy();

    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_shutdown(VALUE self)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        p->shutdown();
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_isShutdown(VALUE self)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        return p->isShutdown() ? Qtrue : Qfalse;
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_waitForShutdown(VALUE self)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        p->waitForShutdown();
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_stringToProxy(VALUE self, VALUE str)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        string s = getString(str);
        Ice::ObjectPrx proxy = p->stringToProxy(s);
        if(proxy)
        {
            return createProxy(proxy);
        }
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_proxyToString(VALUE self, VALUE proxy)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        Ice::ObjectPrx prx;
        if(!NIL_P(proxy))
        {
            if(!checkProxy(proxy))
            {
                throw RubyException(rb_eTypeError, "argument must be a proxy");
            }
            prx = getProxy(proxy);
        }
        string str = p->proxyToString(prx);
        return createString(str);
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_propertyToProxy(VALUE self, VALUE str)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        string s = getString(str);
        Ice::ObjectPrx proxy = p->propertyToProxy(s);
        if(proxy)
        {
            return createProxy(proxy);
        }
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_proxyToProperty(VALUE self, VALUE obj, VALUE str)
{
    ICE_RUBY_TRY
    {
        if(!checkProxy(obj))
        {
            throw RubyException(rb_eTypeError, "argument must be a proxy");
        }
        Ice::CommunicatorPtr p = getCommunicator(self);
        Ice::ObjectPrx o = getProxy(obj);
        string s = getString(str);
        Ice::PropertyDict dict = p->proxyToProperty(o, s);
        volatile VALUE result = callRuby(rb_hash_new);
        for(Ice::PropertyDict::const_iterator q = dict.begin(); q != dict.end(); ++q)
        {
            volatile VALUE key = createString(q->first);
            volatile VALUE value = createString(q->second);
            callRuby(rb_hash_aset, result, key, value);
        }
        return result;
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_stringToIdentity(VALUE self, VALUE str)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        string s = getString(str);
        Ice::Identity ident = p->stringToIdentity(s);
        return createIdentity(ident);
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_identityToString(VALUE self, VALUE id)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        Ice::Identity ident = getIdentity(id);
        string str = p->identityToString(ident);
        return createString(str);
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_addObjectFactory(VALUE self, VALUE factory, VALUE id)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        ValueFactoryManagerPtr vfm = ValueFactoryManagerPtr::dynamicCast(p->getValueFactoryManager());
        assert(vfm);
        string idstr = getString(id);
        vfm->addObjectFactory(factory, idstr);
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_findObjectFactory(VALUE self, VALUE id)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        ValueFactoryManagerPtr vfm = ValueFactoryManagerPtr::dynamicCast(p->getValueFactoryManager());
        assert(vfm);
        string idstr = getString(id);
        return vfm->findObjectFactory(idstr);
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_getValueFactoryManager(VALUE self)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        ValueFactoryManagerPtr vfm = ValueFactoryManagerPtr::dynamicCast(p->getValueFactoryManager());
        assert(vfm);
        return vfm->getObject();
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_getImplicitContext(VALUE self)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        Ice::ImplicitContextPtr implicitContext = p->getImplicitContext();
        return createImplicitContext(implicitContext);
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_getProperties(VALUE self)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        Ice::PropertiesPtr props = p->getProperties();
        return createProperties(props);
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_getLogger(VALUE self)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        Ice::LoggerPtr logger = p->getLogger();
        return createLogger(logger);
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_getDefaultRouter(VALUE self)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        Ice::RouterPrx router = p->getDefaultRouter();
        if(router)
        {
            volatile VALUE cls = callRuby(rb_path2class, "Ice::RouterPrx");
            assert(!NIL_P(cls));
            return createProxy(router, cls);
        }
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_setDefaultRouter(VALUE self, VALUE router)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        Ice::RouterPrx proxy;
        if(!NIL_P(router))
        {
            if(!checkProxy(router))
            {
                throw RubyException(rb_eTypeError, "argument must be a proxy");
            }
            proxy = Ice::RouterPrx::uncheckedCast(getProxy(router));
        }
        p->setDefaultRouter(proxy);
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_getDefaultLocator(VALUE self)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        Ice::LocatorPrx locator = p->getDefaultLocator();
        if(locator)
        {
            volatile VALUE cls = callRuby(rb_path2class, "Ice::LocatorPrx");
            assert(!NIL_P(cls));
            return createProxy(locator, cls);
        }
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_setDefaultLocator(VALUE self, VALUE locator)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);
        Ice::LocatorPrx proxy;
        if(!NIL_P(locator))
        {
            if(!checkProxy(locator))
            {
                throw RubyException(rb_eTypeError, "argument must be a proxy");
            }
            proxy = Ice::LocatorPrx::uncheckedCast(getProxy(locator));
        }
        p->setDefaultLocator(proxy);
    }
    ICE_RUBY_CATCH
    return Qnil;
}

extern "C"
VALUE
IceRuby_Communicator_flushBatchRequests(VALUE self, VALUE compress)
{
    ICE_RUBY_TRY
    {
        Ice::CommunicatorPtr p = getCommunicator(self);

        volatile VALUE type = callRuby(rb_path2class, "Ice::CompressBatch");
        if(callRuby(rb_obj_is_instance_of, compress, type) != Qtrue)
        {
            throw RubyException(rb_eTypeError,
                "value for 'compress' argument must be an enumerator of Ice::CompressBatch");
        }
        volatile VALUE compressValue = callRuby(rb_funcall, compress, rb_intern("to_i"), 0);
        assert(TYPE(compressValue) == T_FIXNUM);
        Ice::CompressBatch cb = static_cast<Ice::CompressBatch>(FIX2LONG(compressValue));
        p->flushBatchRequests(cb);
    }
    ICE_RUBY_CATCH
    return Qnil;
}

void
IceRuby::initCommunicator(VALUE iceModule)
{
    rb_define_module_function(iceModule, "initialize", CAST_METHOD(IceRuby_initialize), -1);
    rb_define_module_function(iceModule, "identityToString", CAST_METHOD(IceRuby_identityToString), -1);
    rb_define_module_function(iceModule, "stringToIdentity", CAST_METHOD(IceRuby_stringToIdentity), 1);

    _communicatorClass = rb_define_class_under(iceModule, "CommunicatorI", rb_cObject);
    rb_define_method(_communicatorClass, "destroy", CAST_METHOD(IceRuby_Communicator_destroy), 0);
    rb_define_method(_communicatorClass, "shutdown", CAST_METHOD(IceRuby_Communicator_shutdown), 0);
    rb_define_method(_communicatorClass, "isShutdown", CAST_METHOD(IceRuby_Communicator_isShutdown), 0);
    rb_define_method(_communicatorClass, "waitForShutdown", CAST_METHOD(IceRuby_Communicator_waitForShutdown), 0);
    rb_define_method(_communicatorClass, "stringToProxy", CAST_METHOD(IceRuby_Communicator_stringToProxy), 1);
    rb_define_method(_communicatorClass, "proxyToString", CAST_METHOD(IceRuby_Communicator_proxyToString), 1);
    rb_define_method(_communicatorClass, "propertyToProxy", CAST_METHOD(IceRuby_Communicator_propertyToProxy), 1);
    rb_define_method(_communicatorClass, "proxyToProperty", CAST_METHOD(IceRuby_Communicator_proxyToProperty), 2);
    rb_define_method(_communicatorClass, "stringToIdentity", CAST_METHOD(IceRuby_Communicator_stringToIdentity), 1);
    rb_define_method(_communicatorClass, "identityToString", CAST_METHOD(IceRuby_Communicator_identityToString), 1);
    rb_define_method(_communicatorClass, "addObjectFactory", CAST_METHOD(IceRuby_Communicator_addObjectFactory), 2);
    rb_define_method(_communicatorClass, "findObjectFactory", CAST_METHOD(IceRuby_Communicator_findObjectFactory), 1);
    rb_define_method(_communicatorClass, "getValueFactoryManager", CAST_METHOD(IceRuby_Communicator_getValueFactoryManager), 0);
    rb_define_method(_communicatorClass, "getImplicitContext", CAST_METHOD(IceRuby_Communicator_getImplicitContext), 0);
    rb_define_method(_communicatorClass, "getProperties", CAST_METHOD(IceRuby_Communicator_getProperties), 0);
    rb_define_method(_communicatorClass, "getLogger", CAST_METHOD(IceRuby_Communicator_getLogger), 0);
    rb_define_method(_communicatorClass, "getDefaultRouter", CAST_METHOD(IceRuby_Communicator_getDefaultRouter), 0);
    rb_define_method(_communicatorClass, "setDefaultRouter", CAST_METHOD(IceRuby_Communicator_setDefaultRouter), 1);
    rb_define_method(_communicatorClass, "getDefaultLocator", CAST_METHOD(IceRuby_Communicator_getDefaultLocator), 0);
    rb_define_method(_communicatorClass, "setDefaultLocator", CAST_METHOD(IceRuby_Communicator_setDefaultLocator), 1);
    rb_define_method(_communicatorClass, "flushBatchRequests", CAST_METHOD(IceRuby_Communicator_flushBatchRequests), 1);
}

Ice::CommunicatorPtr
IceRuby::getCommunicator(VALUE v)
{
    Ice::CommunicatorPtr* p = reinterpret_cast<Ice::CommunicatorPtr*>(DATA_PTR(v));
    assert(p);
    return *p;
}

VALUE
IceRuby::lookupCommunicator(const Ice::CommunicatorPtr& p)
{
    CommunicatorMap::iterator q = _communicatorMap.find(p.get());
    if(q != _communicatorMap.end())
    {
        return q->second;
    }
    return Qnil;
}
