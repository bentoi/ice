// **********************************************************************
//
// Copyright (c) 2003-2018 ZeroC, Inc. All rights reserved.
//
// This copy of Ice is licensed to you under the terms described in the
// ICE_LICENSE file included in this distribution.
//
// **********************************************************************

#import <objc/Ice.h>
#import <dispatcher/TestI.h>

#import <Foundation/NSThread.h>

@implementation TestDispatcherTestIntfI
-(void) op:(ICECurrent*)__unused current
{
}
-(void) sleep:(ICEInt)to current:(ICECurrent*)__unused current
{
    [NSThread sleepForTimeInterval:to / 1000.0];
}
-(void) opWithPayload:(ICEMutableByteSeq*)__unused data current:(ICECurrent*)__unused current
{
}
-(void) shutdown:(ICECurrent*)current
{
    [[current.adapter getCommunicator] shutdown];
}
@end

@implementation TestDispatcherTestIntfControllerI
-(id) initWithAdapter:(id<ICEObjectAdapter>)adapter
{
    self = [super init];
    if(!self)
    {
        return nil;
    }
    _adapter = adapter;
    return self;
}
-(void) holdAdapter:(ICECurrent*)__unused current
{
    [_adapter hold];
}
-(void) resumeAdapter:(ICECurrent*)__unused current
{
    [_adapter activate];
}
@end
