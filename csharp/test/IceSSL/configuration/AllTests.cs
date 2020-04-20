//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

//
// NOTE: This test is not interoperable with other language mappings.
//

using Ice;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Test;

public class AllTests
{
    private static bool IsCatalinaOrGreater =>
        AssemblyUtil.IsMacOS && Environment.OSVersion.Version.Major >= 19;

    private static X509Certificate2 createCertificate(string certPEM)
    {
        return new X509Certificate2(System.Text.Encoding.ASCII.GetBytes(certPEM));
    }

    private static Dictionary<string, string>
    CreateProperties(Dictionary<string, string> defaultProperties, string? cert = null, string? ca = null)
    {
        var properties = new Dictionary<string, string>(defaultProperties);
        properties["Ice.Plugin.IceSSL"] = "IceSSL:IceSSL.PluginFactory";
        string? value;
        if (defaultProperties.TryGetValue("IceSSL.DefaultDir", out value))
        {
            properties["IceSSL.DefaultDir"] = value;
        }

        if (defaultProperties.TryGetValue("Ice.Default.Host", out value))
        {
            properties["Ice.Default.Host"] = value;
        }

        if (defaultProperties.TryGetValue("Ice.IPv6", out value))
        {
            properties["Ice.IPv6"] = value;
        }

        properties["Ice.RetryIntervals"] = "-1";
        //properties["IceSSL.Trace.Security"] = "1";

        if (cert != null)
        {
            properties["IceSSL.CertFile"] = $"{cert}.p12";
        }

        if (ca != null)
        {
            properties["IceSSL.CAs"] = $"{ca}.pem";
        }
        properties["IceSSL.Password"] = "password";

        return properties;
    }

