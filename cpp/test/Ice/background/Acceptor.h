// **********************************************************************
//
// Copyright (c) 2003-2014 ZeroC, Inc. All rights reserved.
//
// This copy of Ice is licensed to you under the terms described in the
// ICE_LICENSE file included in this distribution.
//
// **********************************************************************

#ifndef TEST_ACCEPTOR_H
#define TEST_ACCEPTOR_H

#include <Ice/Acceptor.h>

class Acceptor : public IceInternal::Acceptor
{
public:

    virtual IceInternal::NativeInfoPtr getNativeInfo();

    virtual void close();
    virtual IceInternal::EndpointIPtr listen(const IceInternal::EndpointIPtr&);
#ifdef ICE_USE_IOCP
    virtual void startAccept();
    virtual void finishAccept();
#endif
    virtual IceInternal::TransceiverPtr accept();
    virtual std::string protocol() const;
    virtual std::string toString() const;
    virtual std::string toDetailedString() const;

    IceInternal::AcceptorPtr delegate() const { return _acceptor; }

private:

    Acceptor(const IceInternal::AcceptorPtr&);
    friend class EndpointI;

    const IceInternal::AcceptorPtr _acceptor;
};

#endif
