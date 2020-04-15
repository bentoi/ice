//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using Ice;
using System;
using System.Collections.Generic;
using System.Reflection;
using Test;

[assembly: AssemblyTitle("IceTest")]
[assembly: AssemblyDescription("Ice test")]
[assembly: AssemblyCompany("ZeroC, Inc.")]

public class Client : TestHelper
{
    public override void Run(string[] args)
    {
        Dictionary<string, string> properties = CreateTestProperties(ref args);
        //
        // We must disable connection warnings, because we attempt to
        // ping the router before session establishment, as well as
        // after session destruction. Both will cause a
        // ConnectionLostException.
        //
        properties["Ice.Warn.Connections"] = "0";
        using (var communicator = Initialize(properties))
        {
            IObjectPrx routerBase;
            {
                Console.Out.Write("testing stringToProxy for router... ");
                Console.Out.Flush();
                routerBase = IObjectPrx.Parse($"Glacier2/router:{GetTestEndpoint(50)}", communicator);
                Console.Out.WriteLine("ok");
            }

            Glacier2.IRouterPrx? router;
            {
                Console.Out.Write("testing checked cast for router... ");
                Console.Out.Flush();
                router = Glacier2.IRouterPrx.CheckedCast(routerBase);
                Assert(router != null);
                Console.Out.WriteLine("ok");
            }

            {
                Console.Out.Write("testing router finder... ");
                Console.Out.Flush();
                IRouterFinderPrx finder = IRouterFinderPrx.Parse($"Ice/RouterFinder:{GetTestEndpoint(50)}", communicator);
                Assert(finder.GetRouter()!.Identity.Equals(router.Identity));
                Console.Out.WriteLine("ok");
            }

            {
                Console.Out.Write("installing router with communicator... ");
                Console.Out.Flush();
                communicator.SetDefaultRouter(router);
                Console.Out.WriteLine("ok");
            }

            {
                Console.Out.Write("getting the session timeout... ");
                Console.Out.Flush();
                long sessionTimeout = router.GetSessionTimeout();
                long acmTimeout = router.GetACMTimeout();
                Assert(sessionTimeout == 30 && acmTimeout == 30);
                Console.Out.WriteLine("ok");
            }

            ICallbackPrx twoway;
            {
                Console.Out.Write("testing stringToProxy for server object... ");
                Console.Out.Flush();
                twoway = ICallbackPrx.Parse($"c1/callback:{GetTestEndpoint(0)}", communicator);
                Console.Out.WriteLine("ok");
            }

            {
                Console.Out.Write("trying to ping server before session creation... ");
                Console.Out.Flush();
                try
                {
                    twoway.IcePing();
                    Assert(false);
                }
                catch (ConnectionLostException)
                {
                    Console.Out.WriteLine("ok");
                }
                catch (TransportException)
                {
                    Assert(false);
                }
            }

            {
                Console.Out.Write("trying to create session with wrong password... ");
                Console.Out.Flush();
                try
                {
                    router.CreateSession("userid", "xxx");
                    Assert(false);
                }
                catch (Glacier2.PermissionDeniedException)
                {
                    Console.Out.WriteLine("ok");
                }
                catch (Glacier2.CannotCreateSessionException)
                {
                    Assert(false);
                }
            }

            {
                Console.Out.Write("trying to destroy non-existing session... ");
                Console.Out.Flush();
                try
                {
                    router.DestroySession();
                    Assert(false);
                }
                catch (Glacier2.SessionNotExistException)
                {
                    Console.Out.WriteLine("ok");
                }
            }

            {
                Console.Out.Write("creating session with correct password... ");
                Console.Out.Flush();
                try
                {
                    router.CreateSession("userid", "abc123");
                }
                catch (Glacier2.PermissionDeniedException)
                {
                    Assert(false);
                }
                catch (Glacier2.CannotCreateSessionException)
                {
                    Assert(false);
                }
                Console.Out.WriteLine("ok");
            }

            {
                Console.Out.Write("trying to create a second session... ");
                Console.Out.Flush();
                try
                {
                    router.CreateSession("userid", "abc123");
                    Assert(false);
                }
                catch (Glacier2.PermissionDeniedException)
                {
                    Assert(false);
                }
                catch (Glacier2.CannotCreateSessionException)
                {
                    Console.Out.WriteLine("ok");
                }
            }

            {
                Console.Out.Write("pinging server after session creation... ");
                Console.Out.Flush();
                twoway.IcePing();
                Console.Out.WriteLine("ok");
            }

            {
                Console.Out.Write("pinging object with client endpoint... ");
                IObjectPrx baseC = IObjectPrx.Parse($"collocated:{GetTestEndpoint(50)}", communicator);
                try
                {
                    baseC.IcePing();
                }
                catch (ObjectNotExistException)
                {
                }
                Console.Out.WriteLine("ok");
            }

            ObjectAdapter adapter;

            {
                Console.Out.Write("creating and activating callback receiver adapter... ");
                Console.Out.Flush();
                communicator.SetProperty("Ice.PrintAdapterReady", "0");
                adapter = communicator.CreateObjectAdapterWithRouter("CallbackReceiverAdapter", router);
                adapter.Activate();
                Console.Out.WriteLine("ok");
            }

            string category;

            {
                Console.Out.Write("getting category from router... ");
                Console.Out.Flush();
                category = router.GetCategoryForClient();
                Console.Out.WriteLine("ok");
            }

            CallbackReceiver callbackReceiverImpl;
            ICallbackReceiver callbackReceiver;
            ICallbackReceiverPrx twowayR;
            ICallbackReceiverPrx fakeTwowayR;

            {
                Console.Out.Write("creating and adding callback receiver object... ");
                Console.Out.Flush();
                callbackReceiverImpl = new CallbackReceiver();
                callbackReceiver = callbackReceiverImpl;
                Identity callbackReceiverIdent = new Identity("callbackReceiver", category);
                twowayR = adapter.Add(callbackReceiverIdent, callbackReceiver, ICallbackReceiverPrx.Factory);
                Identity fakeCallbackReceiverIdent = new Identity("callbackReceiver", "dummy");
                fakeTwowayR = adapter.Add(fakeCallbackReceiverIdent, callbackReceiver, ICallbackReceiverPrx.Factory);
                Console.Out.WriteLine("ok");
            }

            {
                Console.Out.Write("testing oneway callback... ");
                Console.Out.Flush();
                ICallbackPrx oneway = twoway.Clone(oneway: true);
                ICallbackReceiverPrx onewayR = twowayR.Clone(oneway: true);
                Dictionary<string, string> context = new Dictionary<string, string>();
                context["_fwd"] = "o";
                oneway.initiateCallback(onewayR, context);
                callbackReceiverImpl.callbackOK();
                Console.Out.WriteLine("ok");
            }

            {
                Console.Out.Write("testing twoway callback... ");
                Console.Out.Flush();
                Dictionary<string, string> context = new Dictionary<string, string>();
                context["_fwd"] = "t";
                twoway.initiateCallback(twowayR, context);
                callbackReceiverImpl.callbackOK();
                Console.Out.WriteLine("ok");
            }

            {
                Console.Out.Write("ditto, but with user exception... ");
                Console.Out.Flush();
                Dictionary<string, string> context = new Dictionary<string, string>();
                context["_fwd"] = "t";
                try
                {
                    twoway.initiateCallbackEx(twowayR, context);
                    Assert(false);
                }
                catch (CallbackException ex)
                {
                    Assert(ex.someValue == 3.14);
                    Assert(ex.someString.Equals("3.14"));
                }
                callbackReceiverImpl.callbackOK();
                Console.Out.WriteLine("ok");
            }

            {
                Console.Out.Write("trying twoway callback with fake category... ");
                Console.Out.Flush();
                Dictionary<string, string> context = new Dictionary<string, string>();
                context["_fwd"] = "t";
                try
                {
                    twoway.initiateCallback(fakeTwowayR, context);
                    Assert(false);
                }
                catch (Ice.ObjectNotExistException)
                {
                    Console.Out.WriteLine("ok");
                }
            }

            {
                Console.Out.Write("testing whether other allowed category is accepted... ");
                Console.Out.Flush();
                Dictionary<string, string> context = new Dictionary<string, string>();
                context["_fwd"] = "t";
                ICallbackPrx otherCategoryTwoway =
                    twoway.Clone(Identity.Parse("c2/callback"), ICallbackPrx.Factory);
                otherCategoryTwoway.initiateCallback(twowayR, context);
                callbackReceiverImpl.callbackOK();
                Console.Out.WriteLine("ok");
            }

            {
                Console.Out.Write("testing whether disallowed category gets rejected... ");
                Console.Out.Flush();
                Dictionary<string, string> context = new Dictionary<string, string>();
                context["_fwd"] = "t";
                try
                {
                    ICallbackPrx otherCategoryTwoway =
                        twoway.Clone(Identity.Parse("c3/callback"), ICallbackPrx.Factory);
                    otherCategoryTwoway.initiateCallback(twowayR, context);
                    Assert(false);
                }
                catch (ObjectNotExistException)
                {
                    Console.Out.WriteLine("ok");
                }
            }

            {
                Console.Out.Write("testing whether user-id as category is accepted... ");
                Console.Out.Flush();
                Dictionary<string, string> context = new Dictionary<string, string>();
                context["_fwd"] = "t";
                ICallbackPrx otherCategoryTwoway =
                    twoway.Clone(Identity.Parse("_userid/callback"), ICallbackPrx.Factory);
                otherCategoryTwoway.initiateCallback(twowayR, context);
                callbackReceiverImpl.callbackOK();
                Console.Out.WriteLine("ok");
            }

            if (args.Length >= 1 && args[0].Equals("--shutdown"))
            {
                Console.Out.Write("testing server shutdown... ");
                Console.Out.Flush();
                twoway.shutdown();
                // No ping, otherwise the router prints a warning message if it's
                // started with --Ice.Warn.Connections.
                Console.Out.WriteLine("ok");
                /*
                  try
                  {
                  base.IcePing();
                  Assert(false);
                  }
                  // If we use the glacier router, the exact exception reason gets
                  // lost.
                  catch(Ice.UnknownLocalException ex)
                  {
                  Console.Out.WriteLine("ok");
                  }
                */
            }

            {
                Console.Out.Write("destroying session... ");
                Console.Out.Flush();
                try
                {
                    router.DestroySession();
                }
                catch (System.Exception)
                {
                    Assert(false);
                }

                Console.Out.WriteLine("ok");
            }

            {
                Console.Out.Write("trying to ping server after session destruction... ");
                Console.Out.Flush();
                try
                {
                    twoway.IcePing();
                    Assert(false);
                }
                catch (ConnectionLostException)
                {
                    Console.Out.WriteLine("ok");
                }
                catch (TransportException)
                {
                    Assert(false);
                }
            }

            if (args.Length >= 1 && args[0].Equals("--shutdown"))
            {
                {
                    Console.Out.Write("uninstalling router with communicator... ");
                    Console.Out.Flush();
                    communicator.SetDefaultRouter(null);
                    Console.Out.WriteLine("ok");
                }

                IProcessPrx process;
                {
                    Console.Out.Write("testing stringToProxy for admin object... ");
                    process = IProcessPrx.Parse($"Glacier2/admin -f Process:{GetTestEndpoint(51)}", communicator);
                    Console.Out.WriteLine("ok");
                }

                /*
                  {
                  Console.Out.Write("uninstalling router with process object... ");
                  processBase.ice_router(null);
                  Console.Out.WriteLine("ok");
                  }
                */

                Console.Out.Write("testing Glacier2 shutdown... ");
                process.Shutdown();
                try
                {
                    process.IcePing();
                    Assert(false);
                }
                catch (System.Exception)
                {
                    Console.Out.WriteLine("ok");
                }
            }
        }
    }

    public static int Main(string[] args) => TestDriver.RunTest<Client>(args);
}
