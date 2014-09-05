// **********************************************************************
//
// Copyright (c) 2003-2014 ZeroC, Inc. All rights reserved.
//
// This copy of Ice is licensed to you under the terms described in the
// ICE_LICENSE file included in this distribution.
//
// **********************************************************************

package IceInternal;

public interface Acceptor
{
    java.nio.channels.ServerSocketChannel fd();
    void close();
    EndpointI listen(EndpointI endp);
    Transceiver accept();
    String protocol();
    @Override
    String toString();
    String toDetailedString();
}
