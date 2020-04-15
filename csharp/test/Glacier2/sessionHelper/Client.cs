//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using Test;
using System;
using System.Reflection;
using System.Threading;
using Ice;

[assembly: AssemblyTitle("IceTest")]
[assembly: AssemblyDescription("Ice test")]
[assembly: AssemblyCompany("ZeroC, Inc.")]

public class Client : Test.TestHelper
{
    public class SessionCallback1 : Glacier2.SessionCallback
    {
        public
        SessionCallback1(Client app)
        {
            _app = app;
        }

        public void
        connected(Glacier2.SessionHelper session)
        {
            test(false);
        }

        public void
        disconnected(Glacier2.SessionHelper session)
        {
            test(false);
        }

        public void
        connectFailed(Glacier2.SessionHelper session, System.Exception exception)
        {
            try
            {
                throw exception;
            }
            catch (Glacier2.PermissionDeniedException)
            {
                Console.Out.WriteLine("ok");

            }
            catch (System.Exception)
            {
                test(false);
            }
            finally
            {
                _app.wakeUp();
            }
        }

        public void
        createdCommunicator(Glacier2.SessionHelper session)
        {
            test(session.Communicator() != null);
        }

        private Client _app;
    }

    public class SessionCallback2 : Glacier2.SessionCallback
    {
        public
        SessionCallback2(Client app)
        {
            _app = app;
        }

        public void
        connected(Glacier2.SessionHelper session)
        {
            Console.Out.WriteLine("ok");
            _app.wakeUp();
        }

        public void
        disconnected(Glacier2.SessionHelper session)
        {
            Console.Out.WriteLine("ok");
            _app.wakeUp();
        }

        public void
        connectFailed(Glacier2.SessionHelper session, System.Exception ex)
        {
            Console.Out.WriteLine(ex.ToString());
            test(false);
        }

        public void
        createdCommunicator(Glacier2.SessionHelper session)
        {
            test(session.Communicator() != null);
        }

        private Client _app;
    }

    public class SessionCallback3 : Glacier2.SessionCallback
    {
        public
        SessionCallback3(Client app)
        {
            _app = app;
        }

        public void
        connected(Glacier2.SessionHelper session)
        {
            test(false);
        }

        public void
        disconnected(Glacier2.SessionHelper session)
        {
            test(false);
        }

        public void
        connectFailed(Glacier2.SessionHelper session, System.Exception exception)
        {
            try
            {
                throw exception;
            }
            catch (ConnectionRefusedException)
            {
                Console.Out.WriteLine("ok");
            }
            catch (System.Exception)
            {
                test(false);
            }
            finally
            {
                _app.wakeUp();
            }
        }

        public void
        createdCommunicator(Glacier2.SessionHelper session)
        {
            test(session.Communicator() != null);
        }

        private Client _app;
    }

    public class SessionCallback4 : Glacier2.SessionCallback
    {
        public
        SessionCallback4(Client app)
        {
            _app = app;
        }

        public void
        connected(Glacier2.SessionHelper session)
        {
            test(false);
        }

        public void
        disconnected(Glacier2.SessionHelper session)
        {
            test(false);
        }

        public void
        connectFailed(Glacier2.SessionHelper session, System.Exception exception)
        {
            try
            {
                throw exception;
            }
            catch (CommunicatorDestroyedException)
            {
                Console.Out.WriteLine("ok");
            }
            catch (System.FormatException)
            {
                Console.Out.WriteLine("ok");
            }
            catch (System.Exception)
            {
                test(false);
            }
            finally
            {
                _app.wakeUp();
            }
        }

        public void
        createdCommunicator(Glacier2.SessionHelper session)
        {
            test(session.Communicator() != null);
        }

        private Client _app;
    }