    public static Test.IServerFactoryPrx allTests(Test.TestHelper helper, string testDir)
    {
        Ice.Communicator? communicator = helper.Communicator();
        TestHelper.Assert(communicator != null);
        string factoryRef = "factory:" + helper.GetTestEndpoint(0, "tcp");

        var factory = Test.IServerFactoryPrx.Parse(factoryRef, communicator);

        string defaultHost = communicator.GetProperty("Ice.Default.Host") ?? "";
        string defaultDir = $"{testDir}/../certs";
        Dictionary<string, string> defaultProperties = communicator.GetProperties();
        defaultProperties["IceSSL.DefaultDir"] = defaultDir;

        //
        // Load the CA certificates. We could use the IceSSL.ImportCert property, but
        // it would be nice to remove the CA certificates when the test finishes, so
        // this test manually installs the certificates in the LocalMachine:AuthRoot
        // store.
        //
        // Note that the client and server are assumed to run on the same machine,
        // so the certificates installed by the client are also available to the
        // server.
        //
        string caCert1File = defaultDir + "/cacert1.pem";
        string caCert2File = defaultDir + "/cacert2.pem";
        X509Certificate2 caCert1 = new X509Certificate2(caCert1File);
        X509Certificate2 caCert2 = new X509Certificate2(caCert2File);

        TestHelper.Assert(Enumerable.SequenceEqual(createCertificate(File.ReadAllText(caCert1File)).RawData, caCert1.RawData));
        TestHelper.Assert(Enumerable.SequenceEqual(createCertificate(File.ReadAllText(caCert2File)).RawData, caCert2.RawData));

        X509Store store = new X509Store(StoreName.AuthRoot, StoreLocation.LocalMachine);
        bool isAdministrator = false;
        if (AssemblyUtil.IsWindows)
        {
            try
            {
                store.Open(OpenFlags.ReadWrite);
                isAdministrator = true;
            }
            catch (CryptographicException)
            {
                store.Open(OpenFlags.ReadOnly);
                Console.Out.WriteLine("warning: some test requires administrator privileges, run as Administrator to run all the tests.");
            }
        }

        Dictionary<string, string> clientProperties;
        Dictionary<string, string> serverProperties;
        try
        {
            string[] args = new string[0];

            Console.Out.Write("testing manual initialization... ");
            Console.Out.Flush();
            {
                clientProperties = CreateProperties(defaultProperties);
                clientProperties["Ice.InitPlugins"] = "0";
                var comm = new Communicator(ref args, clientProperties);
                var p = IObjectPrx.Parse("dummy:ssl -p 9999", comm);
                try
                {
                    p.IcePing();
                    TestHelper.Assert(false);
                }
                catch (InvalidOperationException)
                {
                    // expected
                }
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["Ice.InitPlugins"] = "0";
                var comm = new Communicator(ref args, clientProperties);
                comm.InitializePlugins();
                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                //
                // Supply our own certificate.
                //
                X509Certificate2 cert = new X509Certificate2(defaultDir + "/c_rsa_ca1.p12", "password");
                X509Certificate2Collection coll = new X509Certificate2Collection();
                coll.Add(cert);
                clientProperties = CreateProperties(defaultProperties);
                clientProperties["Ice.InitPlugins"] = "0";
                clientProperties["IceSSL.CAs"] = caCert1File;
                var comm = new Communicator(ref args, clientProperties);
                IceSSL.Plugin? plugin = (IceSSL.Plugin?)comm.GetPlugin("IceSSL");
                TestHelper.Assert(plugin != null);
                plugin.SetCertificates(coll);
                comm.InitializePlugins();
                var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.VerifyPeer"] = "2";
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }

            {
                //
                // Supply our own CA certificate.
                //
                X509Certificate2 cert = new X509Certificate2(defaultDir + "/cacert1.pem");
                X509Certificate2Collection coll = new X509Certificate2Collection();
                coll.Add(cert);
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1");
                clientProperties["Ice.InitPlugins"] = "0";
                var comm = new Communicator(ref args, clientProperties);
                IceSSL.Plugin? plugin = (IceSSL.Plugin?)comm.GetPlugin("IceSSL");
                TestHelper.Assert(plugin != null);
                plugin.SetCACertificates(coll);
                comm.InitializePlugins();
                var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.VerifyPeer"] = "2";
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing certificate verification... ");
            Console.Out.Flush();
            {
                //
                // Test IceSSL.VerifyPeer=0. Client does not have a certificate,
                // and it doesn't trust the server certificate.
                //
                clientProperties = CreateProperties(defaultProperties);
                clientProperties["IceSSL.VerifyPeer"] = "0";
                var comm = new Communicator(ref args, clientProperties);
                var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1");
                serverProperties["IceSSL.VerifyPeer"] = "0";
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.noCert();
                    TestHelper.Assert(!((IceSSL.ConnectionInfo)server.GetConnection().GetConnectionInfo()).Verified);
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();

                //
                // Test IceSSL.VerifyPeer=0. Client does not have a certificate,
                // but it still verifies the server's.
                //
                clientProperties = CreateProperties(defaultProperties, ca: "cacert1");
                clientProperties["IceSSL.VerifyPeer"] = "0";
                comm = new Communicator(ref args, clientProperties);
                fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1");
                serverProperties["IceSSL.VerifyPeer"] = "0";
                server = fact.createServer(serverProperties);
                try
                {
                    server!.noCert();
                    TestHelper.Assert(((IceSSL.ConnectionInfo)server.GetConnection().GetConnectionInfo()).Verified);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();

                //
                // Test IceSSL.VerifyPeer=1. Client does not have a certificate.
                //
                clientProperties = CreateProperties(defaultProperties, ca: "cacert1");
                comm = new Communicator(ref args, clientProperties);
                fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1");
                serverProperties["IceSSL.VerifyPeer"] = "1";
                server = fact.createServer(serverProperties);
                try
                {
                    server!.noCert();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);

                //
                // Test IceSSL.VerifyPeer=2. This should fail because the client
                // does not supply a certificate.
                //
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1");
                serverProperties["IceSSL.VerifyPeer"] = "2";
                server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (ConnectionLostException)
                {
                    // Expected.
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);

                comm.Destroy();

                //
                // Test IceSSL.VerifyPeer=1. Client has a certificate.
                //
                // Provide "cacert1" to the client to verify the server
                // certificate (without this the client connection wouln't be
                // able to provide the certificate chain).
                //
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                comm = new Communicator(ref args, clientProperties);
                fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.VerifyPeer"] = "1";
                server = fact.createServer(serverProperties);
                try
                {
                    X509Certificate2 clientCert =
                        new X509Certificate2(defaultDir + "/c_rsa_ca1.p12", "password");
                    server!.checkCert(clientCert.Subject, clientCert.Issuer);

                    X509Certificate2 serverCert =
                        new X509Certificate2(defaultDir + "/s_rsa_ca1.p12", "password");
                    X509Certificate2 caCert = new X509Certificate2(defaultDir + "/cacert1.pem");

                    IceSSL.ConnectionInfo info = (IceSSL.ConnectionInfo)server.GetConnection().GetConnectionInfo();
                    TestHelper.Assert(info.Certs!.Length == 2);
                    TestHelper.Assert(info.Verified);

                    TestHelper.Assert(caCert.Equals(info.Certs[1]));
                    TestHelper.Assert(serverCert.Equals(info.Certs[0]));
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);

                //
                // Test IceSSL.VerifyPeer=2. Client has a certificate.
                //
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.VerifyPeer"] = "2";
                server = fact.createServer(serverProperties);
                try
                {
                    X509Certificate2 clientCert = new X509Certificate2(defaultDir + "/c_rsa_ca1.p12", "password");
                    server!.checkCert(clientCert.Subject, clientCert.Issuer);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);

                comm.Destroy();

                //
                // Test IceSSL.VerifyPeer=1. This should fail because the
                // client doesn't trust the server's CA.
                //
                clientProperties = CreateProperties(defaultProperties);
                comm = new Communicator(ref args, clientProperties);
                fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1");
                serverProperties["IceSSL.VerifyPeer"] = "0";
                server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (SecurityException)
                {
                    // Expected.
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();

                //
                // Test IceSSL.VerifyPeer=1. This should fail because the
                // server doesn't trust the client's CA.
                //
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca2");
                clientProperties["IceSSL.VerifyPeer"] = "0";
                comm = new Communicator(ref args, clientProperties);
                fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1");
                serverProperties["IceSSL.VerifyPeer"] = "1";
                server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (SecurityException)
                {
                    // Expected.
                }
                catch (ConnectionLostException)
                {
                    // Expected.
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();

                //
                // This should succeed because the self signed certificate used by the server is
                // trusted.
                //
                clientProperties = CreateProperties(defaultProperties, ca: "cacert2");
                comm = new Communicator(ref args, clientProperties);
                fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "cacert2");
                serverProperties["IceSSL.VerifyPeer"] = "0";
                server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();

                //
                // This should l because the self signed certificate used by the server is not
                // trusted.
                //
                clientProperties = CreateProperties(defaultProperties);
                comm = new Communicator(ref args, clientProperties);
                fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "cacert2");
                serverProperties["IceSSL.VerifyPeer"] = "0";
                server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (SecurityException)
                {
                    // Expected.
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();

                //
                // Verify that IceSSL.CheckCertName has no effect in a server.
                //
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                comm = new Communicator(ref args, clientProperties);
                fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.CheckCertName"] = "1";
                server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();

                //
                // Test Hostname verification only when Ice.DefaultHost is 127.0.0.1
                // as that is the IP address used in the test certificates.
                //
                if (defaultHost.Equals("127.0.0.1"))
                {
                    //
                    // Test using localhost as target host
                    //
                    var props = new Dictionary<string, string>(defaultProperties);
                    props["Ice.Default.Host"] = "localhost";

                    //
                    // Target host matches the certificate DNS altName
                    //
                    {
                        clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                        clientProperties["IceSSL.CheckCertName"] = "1";
                        comm = new Communicator(ref args, clientProperties);

                        fact = IServerFactoryPrx.Parse(factoryRef, comm);
                        serverProperties = CreateProperties(props, "s_rsa_ca1_cn1", "cacert1");
                        serverProperties["IceSSL.CheckCertName"] = "1";
                        server = fact.createServer(serverProperties);
                        try
                        {
                            server!.IcePing();
                        }
                        catch (System.Exception ex)
                        {
                            //
                            // macOS catalina does not check the certificate common name
                            //
                            if (!AssemblyUtil.IsMacOS)
                            {
                                Console.WriteLine(ex.ToString());
                                TestHelper.Assert(false);
                            }
                        }
                        fact.destroyServer(server);
                        comm.Destroy();
                    }
                    //
                    // Target host does not match the certificate DNS altName
                    //
                    {
                        clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                        clientProperties["IceSSL.CheckCertName"] = "1";
                        comm = new Communicator(ref args, clientProperties);

                        fact = IServerFactoryPrx.Parse(factoryRef, comm);
                        serverProperties = CreateProperties(props, "s_rsa_ca1_cn2", "cacert1");
                        serverProperties["IceSSL.CheckCertName"] = "1";
                        server = fact.createServer(serverProperties);
                        try
                        {
                            server!.IcePing();
                            TestHelper.Assert(false);
                        }
                        catch (SecurityException)
                        {
                            // Expected
                        }
                        fact.destroyServer(server);
                        comm.Destroy();
                    }
                    //
                    // Target host matches the certificate Common Name and the certificate does not
                    // include a DNS altName
                    //
                    {
                        clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                        clientProperties["IceSSL.CheckCertName"] = "1";
                        comm = new Communicator(ref args, clientProperties);

                        fact = IServerFactoryPrx.Parse(factoryRef, comm);
                        serverProperties = CreateProperties(props, "s_rsa_ca1_cn3", "cacert1");
                        serverProperties["IceSSL.CheckCertName"] = "1";
                        server = fact.createServer(serverProperties);
                        try
                        {
                            server!.IcePing();
                        }
                        catch (Exception)
                        {
                            // macOS >= Catalina requires a DNS altName. DNS name as the Common Name is not trusted
                            TestHelper.Assert(IsCatalinaOrGreater);
                        }
                        fact.destroyServer(server);
                        comm.Destroy();
                    }

                    //
                    // Target host does not match the certificate Common Name and the certificate does not
                    // include a DNS altName
                    //
                    {
                        clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                        clientProperties["IceSSL.CheckCertName"] = "1";
                        comm = new Communicator(ref args, clientProperties);

                        fact = IServerFactoryPrx.Parse(factoryRef, comm);
                        serverProperties = CreateProperties(props, "s_rsa_ca1_cn4", "cacert1");
                        serverProperties["IceSSL.CheckCertName"] = "1";
                        server = fact.createServer(serverProperties);
                        try
                        {
                            server!.IcePing();
                            TestHelper.Assert(false);
                        }
                        catch (SecurityException)
                        {
                            // Expected
                        }
                        fact.destroyServer(server);
                        comm.Destroy();
                    }
                    //
                    // Target host matches the certificate Common Name and the certificate has
                    // a DNS altName that does not matches the target host
                    //
                    {
                        clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                        clientProperties["IceSSL.CheckCertName"] = "1";
                        comm = new Communicator(ref args, clientProperties);

                        fact = IServerFactoryPrx.Parse(factoryRef, comm);
                        serverProperties = CreateProperties(props, "s_rsa_ca1_cn5", "cacert1");
                        serverProperties["IceSSL.CheckCertName"] = "1";
                        server = fact.createServer(serverProperties);
                        try
                        {
                            server!.IcePing();
                            TestHelper.Assert(false);
                        }
                        catch (SecurityException)
                        {
                            // Expected
                        }
                        fact.destroyServer(server);
                        comm.Destroy();
                    }

                    //
                    // Test using 127.0.0.1 as target host
                    //

                    //
                    // Disabled for compatibility with older Windows
                    // versions.
                    //
                    /* //
                    // Target host matches the certificate IP altName
                    //
                    {
                        clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                        clientProperties["IceSSL.CheckCertName", "1");
                        comm = new Communicator(ref args, initData);

                        fact = Test.IServerFactoryPrxHelper.checkedCast(comm.stringToProxy(factoryRef));
                        TestHelper.Assert(fact != null);
                        serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1_cn6", "cacert1");
                        d["IceSSL.CheckCertName"] = "1";
                        server = fact.createServer(d);
                        try
                        {
                            server.IcePing();
                        }
                        catch(System.Exception)
                        {
                            TestHelper.Assert(false);
                        }
                        fact.destroyServer(server);
                        comm.Destroy();
                    }
                    //
                    // Target host does not match the certificate IP altName
                    //
                    {
                        clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                        clientProperties["IceSSL.CheckCertName", "1");
                        comm = new Communicator(ref args, initData);

                        fact = Test.IServerFactoryPrxHelper.checkedCast(comm.stringToProxy(factoryRef));
                        TestHelper.Assert(fact != null);
                        serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1_cn7", "cacert1");
                        d["IceSSL.CheckCertName"] = "1";
                        server = fact.createServer(d);
                        try
                        {
                            server.IcePing();
                            TestHelper.Assert(false);
                        }
                        catch(Ice.SecurityException)
                        {
                            // Expected
                        }
                        fact.destroyServer(server);
                        comm.Destroy();
                    }*/
                    //
                    // Target host is an IP addres that matches the CN and the certificate doesn't
                    // include an IP altName.
                    //
                    {
                        clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                        clientProperties["IceSSL.CheckCertName"] = "1";
                        comm = new Communicator(ref args, clientProperties);

                        fact = IServerFactoryPrx.Parse(factoryRef, comm);
                        serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1_cn8", "cacert1");
                        serverProperties["IceSSL.CheckCertName"] = "1";
                        server = fact.createServer(serverProperties);
                        try
                        {
                            server!.IcePing();
                        }
                        catch (SecurityException ex)
                        {
                            //
                            // macOS catalina does not check the certificate common name
                            //
                            if (!AssemblyUtil.IsMacOS)
                            {
                                Console.WriteLine(ex.ToString());
                                TestHelper.Assert(false);
                            }
                        }
                        fact.destroyServer(server);
                        comm.Destroy();
                    }

                    //
                    // Target host does not match the certificate DNS altName, connection should succeed
                    // because IceSSL.VerifyPeer is set to 0.
                    //
                    {
                        clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                        clientProperties["IceSSL.CheckCertName"] = "1";
                        clientProperties["IceSSL.VerifyPeer"] = "0";
                        comm = new Communicator(ref args, clientProperties);

                        fact = IServerFactoryPrx.Parse(factoryRef, comm);
                        serverProperties = CreateProperties(props, "s_rsa_ca1_cn2", "cacert1");
                        server = fact.createServer(serverProperties);
                        try
                        {
                            server!.IcePing();
                            IceSSL.ConnectionInfo info = (IceSSL.ConnectionInfo)server.GetConnection().GetConnectionInfo();
                            TestHelper.Assert(!info.Verified);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.ToString());
                            TestHelper.Assert(false);
                        }
                        fact.destroyServer(server);
                        comm.Destroy();
                    }

                    //
                    // Target host does not match the certificate DNS altName, connection should succeed
                    // because IceSSL.CheckCertName is set to 0.
                    //
                    {
                        clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                        clientProperties["IceSSL.CheckCertName"] = "0";
                        comm = new Communicator(ref args, clientProperties);

                        fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                        serverProperties = CreateProperties(props, "s_rsa_ca1_cn2", "cacert1");
                        serverProperties["IceSSL.CheckCertName"] = "1";
                        server = fact.createServer(serverProperties);
                        try
                        {
                            server!.IcePing();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.ToString());
                            TestHelper.Assert(false);
                        }
                        fact.destroyServer(server);
                        comm.Destroy();
                    }
                }
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing certificate chains... ");
            Console.Out.Flush();
            {
                X509Store certStore = new X509Store("My", StoreLocation.CurrentUser);
                certStore.Open(OpenFlags.ReadWrite);
                X509Certificate2Collection certs = new X509Certificate2Collection();
                var storageFlags = X509KeyStorageFlags.DefaultKeySet;
                if (AssemblyUtil.IsMacOS)
                {
                    //
                    // On macOS, we need to mark the key exportable because the addition of the key to the
                    // cert store requires to move the key from on keychain to another (which requires the
                    // Exportable flag... see https://github.com/dotnet/corefx/issues/25631)
                    //
                    storageFlags |= X509KeyStorageFlags.Exportable;
                }
                certs.Import(defaultDir + "/s_rsa_cai2.p12", "password", storageFlags);
                foreach (X509Certificate2 cert in certs)
                {
                    certStore.Add(cert);
                }
                try
                {
                    IceSSL.ConnectionInfo info;

                    clientProperties = CreateProperties(defaultProperties);
                    clientProperties["IceSSL.VerifyPeer"] = "0";
                    Communicator comm = new Communicator(clientProperties);

                    Test.IServerFactoryPrx fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);

                    //
                    // The client can't verify the server certificate but it should
                    // still provide it. "s_rsa_ca1" doesn't include the root so the
                    // cert size should be 1.
                    //
                    serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1");
                    serverProperties["IceSSL.VerifyPeer"] = "0";
                    Test.IServerPrx? server = fact.createServer(serverProperties);
                    try
                    {
                        info = (IceSSL.ConnectionInfo)server!.GetConnection().GetConnectionInfo();
                        TestHelper.Assert(info.Certs!.Length == 1);
                        TestHelper.Assert(!info.Verified);
                    }
                    catch (System.Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                        TestHelper.Assert(false);
                    }
                    fact.destroyServer(server);

                    //
                    // Setting the CA for the server shouldn't change anything, it
                    // shouldn't modify the cert chain sent to the client.
                    //
                    serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                    serverProperties["IceSSL.VerifyPeer"] = "0";
                    server = fact.createServer(serverProperties);
                    try
                    {
                        info = (IceSSL.ConnectionInfo)server!.GetConnection().GetConnectionInfo();
                        TestHelper.Assert(info.Certs!.Length == 1);
                        TestHelper.Assert(!info.Verified);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                        TestHelper.Assert(false);
                    }
                    fact.destroyServer(server);

                    //
                    // The client can't verify the server certificate but should
                    // still provide it. "s_rsa_wroot_ca1" includes the root so
                    // the cert size should be 2.
                    //
                    serverProperties = CreateProperties(defaultProperties, "s_rsa_wroot_ca1");
                    serverProperties["IceSSL.VerifyPeer"] = "0";
                    server = fact.createServer(serverProperties);
                    try
                    {
                        info = (IceSSL.ConnectionInfo)server!.GetConnection().GetConnectionInfo();
                        TestHelper.Assert(info.Certs!.Length == 1); // Like the SChannel transport, .NET never sends the root.
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                        TestHelper.Assert(false);
                    }
                    fact.destroyServer(server);
                    comm.Destroy();

                    //
                    // Now the client verifies the server certificate
                    //
                    clientProperties = CreateProperties(defaultProperties, ca: "cacert1");
                    clientProperties["IceSSL.VerifyPeer"] = "1";
                    comm = new Communicator(clientProperties);

                    fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);

                    {
                        serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1");
                        serverProperties["IceSSL.VerifyPeer"] = "0";
                        server = fact.createServer(serverProperties);
                        try
                        {
                            info = (IceSSL.ConnectionInfo)server!.GetConnection().GetConnectionInfo();
                            TestHelper.Assert(info.Certs!.Length == 2);
                            TestHelper.Assert(info.Verified);
                        }
                        catch (System.Exception ex)
                        {
                            Console.WriteLine(ex.ToString());
                            TestHelper.Assert(false);
                        }
                        fact.destroyServer(server);
                    }

                    //
                    // Try certificate with one intermediate and VerifyDepthMax=2
                    //
                    clientProperties = CreateProperties(defaultProperties, ca: "cacert1");
                    clientProperties["IceSSL.VerifyPeer"] = "1";
                    clientProperties["IceSSL.VerifyDepthMax"] = "2";
                    comm = new Communicator(clientProperties);

                    fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);

                    {
                        serverProperties = CreateProperties(defaultProperties, "s_rsa_cai1");
                        serverProperties["IceSSL.VerifyPeer"] = "0";
                        server = fact.createServer(serverProperties);
                        try
                        {
                            _ = server!.GetConnection().GetConnectionInfo();
                            TestHelper.Assert(false);
                        }
                        catch (SecurityException)
                        {
                            // Chain length too long
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.ToString());
                            TestHelper.Assert(false);
                        }
                        fact.destroyServer(server);
                    }
                    comm.Destroy();

                    if (AssemblyUtil.IsWindows)
                    {
                        //
                        // The certificate chain on Linux doesn't include the intermeidate
                        // certificates see ICE-8576
                        //

                        //
                        // Set VerifyDepthMax to 3 (the default)
                        //
                        clientProperties = CreateProperties(defaultProperties, ca: "cacert1");
                        clientProperties["IceSSL.VerifyPeer"] = "1";
                        //clientProperties["IceSSL.VerifyDepthMax", "3");
                        comm = new Communicator(clientProperties);

                        fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);

                        {
                            serverProperties = CreateProperties(defaultProperties, "s_rsa_cai1");
                            serverProperties["IceSSL.VerifyPeer"] = "0";
                            server = fact.createServer(serverProperties);
                            try
                            {
                                info = (IceSSL.ConnectionInfo)server!.GetConnection().GetConnectionInfo();
                                TestHelper.Assert(info.Certs!.Length == 3);
                                TestHelper.Assert(info.Verified);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.ToString());
                                TestHelper.Assert(false);
                            }
                            fact.destroyServer(server);
                        }

                        {
                            serverProperties = CreateProperties(defaultProperties, "s_rsa_cai2");
                            serverProperties["IceSSL.VerifyPeer"] = "0";
                            server = fact.createServer(serverProperties);
                            try
                            {
                                _ = server!.GetConnection().GetConnectionInfo();
                                TestHelper.Assert(false);
                            }
                            catch (SecurityException)
                            {
                                // Chain length too long
                            }
                            fact.destroyServer(server);
                        }
                        comm.Destroy();

                        //
                        // Increase VerifyDepthMax to 4
                        //
                        clientProperties = CreateProperties(defaultProperties, ca: "cacert1");
                        clientProperties["IceSSL.VerifyPeer"] = "1";
                        clientProperties["IceSSL.VerifyDepthMax"] = "4";
                        comm = new Communicator(clientProperties);

                        fact = IServerFactoryPrx.Parse(factoryRef, comm);

                        {
                            serverProperties = CreateProperties(defaultProperties, "s_rsa_cai2");
                            serverProperties["IceSSL.VerifyPeer"] = "0";
                            server = fact.createServer(serverProperties);
                            try
                            {
                                info = (IceSSL.ConnectionInfo)server!.GetConnection().GetConnectionInfo();
                                TestHelper.Assert(info.Certs!.Length == 4);
                                TestHelper.Assert(info.Verified);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.ToString());
                                TestHelper.Assert(false);
                            }
                            fact.destroyServer(server);
                        }

