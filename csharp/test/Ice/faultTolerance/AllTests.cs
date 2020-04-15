//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using Test;

public class AllTests : Test.AllTests
{
    private static void exceptAbortI(Exception ex, TextWriter output)
    {
        try
        {
            throw ex;
        }
        catch (Ice.ConnectionLostException)
        {
        }
        catch (Ice.ConnectFailedException)
        {
        }
        catch (Ice.TransportException)
        {
        }
        catch (Exception)
        {
            output.WriteLine(ex.ToString());
            test(false);
        }
    }

    public static void allTests(TestHelper helper, List<int> ports)
    {
        Ice.Communicator communicator = helper.communicator();
        var output = helper.getWriter();
        output.Write("testing stringToProxy... ");
        output.Flush();
        string refString = "test";
        for (int i = 0; i < ports.Count; i++)
        {
            refString += ":" + helper.getTestEndpoint(ports[i]);
        }
        Ice.IObjectPrx basePrx = Ice.IObjectPrx.Parse(refString, communicator);
        test(basePrx != null);
        output.WriteLine("ok");

        output.Write("testing checked cast... ");
        output.Flush();
        ITestIntfPrx obj = ITestIntfPrx.CheckedCast(basePrx);
        test(obj != null);
        test(obj.Equals(basePrx));
        output.WriteLine("ok");

        int oldPid = 0;
        bool ami = false;
        for (int i = 1, j = 0; i <= ports.Count; ++i, ++j)
        {
            if (j > 3)
            {
                j = 0;
                ami = !ami;
            }

            if (!ami)
            {
                output.Write("testing server #" + i + "... ");
                output.Flush();
                int pid = obj.pid();
                test(pid != oldPid);
                output.WriteLine("ok");
                oldPid = pid;
            }
            else
            {
                output.Write("testing server #" + i + " with AMI... ");
                output.Flush();
                var pid = obj.pidAsync().Result;
                test(pid != oldPid);
                output.WriteLine("ok");
                oldPid = pid;
            }

            if (j == 0)
            {
                if (!ami)
                {
                    output.Write("shutting down server #" + i + "... ");
                    output.Flush();
                    obj.shutdown();
                    output.WriteLine("ok");
                }
                else
                {
                    output.Write("shutting down server #" + i + " with AMI... ");
                    obj.shutdownAsync().Wait();
                    output.WriteLine("ok");
                }
            }
            else if (j == 1 || i + 1 > ports.Count)
            {
                if (!ami)
                {
                    output.Write("aborting server #" + i + "... ");
                    output.Flush();
                    try
                    {
                        obj.abort();
                        test(false);
                    }
                    catch (Ice.ConnectionLostException)
                    {
                        output.WriteLine("ok");
                    }
                    catch (Ice.ConnectFailedException)
                    {
                        output.WriteLine("ok");
                    }
                    catch (Ice.TransportException)
                    {
                        output.WriteLine("ok");
                    }
                }
                else
                {
                    output.Write("aborting server #" + i + " with AMI... ");
                    output.Flush();
                    try
                    {
                        obj.abortAsync().Wait();
                        test(false);
                    }
                    catch (AggregateException ex)
                    {
                        exceptAbortI(ex.InnerException, output);
                    }
                    output.WriteLine("ok");
                }
            }
            else if (j == 2 || j == 3)
            {
                if (!ami)
                {
                    output.Write("aborting server #" + i + " and #" + (i + 1) + " with idempotent call... ");
                    output.Flush();
                    try
                    {
                        obj.idempotentAbort();
                        test(false);
                    }
                    catch (Ice.ConnectionLostException)
                    {
                        output.WriteLine("ok");
                    }
                    catch (Ice.ConnectFailedException)
                    {
                        output.WriteLine("ok");
                    }
                    catch (Ice.TransportException)
                    {
                        output.WriteLine("ok");
                    }
                }
                else
                {
                    output.Write("aborting server #" + i + " and #" + (i + 1) + " with idempotent AMI call... ");
                    output.Flush();
                    try
                    {
                        obj.idempotentAbortAsync().Wait();
                        test(false);
                    }
                    catch (AggregateException ex)
                    {
                        exceptAbortI(ex.InnerException, output);
                    }
                    output.WriteLine("ok");
                }
                ++i;
            }
            else
            {
                Debug.Assert(false);
            }
        }

        output.Write("testing whether all servers are gone... ");
        output.Flush();
        try
        {
            obj.IcePing();
            test(false);
        }
        catch (Exception)
        {
            output.WriteLine("ok");
        }
    }
}