    public override void run(string[] args)
    {
        var properties = createTestProperties(ref args);
        properties["Ice.Warn.Connections"] = "0";
        properties["Ice.Default.Router"] = $"Glacier2/router:{getTestEndpoint(properties, 50)}";

        using var communicator = initialize(properties);

        string transport = getTestTransport();
        string host = getTestHost();

        Glacier2.SessionFactoryHelper factory = new Glacier2.SessionFactoryHelper(new SessionCallback1(this), properties);
        Glacier2.SessionHelper? session = null;

        //
        // Test to create a session with wrong userid/password
        //
        lock (this)
        {
            Console.Out.Write("testing SessionHelper connect with wrong userid/password... ");
            Console.Out.Flush();

            factory.setTransport(transport);
            session = factory.connect("userid", "xxx");
            while (true)
            {
                try
                {
                    if (!Monitor.Wait(this, 30000))
                    {
                        test(false);
                    }
                    break;
                }
                catch (ThreadInterruptedException)
                {
                }
            }
            test(!session.IsConnected());
        }

        properties.Remove("Ice.Default.Router");
        factory = new Glacier2.SessionFactoryHelper(new SessionCallback4(this), properties);
        lock (this)
        {
            Console.Out.Write("testing SessionHelper connect interrupt... ");
            Console.Out.Flush();
            factory.setRouterHost(host);
            factory.setPort(getTestPort(1));
            factory.setTransport(transport);
            session = factory.connect("userid", "abc123");

            Thread.Sleep(100);
            session.Destroy();

            while (true)
            {
                try
                {
                    if (!Monitor.Wait(this, 30000))
                    {
                        test(false);
                    }
                    break;
                }
                catch (ThreadInterruptedException)
                {
                }
            }
            test(!session.IsConnected());
        }

        factory = new Glacier2.SessionFactoryHelper(new SessionCallback2(this), properties);
        lock (this)
        {
            Console.Out.Write("testing SessionHelper connect... ");
            Console.Out.Flush();
            factory.setRouterHost(host);
            factory.setPort(getTestPort(50));
            factory.setTransport(transport);
            session = factory.connect("userid", "abc123");
            while (true)
            {
                try
                {
                    if (!Monitor.Wait(this, 30000))
                    {
                        test(false);
                    }
                    break;
                }
                catch (ThreadInterruptedException)
                {
                }
            }

            Console.Out.Write("testing SessionHelper isConnected after connect... ");
            Console.Out.Flush();
            test(session.IsConnected());
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing SessionHelper categoryForClient after connect... ");
            Console.Out.Flush();
            try
            {
                test(!session.CategoryForClient().Equals(""));
            }
            catch (Glacier2.SessionNotExistException)
            {
                test(false);
            }
            Console.Out.WriteLine("ok");

            test(session.Session() == null);

            Console.Out.Write("testing stringToProxy for server object... ");
            Console.Out.Flush();
            var @base = IObjectPrx.Parse($"callback:{getTestEndpoint(0)}", session.Communicator());
            Console.Out.WriteLine("ok");

            Console.Out.Write("pinging server after session creation... ");
            Console.Out.Flush();
            @base.IcePing();
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing checked cast for server object... ");
            Console.Out.Flush();
            var twoway = ICallbackPrx.CheckedCast(@base);
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing server shutdown... ");
            Console.Out.Flush();
            twoway.shutdown();
            Console.Out.WriteLine("ok");

            test(session.Communicator() != null);
            Console.Out.Write("testing SessionHelper destroy... ");
            Console.Out.Flush();
            session.Destroy();
            while (true)
            {
                try
                {
                    if (!Monitor.Wait(this, 30000))
                    {
                        test(false);
                    }
                    break;
                }
                catch (ThreadInterruptedException)
                {
                }
            }

            Console.Out.Write("testing SessionHelper isConnected after destroy... ");
            Console.Out.Flush();
            test(session.IsConnected() == false);
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing SessionHelper categoryForClient after destroy... ");
            Console.Out.Flush();
            try
            {
                test(!session.CategoryForClient().Equals(""));
                test(false);
            }
            catch (Glacier2.SessionNotExistException)
            {
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing SessionHelper session after destroy... ");
            test(session.Session() == null);
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing SessionHelper communicator after destroy... ");
            Console.Out.Flush();
            try
            {
                test(session.Communicator() != null);
                IObjectPrx.Parse("dummy", session.Communicator());
                test(false);
            }
            catch (CommunicatorDestroyedException)
            {
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("uninstalling router with communicator... ");
            Console.Out.Flush();
            communicator.SetDefaultRouter(null);
            Console.Out.WriteLine("ok");

            IObjectPrx processBase;
            {
                Console.Out.Write("testing stringToProxy for process object... ");
                processBase = IObjectPrx.Parse($"Glacier2/admin -f Process:{getTestEndpoint(51)}", communicator);
                Console.Out.WriteLine("ok");
            }

            IProcessPrx process;
            {
                Console.Out.Write("testing checked cast for admin object... ");
                process = Ice.IProcessPrx.CheckedCast(processBase);
                test(process != null);
                Console.Out.WriteLine("ok");
            }

            Console.Out.Write("testing Glacier2 shutdown... ");
            process.Shutdown();
            try
            {
                process.IcePing();
                test(false);
            }
            catch (System.Exception)
            {
                Console.Out.WriteLine("ok");
            }
        }

        factory = new Glacier2.SessionFactoryHelper(new SessionCallback3(this), properties);
        lock (this)
        {
            Console.Out.Write("testing SessionHelper connect after router shutdown... ");
            Console.Out.Flush();

            factory.setRouterHost(host);
            factory.setPort(getTestPort(50));
            factory.setTransport(transport);
            session = factory.connect("userid", "abc123");
            while (true)
            {
                try
                {
                    if (!Monitor.Wait(this, 30000))
                    {
                        test(false);
                    }
                    break;
                }
                catch (ThreadInterruptedException)
                {
                }
            }

            Console.Out.Write("testing SessionHelper isConnect after connect failure... ");
            Console.Out.Flush();
            test(session.IsConnected() == false);
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing SessionHelper communicator after connect failure... ");
            Console.Out.Flush();
            try
            {
                test(session.Communicator() != null);
                IObjectPrx.Parse("dummy", session.Communicator());
                test(false);
            }
            catch (CommunicatorDestroyedException)
            {
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing SessionHelper destroy after connect failure... ");
            Console.Out.Flush();
            session.Destroy();
            Console.Out.WriteLine("ok");
        }
    }

    public void
    wakeUp()
    {
        lock (this)
        {
            Monitor.Pulse(this);
        }
    }

    public static int Main(string[] args) => TestDriver.runTest<Client>(args);
}