                        comm.Destroy();

                        //
                        // Increase VerifyDepthMax to 4
                        //
                        clientProperties = CreateProperties(defaultProperties, "c_rsa_cai2", "cacert1");
                        clientProperties["IceSSL.VerifyPeer"] = "1";
                        clientProperties["IceSSL.VerifyDepthMax"] = "4";
                        comm = new Communicator(clientProperties);

                        fact = IServerFactoryPrx.Parse(factoryRef, comm);
                        {
                            serverProperties = CreateProperties(defaultProperties, "s_rsa_cai2", "cacert1");
                            serverProperties["IceSSL.VerifyPeer"] = "2";
                            server = fact.createServer(serverProperties);
                            try
                            {
                                server!.GetConnection();
                                TestHelper.Assert(false);
                            }
                            catch (ConnectionLostException)
                            {
                                // Expected
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.ToString());
                                TestHelper.Assert(false);
                            }
                            fact.destroyServer(server);
                        }

                        {
                            serverProperties = CreateProperties(defaultProperties, "s_rsa_cai2", "cacert1");
                            serverProperties["IceSSL.VerifyPeer"] = "2";
                            serverProperties["IceSSL.VerifyDepthMax"] = "4";
                            server = fact.createServer(serverProperties);
                            try
                            {
                                server!.GetConnection();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.ToString());
                                TestHelper.Assert(false);
                            }
                            fact.destroyServer(server);
                        }

                        comm.Destroy();
                    }
                }
                finally
                {
                    foreach (X509Certificate2 cert in certs)
                    {
                        certStore.Remove(cert);
                    }
                }
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing custom certificate verifier... ");
            Console.Out.Flush();
            {
                //
                // Verify that a server certificate is present.
                //
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);
                IceSSL.Plugin? plugin = (IceSSL.Plugin?)comm.GetPlugin("IceSSL");
                TestHelper.Assert(plugin != null);
                var verifier = new CertificateVerifier();
                plugin.SetCertificateVerifier(verifier);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.VerifyPeer"] = "2";
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    TestHelper.Assert(server != null);
                    var info = (IceSSL.ConnectionInfo)server.GetConnection().GetConnectionInfo();
                    TestHelper.Assert(info.Cipher != null);
                    server.checkCipher(info.Cipher);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                TestHelper.Assert(verifier.invoked());
                TestHelper.Assert(verifier.hadCert());

                //
                // Have the verifier return false. Close the connection explicitly
                // to force a new connection to be established.
                //
                verifier.reset();
                verifier.returnValue(false);
                server.GetConnection().Close(Ice.ConnectionClose.GracefullyWithWait);
                try
                {
                    server.IcePing();
                    TestHelper.Assert(false);
                }
                catch (SecurityException)
                {
                    // Expected.
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                TestHelper.Assert(verifier.invoked());
                TestHelper.Assert(verifier.hadCert());
                fact.destroyServer(server);

                comm.Destroy();
            }
            {
                //
                // Verify that verifier is installed via property.
                //
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1");
                clientProperties["IceSSL.CertVerifier"] = "CertificateVerifier";
                Communicator comm = new Communicator(ref args, clientProperties);
                IceSSL.Plugin? plugin = (IceSSL.Plugin?)comm.GetPlugin("IceSSL");
                TestHelper.Assert(plugin != null);
                TestHelper.Assert(plugin.GetCertificateVerifier() != null);
                comm.Destroy();
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing protocols... ");
            Console.Out.Flush();
            {
                //
                // This should fail because the client and server have no protocol
                // in common.
                //
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.Protocols"] = "tls1_1";
                Communicator comm = new Communicator(ref args, clientProperties);
                var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.VerifyPeer"] = "2";
                serverProperties["IceSSL.Protocols"] = "tls1_2";
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    //TestHelper.Assert(false);
                }
                catch (SecurityException)
                {
                    // Expected.
                }
                catch (ConnectionLostException)
                {
                    // Expected.
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();

                //
                // This should succeed.
                //
                comm = new Communicator(ref args, clientProperties);
                fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.VerifyPeer"] = "2";
                serverProperties["IceSSL.Protocols"] = "tls1_1, tls1_2";
                server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (System.Exception ex)
                {
                    if (ex.ToString().IndexOf("no protocols available") < 0) // Expected if TLS1.1 is disabled (RHEL8)
                    {
                        Console.WriteLine(ex.ToString());
                        TestHelper.Assert(false);
                    }
                }
                fact.destroyServer(server);
                comm.Destroy();

                try
                {
                    clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                    clientProperties["IceSSL.Protocols"] = "tls1_2";
                    comm = new Communicator(ref args, clientProperties);
                    fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                    serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                    serverProperties["IceSSL.VerifyPeer"] = "2";
                    serverProperties["IceSSL.Protocols"] = "tls1_2";
                    server = fact.createServer(serverProperties);
                    server!.IcePing();

                    fact.destroyServer(server);
                    comm.Destroy();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing expired certificates... ");
            Console.Out.Flush();
            {
                //
                // This should fail because the server's certificate is expired.
                //
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);
                var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1_exp", "cacert1");
                serverProperties["IceSSL.VerifyPeer"] = "2";
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (SecurityException)
                {
                    // Expected.
                }
                catch (System.Exception ex)
                {
                    Console.Out.Write(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();

                //
                // This should fail because the client's certificate is expired.
                //
                clientProperties["IceSSL.CertFile"] = "c_rsa_ca1_exp.p12";
                comm = new Communicator(ref args, clientProperties);
                fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.VerifyPeer"] = "2";
                server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (ConnectionLostException)
                {
                    // Expected.
                }
                catch (Exception ex)
                {
                    Console.Out.Write(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            Console.Out.WriteLine("ok");

            if (AssemblyUtil.IsWindows && isAdministrator)
            {
                //
                // LocalMachine certificate store is not supported on non
                // Windows platforms.
                //
                Console.Out.Write("testing multiple CA certificates... ");
                Console.Out.Flush();
                {
                    clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1");
                    clientProperties["IceSSL.UsePlatformCAs"] = "1";
                    Communicator comm = new Communicator(ref args, clientProperties);
                    var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                    serverProperties = CreateProperties(defaultProperties, "s_rsa_ca2");
                    serverProperties["IceSSL.VerifyPeer"] = "2";
                    serverProperties["IceSSL.UsePlatformCAs"] = "1";
                    store.Add(caCert1);
                    store.Add(caCert2);
                    Test.IServerPrx? server = fact.createServer(serverProperties);
                    try
                    {
                        server!.IcePing();
                    }
                    catch (System.Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                        TestHelper.Assert(false);
                    }
                    fact.destroyServer(server);
                    store.Remove(caCert1);
                    store.Remove(caCert2);
                    comm.Destroy();
                }
                Console.Out.WriteLine("ok");
            }

            Console.Out.Write("testing multiple CA certificates... ");
            Console.Out.Flush();
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacerts");
                Communicator comm = new Communicator(clientProperties);
                Test.IServerFactoryPrx fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca2", "cacerts");
                serverProperties["IceSSL.VerifyPeer"] = "2";
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing DER CA certificate... ");
            Console.Out.Flush();
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1");
                clientProperties["IceSSL.CAs"] = "cacert1.der";
                using var comm = new Communicator(clientProperties);
                var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1");
                serverProperties["IceSSL.VerifyPeer"] = "2";
                serverProperties["IceSSL.CAs"] = "cacert1.der";
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing passwords... ");
            Console.Out.Flush();
            {
                //
                // Test password failure.
                //
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1");
                // Don't specify the password.
                clientProperties.Remove("IceSSL.Password");
                try
                {
                    new Communicator(ref args, clientProperties);
                    TestHelper.Assert(false);
                }
                catch (System.Exception)
                {
                    // Expected.
                }
            }
            {
                //
                // Test password failure with callback.
                //
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1");
                clientProperties["Ice.InitPlugins"] = "0";
                // Don't specify the password.
                clientProperties.Remove("IceSSL.Password");
                using var comm = new Communicator(ref args, clientProperties);
                IceSSL.Plugin? plugin = (IceSSL.Plugin?)comm.GetPlugin("IceSSL");
                TestHelper.Assert(plugin != null);
                var cb = new PasswordCallback("bogus");
                plugin.SetPasswordCallback(cb);
                try
                {
                    comm.InitializePlugins();
                    TestHelper.Assert(false);
                }
                catch (System.Exception)
                {
                    // Expected.
                }
            }
            {
                //
                // Test installation of password callback.
                //
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1");
                clientProperties["Ice.InitPlugins"] = "0";
                // Don't specify the password.
                clientProperties.Remove("IceSSL.Password");
                Communicator comm = new Communicator(ref args, clientProperties);
                IceSSL.Plugin? plugin = (IceSSL.Plugin?)comm.GetPlugin("IceSSL");
                TestHelper.Assert(plugin != null);
                var cb = new PasswordCallback();
                plugin.SetPasswordCallback(cb);
                TestHelper.Assert(plugin.GetPasswordCallback() == cb);
                try
                {
                    comm.InitializePlugins();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                comm.Destroy();
            }
            {
                //
                // Test password callback property.
                //
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1");
                clientProperties["IceSSL.PasswordCallback"] = "PasswordCallback";
                // Don't specify the password.
                clientProperties.Remove("IceSSL.Password");
                Communicator comm = new Communicator(ref args, clientProperties);
                IceSSL.Plugin? plugin = (IceSSL.Plugin?)comm.GetPlugin("IceSSL");
                TestHelper.Assert(plugin != null);
                TestHelper.Assert(plugin.GetPasswordCallback() != null);
                comm.Destroy();
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing IceSSL.TrustOnly... ");
            Console.Out.Flush();
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly"] =
                    "C=US, ST=Florida, O=ZeroC\\, Inc.,OU=Ice, emailAddress=info@zeroc.com, CN=Server";
                Communicator comm = new Communicator(ref args, clientProperties);

                Test.IServerFactoryPrx fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly"] =
                    "!C=US, ST=Florida, O=ZeroC\\, Inc.,OU=Ice, emailAddress=info@zeroc.com, CN=Server";
                Communicator comm = new Communicator(ref args, clientProperties);

                Test.IServerFactoryPrx fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (System.Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly"] =
                    "C=US, ST=Florida, O=\"ZeroC, Inc.\",OU=Ice, emailAddress=info@zeroc.com, CN=Server";
                Communicator comm = new Communicator(ref args, clientProperties);

                Test.IServerFactoryPrx fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);

                Test.IServerFactoryPrx fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly"] =
                    "C=US, ST=Florida, O=ZeroC\\, Inc.,OU=Ice, emailAddress=info@zeroc.com, CN=Client";
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly"] =
                    "!C=US, ST=Florida, O=ZeroC\\, Inc.,OU=Ice, emailAddress=info@zeroc.com, CN=Client";
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (System.Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly"] = "CN=Server";
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly"] = "!CN=Server";
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (System.Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly"] = "CN=Client";
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly"] = "!CN=Client";
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (System.Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly"] = "CN=Client";
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                Test.IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (System.Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);

                IServerFactoryPrx fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly"] = "CN=Server";
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly"] = "C=Canada,CN=Server";
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly"] = "!C=Canada,CN=Server";
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly"] = "C=Canada;CN=Server";
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly"] = "!C=Canada;!CN=Server";
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly"] = "!CN=Server1"; // Should not match "Server"
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                var comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly"] = "!CN=Client1"; // Should not match "Client"
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                //
                // Rejection takes precedence (client).
                //
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly"] = "ST=Florida;!CN=Server;C=US";
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                //
                // Rejection takes precedence (server).
                //
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly"] = "C=US;!CN=Client;ST=Florida";
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing IceSSL.TrustOnly.Client... ");
            Console.Out.Flush();
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly.Client"] =
                    "C=US, ST=Florida, O=ZeroC\\, Inc.,OU=Ice, emailAddress=info@zeroc.com, CN=Server";
                var comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                // Should have no effect.
                serverProperties["IceSSL.TrustOnly.Client"] =
                    "C=US, ST=Florida, O=ZeroC\\, Inc.,OU=Ice, emailAddress=info@zeroc.com, CN=Server";
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly.Client"] =
                    "!C=US, ST=Florida, O=ZeroC\\, Inc.,OU=Ice, emailAddress=info@zeroc.com, CN=Server";
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                var comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                // Should have no effect.
                serverProperties["IceSSL.TrustOnly.Client"] = "!CN=Client";
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly.Client"] = "CN=Client";
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                clientProperties["IceSSL.TrustOnly.Client"] = "!CN=Client";
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing IceSSL.TrustOnly.Server... ");
            Console.Out.Flush();
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                // Should have no effect.
                clientProperties["IceSSL.TrustOnly.Server"] =
                    "C=US, ST=Florida, O=ZeroC\\, Inc.,OU=Ice, emailAddress=info@zeroc.com, CN=Client";
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly.Server"] =
                    "C=US, ST=Florida, O=ZeroC\\, Inc.,OU=Ice, emailAddress=info@zeroc.com, CN=Client";
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly.Server"] =
                    "!C=US, ST=Florida, O=ZeroC\\, Inc.,OU=Ice, emailAddress=info@zeroc.com, CN=Client";
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                // Should have no effect.
                clientProperties["IceSSL.TrustOnly.Server"] = "!CN=Server";
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);

                IServerFactoryPrx fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly.Server"] = "CN=Server";
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly.Server"] = "!CN=Client";
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing IceSSL.TrustOnly.Server.<AdapterName>... ");
            Console.Out.Flush();
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly.Server"] = "CN=bogus";
                serverProperties["IceSSL.TrustOnly.Server.ServerAdapter"] =
                    "C=US, ST=Florida, O=ZeroC\\, Inc.,OU=Ice, emailAddress=info@zeroc.com, CN=Client";
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly.Server.ServerAdapter"] =
                    "!C=US, ST=Florida, O=ZeroC\\, Inc.,OU=Ice, emailAddress=info@zeroc.com, CN=Client";
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly.Server.ServerAdapter"] = "CN=bogus";
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                    TestHelper.Assert(false);
                }
                catch (Exception)
                {
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            {
                clientProperties = CreateProperties(defaultProperties, "c_rsa_ca1", "cacert1");
                Communicator comm = new Communicator(ref args, clientProperties);

                var fact = IServerFactoryPrx.Parse(factoryRef, comm);
                serverProperties = CreateProperties(defaultProperties, "s_rsa_ca1", "cacert1");
                serverProperties["IceSSL.TrustOnly.Server.ServerAdapter"] = "!CN=bogus";
                IServerPrx? server = fact.createServer(serverProperties);
                try
                {
                    server!.IcePing();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    TestHelper.Assert(false);
                }
                fact.destroyServer(server);
                comm.Destroy();
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing IceSSL.FindCerts properties... ");
            Console.Out.Flush();
            {
                string[] clientFindCertProperties = new string[]
                {
                    "SUBJECTDN:'CN=Client, OU=Ice, O=\"ZeroC, Inc.\", L=Jupiter, S=Florida, C=US, E=info@zeroc.com'",
                    "ISSUER:'ZeroC, Inc.' SUBJECT:Client SERIAL:02",
                    "ISSUERDN:'CN=ZeroC Test CA 1, OU=Ice, O=\"ZeroC, Inc.\",L=Jupiter, S=Florida, C=US,E=info@zeroc.com' SUBJECT:Client",
                    "THUMBPRINT:'10 8D FB DE 94 EE 36 AC AC 3D 58 48 46 AE A4 28 C7 D2 49 A9'",
                    "SUBJECTKEYID:'15 60 69 5F C5 27 48 7F 25 99 3F 3D D8 2E CB C2 F4 66 03 53'"
                };

                string[] serverFindCertProperties = new string[]
                {
                    "SUBJECTDN:'CN=Server, OU=Ice, O=\"ZeroC, Inc.\", L=Jupiter, S=Florida, C=US, E=info@zeroc.com'",
                    "ISSUER:'ZeroC, Inc.' SUBJECT:Server SERIAL:01",
                    "ISSUERDN:'CN=ZeroC Test CA 1, OU=Ice, O=\"ZeroC, Inc.\", L=Jupiter, S=Florida, C=US,E=info@zeroc.com' SUBJECT:Server",
                    "THUMBPRINT:'FF 66 AD CF D5 DA 3E E0 D9 91 E6 6B 8E 74 82 3A 54 E6 68 4A'",
                    "SUBJECTKEYID:'14 56 24 99 69 6B AD B3 FB 72 0E 4D B4 DC 9E A8 7F DD B0 E3'"
                };

                string[] failFindCertProperties = new string[]
                {
                    "nolabel",
                    "unknownlabel:foo",
                    "LABEL:",
                    "SUBJECTDN:'CN = Client, E = infox@zeroc.com, OU = Ice, O = \"ZeroC, Inc.\", S = Florida, C = US'",
                    "ISSUER:'ZeroC, Inc.' SUBJECT:Client SERIAL:'02 02'",
                    "ISSUERDN:'E=info@zeroc.com, CN=ZeroC Test CA 1, OU=Ice, O=\"ZeroC, Inc.\"," +
                        " L=Jupiter, S=Florida, C=ES' SUBJECT:Client",
                    "THUMBPRINT:'27 e0 18 c9 23 12 6c f0 5c da fa 36 5a 4c 63 5a e2 53 07 ff'",
                    "SUBJECTKEYID:'a6 42 aa 17 04 41 86 56 67 e4 04 64 59 34 30 c7 4c 6b ef ff'"
                };

                string[] certificates = new string[] { "/s_rsa_ca1.p12", "/c_rsa_ca1.p12" };

                X509Store certStore = new X509Store("My", StoreLocation.CurrentUser);
                certStore.Open(OpenFlags.ReadWrite);
                var storageFlags = X509KeyStorageFlags.DefaultKeySet;
                if (AssemblyUtil.IsMacOS)
                {
                    //
                    // On macOS, we need to mark the key exportable because the addition of the key to the
                    // cert store requires to move the key from on keychain to another (which requires the
                    // Exportable flag... see https://github.com/dotnet/corefx/issues/25631)
                    //
                    storageFlags |= X509KeyStorageFlags.Exportable;
                }
                try
                {
                    foreach (string cert in certificates)
                    {
                        certStore.Add(new X509Certificate2(defaultDir + cert, "password", storageFlags));
                    }

                    for (int i = 0; i < clientFindCertProperties.Length; ++i)
                    {
                        clientProperties = CreateProperties(defaultProperties, ca: "cacert1");
                        clientProperties["IceSSL.CertStore"] = "My";
                        clientProperties["IceSSL.CertStoreLocation"] = "CurrentUser";
                        clientProperties["IceSSL.FindCert"] = clientFindCertProperties[i];
                        //
                        // Use TrustOnly to ensure the peer has pick the expected certificate.
                        //
                        clientProperties["IceSSL.TrustOnly"] = "CN=Server";
                        Communicator comm = new Communicator(ref args, clientProperties);

                        var fact = Test.IServerFactoryPrx.Parse(factoryRef, comm);
                        serverProperties = CreateProperties(defaultProperties, ca: "cacert1");
                        // Use deprecated property here to test it
                        serverProperties["IceSSL.FindCert.CurrentUser.My"] = serverFindCertProperties[i];
                        //
                        // Use TrustOnly to ensure the peer has pick the expected certificate.
                        //
                        serverProperties["IceSSL.TrustOnly"] = "CN=Client";

                        IServerPrx? server = fact.createServer(serverProperties);
                        try
                        {
                            server!.IcePing();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.ToString());
                            TestHelper.Assert(false);
                        }
                        fact.destroyServer(server);
                        comm.Destroy();
                    }

                    //
                    // These must fail because the search criteria does not match any certificates.
                    //
                    foreach (string s in failFindCertProperties)
                    {
                        try
                        {
                            clientProperties = CreateProperties(defaultProperties);
                            clientProperties["IceSSL.FindCert"] = s;
                            Communicator comm = new Communicator(ref args, clientProperties);
                            TestHelper.Assert(false);
                        }
                        catch (System.Exception)
                        {
                            // Expected
                        }
                    }

                }
                finally
                {
                    foreach (string cert in certificates)
                    {
                        certStore.Remove(new X509Certificate2(defaultDir + cert, "password"));
                    }
                    certStore.Close();
                }

                //
                // These must fail because we have already remove the certificates.
                //
                foreach (string s in clientFindCertProperties)
                {
                    try
                    {
                        clientProperties = CreateProperties(defaultProperties);
                        clientProperties["IceSSL.FindCert.CurrentUser.My"] = s;
                        Communicator comm = new Communicator(ref args, clientProperties);
                        TestHelper.Assert(false);
                    }
                    catch (System.Exception)
                    {
                        // Expected
                    }
                }
            }
            Console.Out.WriteLine("ok");

            Console.Out.Write("testing system CAs... ");
            Console.Out.Flush();
            {
                //
                // Retry a few times in case there are connectivity problems with demo.zeroc.com.
                //
                const int retryMax = 5;
                const int retryDelay = 1000;
                int retryCount = 0;

                clientProperties = CreateProperties(defaultProperties);
                clientProperties["IceSSL.DefaultDir"] = "";
                clientProperties["IceSSL.VerifyDepthMax"] = "4";
                clientProperties["Ice.Override.Timeout"] = "5000"; // 5s timeout
                if (AssemblyUtil.IsWindows)
                {
                    //
                    // BUGFIX: SChannel TLS 1.2 bug that affects Windows versions prior to Windows 10
                    // can cause SSL handshake errors when connecting to the remote zeroc server.
                    //
                    clientProperties["IceSSL.Protocols"] = "TLS1_0,TLS1_1";
                }
                Communicator comm = new Communicator(clientProperties);
                var p = IObjectPrx.Parse("dummy:wss -p 443 -h zeroc.com -r /demo-proxy/chat/glacier2", comm);
                while (true)
                {
                    try
                    {
                        p.IcePing();
                        TestHelper.Assert(false);
                    }
                    catch (Ice.SecurityException)
                    {
                        // Expected, by default we don't check for system CAs.
                        break;
                    }
                    catch (System.Exception ex)
                    {
                        if ((ex is Ice.ConnectTimeoutException) ||
                           (ex is Ice.TransportException) ||
                           (ex is Ice.DNSException))
                        {
                            if (++retryCount < retryMax)
                            {
                                Console.Out.Write("retrying... ");
                                Console.Out.Flush();
                                Thread.Sleep(retryDelay);
                                continue;
                            }
                        }

                        Console.Out.WriteLine("warning: unable to connect to demo.zeroc.com to check system CA");
                        Console.WriteLine(ex.ToString());
                        break;
                    }
                }
                comm.Destroy();

                retryCount = 0;
                clientProperties = CreateProperties(defaultProperties);
                clientProperties["IceSSL.DefaultDir"] = "";
                clientProperties["IceSSL.VerifyDepthMax"] = "4";
                clientProperties["Ice.Override.Timeout"] = "5000"; // 5s timeout
                clientProperties["IceSSL.UsePlatformCAs"] = "1";
                if (AssemblyUtil.IsWindows)
                {
                    //
                    // BUGFIX: SChannel TLS 1.2 bug that affects Windows versions prior to Windows 10
                    // can cause SSL handshake errors when connecting to the remote zeroc server.
                    //
                    clientProperties["IceSSL.Protocols"] = "TLS1_0,TLS1_1";
                }
                comm = new Communicator(clientProperties);
                p = IObjectPrx.Parse("dummy:wss -p 443 -h zeroc.com -r /demo-proxy/chat/glacier2", comm);
                while (true)
                {
                    try
                    {
                        IceSSL.ConnectionInfo? info = (IceSSL.ConnectionInfo?)p.GetConnection().GetConnectionInfo().Underlying;
                        TestHelper.Assert(info != null);
                        TestHelper.Assert(info.Verified);
                        break;
                    }
                    catch (System.Exception ex)
                    {
                        if ((ex is Ice.ConnectTimeoutException) ||
                           (ex is Ice.TransportException) ||
                           (ex is Ice.DNSException))
                        {
                            if (++retryCount < retryMax)
                            {
                                Console.Out.Write("retrying... ");
                                Console.Out.Flush();
                                Thread.Sleep(retryDelay);
                                continue;
                            }
                        }

                        Console.Out.WriteLine("warning: unable to connect to demo.zeroc.com to check system CA");
                        Console.WriteLine(ex.ToString());
                        break;
                    }
                }
                comm.Destroy();
            }
            Console.Out.WriteLine("ok");
        }
        finally
        {
            if (AssemblyUtil.IsWindows && isAdministrator)
            {
                store.Remove(caCert1);
                store.Remove(caCert2);
            }
            store.Close();
        }

        return factory;
    }
}
