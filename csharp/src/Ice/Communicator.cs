//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using IceInternal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ice
{
    internal sealed class BufSizeWarnInfo
    {
        // Whether send size warning has been emitted
        public bool SndWarn;

        // The send size for which the warning was emitted
        public int SndSize;

        // Whether receive size warning has been emitted
        public bool RcvWarn;

        // The receive size for which the warning was emitted
        public int RcvSize;
    }

    public sealed partial class Communicator : IDisposable
    {
        private class ObserverUpdater : Instrumentation.IObserverUpdater
        {
            public ObserverUpdater(Communicator communicator) => _communicator = communicator;

            public void UpdateConnectionObservers() => _communicator.UpdateConnectionObservers();

            public void UpdateThreadObservers() => _communicator.UpdateThreadObservers();

            private readonly Communicator _communicator;
        }

         /// <summary>Indicates whether or not object adapters created by this communicator accept non-secure incoming
         /// connections. When false, they accept only secure connections; when true, they accept both secure and
         /// non-secure connections. This property corresponds to the Ice.AcceptNonSecureConnections configuration
         /// property. It can be overridden for each object adapter by the object adapter property with the same name.
         /// </summary>
         // TODO: update doc with default value for AcceptNonSecureConnections - it's currently true but should be
         // false.
         // TODO: currently only this property is implemented and nothing else.
        public bool AcceptNonSecureConnections { get; }

        /// <summary>Each time you send a request without an explicit context parameter, Ice sends automatically the
        /// per-thread CurrentContext combined with the proxy's context.</summary>
        public Dictionary<string, string> CurrentContext
        {
            get
            {
                try
                {
                    if (_currentContext.IsValueCreated)
                    {
                        Debug.Assert(_currentContext.Value != null);
                        return _currentContext.Value;
                    }
                    else
                    {
                        _currentContext.Value = new Dictionary<string, string>();
                        return _currentContext.Value;
                    }
                }
                catch (ObjectDisposedException ex)
                {
#pragma warning disable CA1065
                    throw new CommunicatorDestroyedException(ex);
#pragma warning restore CA1065
                }
            }
            set
            {
                try
                {
                    _currentContext.Value = value;
                }
                catch (ObjectDisposedException ex)
                {
                    throw new CommunicatorDestroyedException(ex);
                }
            }
        }

        public bool DefaultCollocationOptimized { get; }

        /// <summary>The default context for proxies created using this communicator.
        /// Changing the value of DefaultContext does not change the context of previously created proxies.</summary>
        public IReadOnlyDictionary<string, string> DefaultContext
        {
            get => _defaultContext;
            set => _defaultContext = value.Count == 0 ? Reference.EmptyContext : new Dictionary<string, string>(value);
        }

        public Encoding DefaultEncoding { get; }
        public EndpointSelectionType DefaultEndpointSelection { get; }
        public FormatType DefaultFormat { get; }
        public string? DefaultHost { get; }

        /// <summary>The default locator for this communicator. To disable the default locator, null can be used.
        /// All newly created proxies and object adapters will use this default locator.
        /// Note that setting this property has no effect on existing proxies or object adapters.
        ///
        /// You can also set a locator for an individual proxy by calling the operation ice_locator on the proxy,
        /// or for an object adapter by calling ObjectAdapter.setLocator on the object adapter.</summary>
        public ILocatorPrx? DefaultLocator
        {
            get => _defaultLocator;
            set => _defaultLocator = value;
        }

        public bool DefaultPreferNonSecure { get; }
        public IPAddress? DefaultSourceAddress { get; }
        public string DefaultTransport { get; }
        public int DefaultTimeout { get; }
        public int DefaultInvocationTimeout { get; }
        public int DefaultLocatorCacheTimeout { get; }

        /// <summary>The default router for this communicator. To disable the default router, null can be used.
        /// All newly created proxies will use this default router.
        /// Note that setting this property has no effect on existing proxies.
        ///
        /// You can also set a router for an individual proxy by calling the operation ice_router on the proxy.
        /// </summary>
        public IRouterPrx? DefaultRouter
        {
            get => _defaultRouter;
            set => _defaultRouter = value;
        }

        public bool? OverrideCompress { get; }
        public int? OverrideTimeout { get; }
        public int? OverrideCloseTimeout { get; }
        public int? OverrideConnectTimeout { get; }

        /// <summary>The logger for this communicator.</summary>
        public ILogger Logger { get; internal set; }

        public Instrumentation.ICommunicatorObserver? Observer { get; }

        /// <summary>Returns the TaskScheduler used to dispatch requests.</summary>
        public TaskScheduler? TaskScheduler { get; }

        public ToStringMode ToStringMode { get; }

        internal int ClassGraphDepthMax { get; }
        internal ACMConfig ClientACM { get; }
        internal int MessageSizeMax { get; }
        internal INetworkProxy? NetworkProxy { get; }
        internal bool PreferIPv6 { get; }
        internal int IPVersion { get; }
        internal ACMConfig ServerACM { get; }
        internal TraceLevels TraceLevels { get; private set; }

        private static string[] _emptyArgs = Array.Empty<string>();
        private static readonly string[] _suffixes =
        {
            "EndpointSelection",
            "ConnectionCached",
            "PreferNonSecure",
            "LocatorCacheTimeout",
            "InvocationTimeout",
            "Locator",
            "Router",
            "CollocationOptimized",
            "Context\\..*"
        };
        private static readonly object _staticLock = new object();

        private const int StateActive = 0;
        private const int StateDestroyInProgress = 1;
        private const int StateDestroyed = 2;

        private readonly HashSet<string> _adapterNamesInUse = new HashSet<string>();
        private readonly List<ObjectAdapter> _adapters = new List<ObjectAdapter>();
        private ObjectAdapter? _adminAdapter;
        private readonly bool _adminEnabled = false;
        private readonly HashSet<string> _adminFacetFilter = new HashSet<string>();
        private readonly Dictionary<string, IObject> _adminFacets = new Dictionary<string, IObject>();
        private Identity? _adminIdentity;
        private readonly ConcurrentDictionary<int, Type?> _compactIdCache = new ConcurrentDictionary<int, Type?>();
        private readonly string[] _compactIdNamespaces;
        private readonly ThreadLocal<Dictionary<string, string>> _currentContext
            = new ThreadLocal<Dictionary<string, string>>();
        private volatile IReadOnlyDictionary<string, string> _defaultContext = Reference.EmptyContext;
        private volatile ILocatorPrx? _defaultLocator;
        private volatile IRouterPrx? _defaultRouter;

        private bool _isShutdown = false;
        private static bool _oneOffDone = false;
        private readonly OutgoingConnectionFactory _outgoingConnectionFactory;
        private static bool _printProcessIdDone = false;
        private readonly int[] _retryIntervals;
        private readonly Dictionary<EndpointType, BufSizeWarnInfo> _setBufSizeWarn =
            new Dictionary<EndpointType, BufSizeWarnInfo>();
        private int _state;
        private readonly IceInternal.Timer _timer;
        private readonly ConcurrentDictionary<string, Type?> _typeIdCache = new ConcurrentDictionary<string, Type?>();
        private readonly string[] _typeIdNamespaces;

        public Communicator(Dictionary<string, string>? properties,
                            ILogger? logger = null,
                            Instrumentation.ICommunicatorObserver? observer = null,
                            TaskScheduler? taskScheduler = null,
                            string[]? typeIdNamespaces = null) :
            this(ref _emptyArgs,
                 null,
                 properties,
                 logger,
                 observer,
                 taskScheduler,
                 typeIdNamespaces)
        {
        }

        public Communicator(ref string[] args,
                            Dictionary<string, string>? properties,
                            ILogger? logger = null,
                            Instrumentation.ICommunicatorObserver? observer = null,
                            TaskScheduler? taskScheduler = null,
                            string[]? typeIdNamespaces = null) :
            this(ref args,
                 null,
                 properties,
                 logger,
                 observer,
                 taskScheduler,
                 typeIdNamespaces)
        {
        }

        public Communicator(NameValueCollection? appSettings = null,
                            Dictionary<string, string>? properties = null,
                            ILogger? logger = null,
                            Instrumentation.ICommunicatorObserver? observer = null,
                            TaskScheduler? taskScheduler = null,
                            string[]? typeIdNamespaces = null) :
            this(ref _emptyArgs,
                 appSettings,
                 properties,
                 logger,
                 observer,
                 taskScheduler,
                 typeIdNamespaces)
        {
        }

        public Communicator(ref string[] args,
                            NameValueCollection? appSettings,
                            Dictionary<string, string>? properties = null,
                            ILogger? logger = null,
                            Instrumentation.ICommunicatorObserver? observer = null,
                            TaskScheduler? taskScheduler = null,
                            string[]? typeIdNamespaces = null)
        {
            _state = StateActive;
            Logger = logger ?? Util.GetProcessLogger();
            Observer = observer;
            TaskScheduler = taskScheduler;
            _typeIdNamespaces = typeIdNamespaces ?? new string[] { "Ice.TypeId" };
            _compactIdNamespaces = new string[] { "IceCompactId" }.Concat(_typeIdNamespaces).ToArray();

            if (properties == null)
            {
                properties = new Dictionary<string, string>();
            }
            else
            {
                // clone properties as we don't want to modify the properties given to
                // this constructor
                properties = new Dictionary<string, string>(properties);
            }

            if (appSettings != null)
            {
                foreach (string key in appSettings.AllKeys)
                {
                    string[]? values = appSettings.GetValues(key);
                    if (values == null)
                    {
                        properties[key] = "";
                    }
                    else
                    {
                        // TODO: this join is not sufficient to create a string
                        // compatible with GetPropertyAsList
                        properties[key] = string.Join(",", values);
                    }
                }
            }

            if (!properties.ContainsKey("Ice.ProgramName"))
            {
                properties["Ice.ProgramName"] = AppDomain.CurrentDomain.FriendlyName;
            }

            properties.ParseIceArgs(ref args);
            SetProperties(properties);

            try
            {
                lock (_staticLock)
                {
                    if (!_oneOffDone)
                    {
                        string? stdOut = GetProperty("Ice.StdOut");

                        System.IO.StreamWriter? outStream = null;

                        if (stdOut != null)
                        {
                            outStream = System.IO.File.AppendText(stdOut);
                            outStream.AutoFlush = true;
                            Console.Out.Close();
                            Console.SetOut(outStream);
                        }

                        string? stdErr = GetProperty("Ice.StdErr");
                        if (stdErr != null)
                        {
                            if (stdErr.Equals(stdOut))
                            {
                                Console.SetError(outStream);
                            }
                            else
                            {
                                System.IO.StreamWriter errStream = System.IO.File.AppendText(stdErr);
                                errStream.AutoFlush = true;
                                Console.Error.Close();
                                Console.SetError(errStream);
                            }
                        }

                        _oneOffDone = true;
                    }
                }

                if (logger == null)
                {
                    string? logfile = GetProperty("Ice.LogFile");
                    string? programName = GetProperty("Ice.ProgramName");
                    Debug.Assert(programName != null);
                    if (logfile != null)
                    {
                        Logger = new FileLogger(programName, logfile);
                    }
                    else if (Util.GetProcessLogger() is Logger)
                    {
                        //
                        // Ice.ConsoleListener is enabled by default.
                        //
                        Logger = new TraceLogger(programName, (GetPropertyAsInt("Ice.ConsoleListener") ?? 1) > 0);
                    }
                    // else already set to process logger
                }

                TraceLevels = new TraceLevels(this);

                DefaultCollocationOptimized = (GetPropertyAsInt("Ice.Default.CollocationOptimized") ?? 1) > 0;

                if (GetProperty("Ice.Default.Encoding") is string encoding)
                {
                    try
                    {
                        DefaultEncoding = Encoding.Parse(encoding);
                        DefaultEncoding.CheckSupported();
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidConfigurationException(
                            $"invalid value for for Ice.Default.Encoding: `{encoding}'", ex);
                    }
                }
                else
                {
                    DefaultEncoding = Encoding.Latest;
                }

                var endpointSelection = GetProperty("Ice.Default.EndpointSelection") ?? "Random";
                DefaultEndpointSelection = endpointSelection switch
                {
                    "Random" => EndpointSelectionType.Random,
                    "Ordered" => EndpointSelectionType.Ordered,
                    _ => throw new InvalidConfigurationException(
                             $"illegal value `{endpointSelection}'; expected `Random' or `Ordered'")
                };

                DefaultFormat = (GetPropertyAsInt("Ice.Default.SlicedFormat") > 0) ? FormatType.Sliced :
                                                                                     FormatType.Compact;

                DefaultHost = GetProperty("Ice.Default.Host");

                // TODO: switch to 0 default
                DefaultPreferNonSecure = (GetPropertyAsInt("Ice.Default.PreferNonSecure") ?? 1) > 0;

                if (GetProperty("Ice.Default.SourceAddress") is string address)
                {
                    DefaultSourceAddress = Network.GetNumericAddress(address);
                    if (DefaultSourceAddress == null)
                    {
                        throw new InvalidConfigurationException(
                            $"invalid IP address set for Ice.Default.SourceAddress: `{address}'");
                    }
                }

                DefaultTransport = GetProperty("Ice.Default.Transport") ?? "tcp";

                DefaultTimeout = GetPropertyAsInt("Ice.Default.Timeout") ?? 60000;
                if (DefaultTimeout < 1 && DefaultTimeout != -1)
                {
                    throw new InvalidConfigurationException(
                        $"invalid value for Ice.Default.Timeout: `{DefaultTimeout}'");
                }

                DefaultInvocationTimeout = GetPropertyAsInt("Ice.Default.InvocationTimeout") ?? -1;
                if (DefaultInvocationTimeout < 1 && DefaultInvocationTimeout != -1)
                {
                    throw new InvalidConfigurationException(
                        $"invalid value for Ice.Default.InvocationTimeout: `{DefaultInvocationTimeout}'");
                }

                DefaultLocatorCacheTimeout = GetPropertyAsInt("Ice.Default.LocatorCacheTimeout") ?? -1;
                if (DefaultLocatorCacheTimeout < -1)
                {
                    throw new InvalidConfigurationException(
                        $"invalid value for Ice.Default.LocatorCacheTimeout: `{DefaultLocatorCacheTimeout}'");
                }

                if (GetPropertyAsInt("Ice.Override.Compress") is int compress)
                {
                    OverrideCompress = compress > 0;
                    if (!BZip2.IsLoaded && OverrideCompress.Value)
                    {
                        throw new InvalidConfigurationException($"compression not supported, bzip2 library not found");
                    }
                }
                else if (!BZip2.IsLoaded)
                {
                    OverrideCompress = false;
                }

                {
                    if (GetPropertyAsInt("Ice.Override.Timeout") is int timeout)
                    {
                        OverrideTimeout = timeout;
                        if (timeout < 1 && timeout != -1)
                        {
                            throw new InvalidConfigurationException(
                                $"invalid value for Ice.Override.Timeout: `{timeout}'");
                        }
                    }
                }

                {
                    if (GetPropertyAsInt("Ice.Override.CloseTimeout") is int timeout)
                    {
                        OverrideCloseTimeout = timeout;
                        if (timeout < 1 && timeout != -1)
                        {
                            throw new InvalidConfigurationException(
                                $"invalid value for Ice.Override.CloseTimeout: `{timeout}'");
                        }
                    }
                }

                {
                    if (GetPropertyAsInt("Ice.Override.ConnectTimeout") is int timeout)
                    {
                        OverrideConnectTimeout = timeout;
                        if (timeout < 1 && timeout != -1)
                        {
                            throw new InvalidConfigurationException(
                                $"invalid value for Ice.Override.ConnectTimeout: `{timeout}'");
                        }
                    }
                }

                ClientACM = new ACMConfig(this, Logger, "Ice.ACM.Client",
                                           new ACMConfig(this, Logger, "Ice.ACM", new ACMConfig(false)));

                ServerACM = new ACMConfig(this, Logger, "Ice.ACM.Server",
                                           new ACMConfig(this, Logger, "Ice.ACM", new ACMConfig(true)));

                {
                    int num = GetPropertyAsInt("Ice.MessageSizeMax") ?? 1024;
                    if (num < 1 || num > 0x7fffffff / 1024)
                    {
                        MessageSizeMax = 0x7fffffff;
                    }
                    else
                    {
                        MessageSizeMax = num * 1024; // Property is in kilobytes, MessageSizeMax in bytes
                    }
                }

                // TODO: switch to 0 default
                AcceptNonSecureConnections = (GetPropertyAsInt("Ice.AcceptNonSecureConnections") ?? 1) > 0;

                {
                    int num = GetPropertyAsInt("Ice.ClassGraphDepthMax") ?? 100;
                    if (num < 1 || num > 0x7fffffff)
                    {
                        ClassGraphDepthMax = 0x7fffffff;
                    }
                    else
                    {
                        ClassGraphDepthMax = num;
                    }
                }

                ToStringMode = Enum.Parse<ToStringMode>(GetProperty("Ice.ToStringMode") ?? "Unicode");

                _backgroundLocatorCacheUpdates = GetPropertyAsInt("Ice.BackgroundLocatorCacheUpdates") > 0;

                string[]? arr = GetPropertyAsList("Ice.RetryIntervals");

                if (arr == null)
                {
                    _retryIntervals = new int[] { 0 };
                }
                else
                {
                    _retryIntervals = new int[arr.Length];
                    for (int i = 0; i < arr.Length; i++)
                    {
                        int v = int.Parse(arr[i], CultureInfo.InvariantCulture);
                        //
                        // If -1 is the first value, no retry and wait intervals.
                        //
                        if (i == 0 && v == -1)
                        {
                            _retryIntervals = Array.Empty<int>();
                            break;
                        }

                        _retryIntervals[i] = v > 0 ? v : 0;
                    }
                }

                bool isIPv6Supported = Network.IsIPv6Supported();
                bool ipv4 = (GetPropertyAsInt("Ice.IPv4") ?? 1) > 0;
                bool ipv6 = (GetPropertyAsInt("Ice.IPv6") ?? (isIPv6Supported ? 1 : 0)) > 0;
                if (!ipv4 && !ipv6)
                {
                    throw new InvalidConfigurationException("Both IPV4 and IPv6 support cannot be disabled.");
                }
                else if (ipv4 && ipv6)
                {
                    IPVersion = Network.EnableBoth;
                }
                else if (ipv4)
                {
                    IPVersion = Network.EnableIPv4;
                }
                else
                {
                    IPVersion = Network.EnableIPv6;
                }
                PreferIPv6 = GetPropertyAsInt("Ice.PreferIPv6Address") > 0;

                NetworkProxy = CreateNetworkProxy(IPVersion);

                _endpointFactories = new List<IEndpointFactory>();
                AddEndpointFactory(new TcpEndpointFactory(new TransportInstance(this, EndpointType.TCP, "tcp", false)));
                AddEndpointFactory(new UdpEndpointFactory(new TransportInstance(this, EndpointType.UDP, "udp", false)));
                AddEndpointFactory(new WSEndpointFactory(new TransportInstance(this, EndpointType.WS, "ws", false), EndpointType.TCP));
                AddEndpointFactory(new WSEndpointFactory(new TransportInstance(this, EndpointType.WSS, "wss", true), EndpointType.SSL));

                _outgoingConnectionFactory = new OutgoingConnectionFactory(this);

                if (GetPropertyAsInt("Ice.PreloadAssemblies") > 0)
                {
                    AssemblyUtil.PreloadAssemblies();
                }

                //
                // Load plug-ins.
                //
                LoadPlugins(ref args);

                //
                // Initialize the endpoint factories once all the plugins are loaded. This gives
                // the opportunity for the endpoint factories to find underlying factories.
                //
                foreach (IEndpointFactory f in _endpointFactories)
                {
                    f.Initialize();
                }

                //
                // Create Admin facets, if enabled.
                //
                // Note that any logger-dependent admin facet must be created after we load all plugins,
                // since one of these plugins can be a Logger plugin that sets a new logger during loading
                //

                if (GetProperty("Ice.Admin.Enabled") == null)
                {
                    _adminEnabled = GetProperty("Ice.Admin.Endpoints") != null;
                }
                else
                {
                    _adminEnabled = GetPropertyAsInt("Ice.Admin.Enabled") > 0;
                }

                _adminFacetFilter = new HashSet<string>(
                    (GetPropertyAsList("Ice.Admin.Facets") ?? Array.Empty<string>()).Distinct());

                if (_adminEnabled)
                {
                    //
                    // Process facet
                    //
                    string processFacetName = "Process";
                    if (_adminFacetFilter.Count == 0 || _adminFacetFilter.Contains(processFacetName))
                    {
                        _adminFacets.Add(processFacetName, new Process(this));
                    }

                    //
                    // Logger facet
                    //
                    string loggerFacetName = "Logger";
                    if (_adminFacetFilter.Count == 0 || _adminFacetFilter.Contains(loggerFacetName))
                    {
                        ILoggerAdminLogger loggerAdminLogger = new LoggerAdminLogger(this, Logger);
                        Logger = loggerAdminLogger;
                        _adminFacets.Add(loggerFacetName, loggerAdminLogger.GetFacet());
                    }

                    //
                    // Properties facet
                    //
                    string propertiesFacetName = "Properties";
                    PropertiesAdmin? propsAdmin = null;
                    if (_adminFacetFilter.Count == 0 || _adminFacetFilter.Contains(propertiesFacetName))
                    {
                        propsAdmin = new PropertiesAdmin(this);
                        _adminFacets.Add(propertiesFacetName, propsAdmin);
                    }

                    //
                    // Metrics facet
                    //
                    string metricsFacetName = "Metrics";
                    if (_adminFacetFilter.Count == 0 || _adminFacetFilter.Contains(metricsFacetName))
                    {
                        var communicatorObserver = new CommunicatorObserverI(this, Logger);
                        Observer = communicatorObserver;
                        _adminFacets.Add(metricsFacetName, communicatorObserver.GetFacet());

                        // Make sure the admin plugin receives property updates.
                        if (propsAdmin != null)
                        {
                            propsAdmin.Updated += (_, updates) =>
                                communicatorObserver.GetFacet().Updated(updates);
                        }
                    }
                }

                //
                // Set observer updater
                //
                if (Observer != null)
                {
                    Observer.SetObserverUpdater(new ObserverUpdater(this));
                }

                //
                // Create threads.
                //
                try
                {
                    _timer = new IceInternal.Timer(this, IceInternal.Util.StringToThreadPriority(
                                                   GetProperty("Ice.ThreadPriority")));
                }
                catch (Exception ex)
                {
                    Logger.Error($"cannot create thread for timer:\n{ex}");
                    throw;
                }

                try
                {
                    _endpointHostResolverThread = new HelperThread(this);
                    UpdateEndpointHostResolverObserver();
                    _endpointHostResolverThread.Start(IceInternal.Util.StringToThreadPriority(GetProperty("Ice.ThreadPriority")));
                }
                catch (Exception ex)
                {
                    Logger.Error($"cannot create thread for endpoint host resolver:\n{ex}");
                    throw;
                }

                // The default router/locator may have been set during the loading of plugins.
                // Therefore we only set it if it hasn't already been set.
                try
                {
                    _defaultLocator ??= GetPropertyAsProxy("Ice.Default.Locator", ILocatorPrx.Factory);
                }
                catch (FormatException ex)
                {
                    throw new InvalidConfigurationException("invalid value for Ice.Default.Locator", ex);
                }

                try
                {
                    _defaultRouter ??= GetPropertyAsProxy("Ice.Default.Router", IRouterPrx.Factory);
                }
                catch (FormatException ex)
                {
                    throw new InvalidConfigurationException("invalid value for Ice.Default.Locator", ex);
                }

                //
                // Show process id if requested (but only once).
                //
                lock (this)
                {
                    if (!_printProcessIdDone && GetPropertyAsInt("Ice.PrintProcessId") > 0)
                    {
                        using var p = System.Diagnostics.Process.GetCurrentProcess();
                        Console.WriteLine(p.Id);
                        _printProcessIdDone = true;
                    }
                }

                //
                // An application can set Ice.InitPlugins=0 if it wants to postpone
                // initialization until after it has interacted directly with the
                // plug-ins.
                //
                if ((GetPropertyAsInt("Ice.InitPlugins") ?? 1) > 0)
                {
                    InitializePlugins();
                }

                //
                // This must be done last as this call creates the Ice.Admin object adapter
                // and eventually registers a process proxy with the Ice locator (allowing
                // remote clients to invoke on Ice.Admin facets as soon as it's registered).
                //
                if ((GetPropertyAsInt("Ice.Admin.DelayCreation") ?? 0) <= 0)
                {
                    GetAdmin();
                }
            }
            catch (Exception)
            {
                Destroy();
                throw;
            }
        }

        public void AddAdminFacet(string facet, IObject servant)
        {
            lock (this)
            {
                if (_state == StateDestroyed)
                {
                    throw new CommunicatorDestroyedException();
                }

                if (_adminFacetFilter.Count > 0 && !_adminFacetFilter.Contains(facet))
                {
                    throw new ArgumentException($"facet `{facet}' not allowed by Ice.Admin.Facets configuration",
                        nameof(facet));
                }

                if (_adminFacets.ContainsKey(facet))
                {
                    throw new ArgumentException($"facet `{facet}' is already registered", nameof(facet));
                }
                _adminFacets.Add(facet, servant);
                if (_adminAdapter != null)
                {
                    Debug.Assert(_adminIdentity != null);
                    _adminAdapter.Add(_adminIdentity.Value, facet, servant);
                }
            }
        }

        /// <summary>
        /// Add the Admin object with all its facets to the provided object adapter.
        /// If Ice.Admin.ServerId is set and the provided object adapter has a Locator,
        /// createAdmin registers the Admin's Process facet with the Locator's LocatorRegistry.
        ///
        /// createAdmin call only be called once; subsequent calls raise InvalidOperationException.
        ///
        /// </summary>
        /// <param name="adminAdapter">The object adapter used to host the Admin object; if null and
        /// Ice.Admin.Endpoints is set, create, activate and use the Ice.Admin object adapter.
        ///
        /// </param>
        /// <param name="adminIdentity">The identity of the Admin object.
        ///
        /// </param>
        /// <returns>A proxy to the main ("") facet of the Admin object. Never returns a null proxy.
        ///
        /// </returns>
        public IObjectPrx CreateAdmin(ObjectAdapter? adminAdapter, Identity adminIdentity)
        {
            lock (this)
            {
                if (_state == StateDestroyed)
                {
                    throw new CommunicatorDestroyedException();
                }

                if (_adminAdapter != null)
                {
                    throw new InvalidOperationException("Admin already created");
                }

                if (!_adminEnabled)
                {
                    throw new InvalidOperationException("Admin is disabled");
                }

                _adminIdentity = adminIdentity;
                if (adminAdapter == null)
                {
                    if (GetProperty("Ice.Admin.Endpoints") != null)
                    {
                        adminAdapter = CreateObjectAdapter("Ice.Admin");
                    }
                    else
                    {
                        throw new InvalidConfigurationException("Ice.Admin.Endpoints is not set");
                    }
                }
                else
                {
                    _adminAdapter = adminAdapter;
                }
                Debug.Assert(_adminAdapter != null);
                AddAllAdminFacets();
            }

            if (adminAdapter == null) // the parameter is null which means _adminAdapter needs to be activated
            {
                try
                {
                    _adminAdapter.Activate();
                }
                catch (Exception)
                {
                    // We cleanup _adminAdapter, however this error is not recoverable
                    // (can't call again getAdmin() after fixing the problem)
                    // since all the facets (servants) in the adapter are lost
                    _adminAdapter.Destroy();
                    lock (this)
                    {
                        _adminAdapter = null;
                    }
                    throw;
                }
            }
            SetServerProcessProxy(_adminAdapter, adminIdentity);
            return _adminAdapter.CreateProxy(adminIdentity, IObjectPrx.Factory);
        }

        public Reference CreateReference(string s, string? propertyPrefix = null)
        {
            const string delim = " \t\n\r";

            int beg;
            int end = 0;

            beg = IceUtilInternal.StringUtil.FindFirstNotOf(s, delim, end);
            if (beg == -1)
            {
                throw new FormatException($"no non-whitespace characters found in `{s}'");
            }

            //
            // Extract the identity, which may be enclosed in single
            // or double quotation marks.
            //
            string idstr;
            end = IceUtilInternal.StringUtil.CheckQuote(s, beg);
            if (end == -1)
            {
                throw new FormatException($"mismatched quotes around identity in `{s} '");
            }
            else if (end == 0)
            {
                end = IceUtilInternal.StringUtil.FindFirstOf(s, delim + ":@", beg);
                if (end == -1)
                {
                    end = s.Length;
                }
                idstr = s[beg..end];
            }
            else
            {
                beg++; // Skip leading quote
                idstr = s[beg..end];
                end++; // Skip trailing quote
            }

            if (beg == end)
            {
                throw new FormatException($"no identity in `{s}'");
            }

            //
            // Parsing the identity may raise FormatException.
            //
            var ident = Identity.Parse(idstr);

            string facet = "";
            InvocationMode mode = InvocationMode.Twoway;
            Encoding encoding = DefaultEncoding;
            Protocol protocol = Protocol.Ice1;
            string adapter;

            while (true)
            {
                beg = IceUtilInternal.StringUtil.FindFirstNotOf(s, delim, end);
                if (beg == -1)
                {
                    break;
                }

                if (s[beg] == ':' || s[beg] == '@')
                {
                    break;
                }

                end = IceUtilInternal.StringUtil.FindFirstOf(s, delim + ":@", beg);
                if (end == -1)
                {
                    end = s.Length;
                }

                if (beg == end)
                {
                    break;
                }

                string option = s[beg..end];
                if (option.Length != 2 || option[0] != '-')
                {
                    throw new FormatException("expected a proxy option but found `{option}' in `{s}'");
                }

                //
                // Check for the presence of an option argument. The
                // argument may be enclosed in single or double
                // quotation marks.
                //
                string? argument = null;
                int argumentBeg = IceUtilInternal.StringUtil.FindFirstNotOf(s, delim, end);
                if (argumentBeg != -1)
                {
                    char ch = s[argumentBeg];
                    if (ch != '@' && ch != ':' && ch != '-')
                    {
                        beg = argumentBeg;
                        end = IceUtilInternal.StringUtil.CheckQuote(s, beg);
                        if (end == -1)
                        {
                            throw new FormatException($"mismatched quotes around value for {option} option in `{s}'");
                        }
                        else if (end == 0)
                        {
                            end = IceUtilInternal.StringUtil.FindFirstOf(s, delim + ":@", beg);
                            if (end == -1)
                            {
                                end = s.Length;
                            }
                            argument = s[beg..end];
                        }
                        else
                        {
                            beg++; // Skip leading quote
                            argument = s[beg..end];
                            end++; // Skip trailing quote
                        }
                    }
                }

                //
                // If any new options are added here,
                // IceInternal::Reference::toString() and its derived classes must be updated as well.
                //
                switch (option[1])
                {
                    case 'f':
                        {
                            if (argument == null)
                            {
                                throw new FormatException($"no argument provided for -f option in `{s}'");
                            }

                            facet = IceUtilInternal.StringUtil.UnescapeString(argument, 0, argument.Length, "");
                            break;
                        }

                    case 't':
                        {
                            if (argument != null)
                            {
                                throw new FormatException(
                                    $"unexpected argument `{argument}' provided for -t option in `{s}'");
                            }
                            mode = InvocationMode.Twoway;
                            break;
                        }

                    case 'o':
                        {
                            if (argument != null)
                            {
                                throw new FormatException(
                                    $"unexpected argument `{argument}' provided for -o option in `{s}'");
                            }
                            mode = InvocationMode.Oneway;
                            break;
                        }

                    case 'O':
                        {
                            if (argument != null)
                            {
                                throw new FormatException(
                                    $"unexpected argument `{argument}' provided for -O option in `{s}'");
                            }
                            mode = InvocationMode.BatchOneway;
                            break;
                        }

                    case 'd':
                        {
                            if (argument != null)
                            {
                                throw new FormatException(
                                    $"unexpected argument `{argument}' provided for -d option in `{s}'");
                            }
                            mode = InvocationMode.Datagram;
                            break;
                        }

                    case 'D':
                        {
                            if (argument != null)
                            {
                                throw new FormatException(
                                    $"unexpected argument `{argument}' provided for -D option in `{s}'");
                            }
                            mode = InvocationMode.BatchDatagram;
                            break;
                        }

                    case 's':
                        {
                            if (argument != null)
                            {
                                throw new FormatException(
                                    $"unexpected argument `{argument}' provided for -s option in `{s}'");
                            }
                            Logger.Warning($"while parsing `{s}': the `-s' proxy option no longer has any effect");
                            break;
                        }

                    case 'e':
                        {
                            if (argument == null)
                            {
                                throw new FormatException($"no argument provided for -e option in `{s}'");
                            }

                            encoding = Encoding.Parse(argument);
                            break;
                        }

                    case 'p':
                        {
                            if (argument == null)
                            {
                                throw new FormatException($"no argument provided for -p option `{s}'");
                            }

                            protocol = ProtocolExtensions.Parse(argument);
                            break;
                        }

                    default:
                        {
                            throw new FormatException("unknown option `{option}' in `{s}'");
                        }
                }
            }

            if (beg == -1)
            {
                return CreateReference(ident, facet, mode, protocol, encoding, Array.Empty<Endpoint>(),
                    null, propertyPrefix);
            }

            var endpoints = new List<Endpoint>();

            if (s[beg] == ':')
            {
                var unknownEndpoints = new List<string>();
                end = beg;

                while (end < s.Length && s[end] == ':')
                {
                    beg = end + 1;

                    end = beg;
                    while (true)
                    {
                        end = s.IndexOf(':', end);
                        if (end == -1)
                        {
                            end = s.Length;
                            break;
                        }
                        else
                        {
                            bool quoted = false;
                            int quote = beg;
                            while (true)
                            {
                                quote = s.IndexOf('\"', quote);
                                if (quote == -1 || end < quote)
                                {
                                    break;
                                }
                                else
                                {
                                    quote = s.IndexOf('\"', ++quote);
                                    if (quote == -1)
                                    {
                                        break;
                                    }
                                    else if (end < quote)
                                    {
                                        quoted = true;
                                        break;
                                    }
                                    ++quote;
                                }
                            }
                            if (!quoted)
                            {
                                break;
                            }
                            ++end;
                        }
                    }

                    string es = s[beg..end];
                    Endpoint? endp = CreateEndpoint(es, false);
                    if (endp != null)
                    {
                        endpoints.Add(endp);
                    }
                    else
                    {
                        unknownEndpoints.Add(es);
                    }
                }

                if (endpoints.Count == 0)
                {
                    Debug.Assert(unknownEndpoints.Count > 0);
                    throw new FormatException($"invalid endpoint `{unknownEndpoints[0]}' in `{s}'");
                }
                else if (unknownEndpoints.Count != 0 && (GetPropertyAsInt("Ice.Warn.Endpoints") ?? 1) > 0)
                {
                    var msg = new StringBuilder("Proxy contains unknown endpoints:");
                    int sz = unknownEndpoints.Count;
                    for (int idx = 0; idx < sz; ++idx)
                    {
                        msg.Append(" `");
                        msg.Append(unknownEndpoints[idx]);
                        msg.Append("'");
                    }
                    Logger.Warning(msg.ToString());
                }
                return CreateReference(ident, facet, mode, protocol, encoding, endpoints, null, propertyPrefix);
            }
            else if (s[beg] == '@')
            {
                beg = IceUtilInternal.StringUtil.FindFirstNotOf(s, delim, beg + 1);
                if (beg == -1)
                {
                    throw new FormatException($"missing adapter id in `{s}'");
                }

                string adapterstr;
                end = IceUtilInternal.StringUtil.CheckQuote(s, beg);
                if (end == -1)
                {
                    throw new FormatException($"mismatched quotes around adapter id in `{s}'");
                }
                else if (end == 0)
                {
                    end = IceUtilInternal.StringUtil.FindFirstOf(s, delim, beg);
                    if (end == -1)
                    {
                        end = s.Length;
                    }
                    adapterstr = s[beg..end];
                }
                else
                {
                    beg++; // Skip leading quote
                    adapterstr = s[beg..end];
                    end++; // Skip trailing quote
                }

                if (end != s.Length && IceUtilInternal.StringUtil.FindFirstNotOf(s, delim, end) != -1)
                {
                    throw new FormatException(
                        $"invalid trailing characters after `{s.Substring(0, end + 1)}' in `{s}'");
                }

                adapter = IceUtilInternal.StringUtil.UnescapeString(adapterstr, 0, adapterstr.Length, "");

                if (adapter.Length == 0)
                {
                    throw new FormatException($"empty adapter id in `{s}'");
                }
                return CreateReference(ident, facet, mode, protocol, encoding, Array.Empty<Endpoint>(),
                    adapter, propertyPrefix);
            }

            throw new FormatException($"malformed proxy `{s}'");
        }

        public Reference CreateReference(Identity ident, InputStream istr)
        {
            //
            // Don't read the identity here. Operations calling this
            // constructor read the identity, and pass it as a parameter.
            //

            //
            // For compatibility with the old FacetPath.
            //
            string[] facetPath = istr.ReadStringArray();
            string facet;
            if (facetPath.Length > 0)
            {
                if (facetPath.Length > 1)
                {
                    throw new InvalidDataException($"invalid facet path length: {facetPath.Length}");
                }
                facet = facetPath[0];
            }
            else
            {
                facet = "";
            }

            int mode = istr.ReadByte();
            if (mode < 0 || mode > (int)InvocationMode.Last)
            {
                throw new InvalidDataException($"invalid invocation mode: {mode}");
            }

            istr.ReadBool(); // secure option, ignored

            byte major = istr.ReadByte();
            byte minor = istr.ReadByte();
            if (minor != 0)
            {
                throw new InvalidDataException($"received proxy with protocol set to {major}.{minor}");
            }
            var protocol = (Protocol)major;

            major = istr.ReadByte();
            minor = istr.ReadByte();
            var encoding = new Encoding(major, minor);

            Endpoint[] endpoints;
            string adapterId = "";

            int sz = istr.ReadSize();
            if (sz > 0)
            {
                endpoints = new Endpoint[sz];
                for (int i = 0; i < sz; i++)
                {
                    endpoints[i] = istr.ReadEndpoint();
                }
            }
            else
            {
                endpoints = Array.Empty<Endpoint>();
                adapterId = istr.ReadString();
            }

            return CreateReference(ident, facet, (InvocationMode)mode, protocol, encoding, endpoints, adapterId,
                                   null);
        }

        /// <summary>
        /// Destroy the communicator.
        /// This operation calls shutdown
        /// implicitly.  Calling destroy cleans up memory, and shuts down
        /// this communicator's client functionality and destroys all object
        /// adapters. Subsequent calls to destroy are ignored.
        /// </summary>
        public void Destroy()
        {
            lock (this)
            {
                //
                // If destroy is in progress, wait for it to be done. This
                // is necessary in case destroy() is called concurrently
                // by multiple threads.
                //
                while (_state == StateDestroyInProgress)
                {
                    Monitor.Wait(this);
                }

                if (_state == StateDestroyed)
                {
                    return;
                }
                _state = StateDestroyInProgress;
            }

            //
            // Shutdown and destroy all the incoming and outgoing Ice
            // connections and wait for the connections to be finished.
            //
            Shutdown();
            _outgoingConnectionFactory.Destroy();

            //
            // First wait for shutdown to finish.
            //
            WaitForShutdown();

            DestroyAllObjectAdapters();

            _outgoingConnectionFactory.WaitUntilFinished();

            DestroyRetryQueue(); // Must be called before destroying thread pools.

            if (Observer != null)
            {
                Observer.SetObserverUpdater(null);
            }

            if (Logger is ILoggerAdminLogger)
            {
                ((ILoggerAdminLogger)Logger).Destroy();
            }

            lock (_endpointHostResolverThread)
            {
                Debug.Assert(!_endpointHostResolverDestroyed);
                _endpointHostResolverDestroyed = true;
                Monitor.Pulse(_endpointHostResolverThread);
            }

            //
            // Wait for all the threads to be finished.
            //
            _timer.Destroy();

            _endpointHostResolverThread.Join();

            lock (_routerInfoTable)
            {
                foreach (RouterInfo i in _routerInfoTable.Values)
                {
                    i.Destroy();
                }
                _routerInfoTable.Clear();
            }

            lock (_locatorInfoMap)
            {
                foreach (LocatorInfo info in _locatorInfoMap.Values)
                {
                    info.Destroy();
                }
                _locatorInfoMap.Clear();
                _locatorTableMap.Clear();
            }

            foreach (IEndpointFactory f in _endpointFactories)
            {
                f.Destroy();
            }
            _endpointFactories.Clear();

            if (GetPropertyAsInt("Ice.Warn.UnusedProperties") > 0)
            {
                List<string> unusedProperties = GetUnusedProperties();
                if (unusedProperties.Count != 0)
                {
                    var message = new StringBuilder("The following properties were set but never read:");
                    foreach (string s in unusedProperties)
                    {
                        message.Append("\n    ");
                        message.Append(s);
                    }
                    Logger.Warning(message.ToString());
                }
            }

            //
            // Destroy last so that a Logger plugin can receive all log/traces before its destruction.
            //
            List<(string Name, IPlugin Plugin)> plugins;
            lock (this)
            {
                plugins = new List<(string Name, IPlugin Plugin)>(_plugins);
            }
            plugins.Reverse();
            foreach ((string Name, IPlugin Plugin) in plugins)
            {
                try
                {
                    Plugin.Destroy();
                }
                catch (Exception ex)
                {
                    Util.GetProcessLogger().Warning(
                        $"unexpected exception raised by plug-in `{Name}' destruction:\n{ex}");
                }
            }

            lock (this)
            {
                _adminAdapter = null;
                _adminFacets.Clear();

                _state = StateDestroyed;
                Monitor.PulseAll(this);
            }

            {
                if (Logger != null && Logger is FileLogger)
                {
                    ((FileLogger)Logger).Destroy();
                }
            }
            _currentContext.Dispose();
        }

        public void Dispose() => Destroy();

        /// <summary>
        /// Returns a facet of the Admin object.
        /// </summary>
        /// <param name="facet">The name of the Admin facet.
        /// </param>
        /// <returns>The servant associated with this Admin facet, or
        /// null if no facet is registered with the given name.</returns>
        public IObject? FindAdminFacet(string facet)
        {
            lock (this)
            {
                if (_state == StateDestroyed)
                {
                    throw new CommunicatorDestroyedException();
                }

                if (!_adminFacets.TryGetValue(facet, out IObject? result))
                {
                    return null;
                }
                return result;
            }
        }

        /// <summary>
        /// Returns a map of all facets of the Admin object.
        /// </summary>
        /// <returns>A collection containing all the facet names and
        /// servants of the Admin object.
        ///
        /// </returns>
        public Dictionary<string, IObject> FindAllAdminFacets()
        {
            lock (this)
            {
                if (_state == StateDestroyed)
                {
                    throw new CommunicatorDestroyedException();
                }
                return new Dictionary<string, IObject>(_adminFacets); // TODO, return a read-only collection
            }
        }

        /// <summary>
        /// Get a proxy to the main facet of the Admin object.
        /// GetAdmin also creates the Admin object and creates and activates the Ice.Admin object
        /// adapter to host this Admin object if Ice.Admin.Enpoints is set. The identity of the Admin
        /// object created by getAdmin is {value of Ice.Admin.InstanceName}/admin, or {UUID}/admin
        /// when Ice.Admin.InstanceName is not set.
        ///
        /// If Ice.Admin.DelayCreation is 0 or not set, getAdmin is called by the communicator
        /// initialization, after initialization of all plugins.
        ///
        /// </summary>
        /// <returns>A proxy to the main ("") facet of the Admin object, or a null proxy if no
        /// Admin object is configured.</returns>
        public IObjectPrx? GetAdmin()
        {
            ObjectAdapter adminAdapter;
            Identity adminIdentity;

            lock (this)
            {
                if (_state == StateDestroyed)
                {
                    throw new CommunicatorDestroyedException();
                }

                if (_adminAdapter != null)
                {
                    Debug.Assert(_adminIdentity != null);
                    return _adminAdapter.CreateProxy(_adminIdentity.Value, IObjectPrx.Factory);
                }
                else if (_adminEnabled)
                {
                    if (GetProperty("Ice.Admin.Endpoints") != null)
                    {
                        adminAdapter = CreateObjectAdapter("Ice.Admin");
                    }
                    else
                    {
                        return null;
                    }
                    adminIdentity = new Identity("admin", GetProperty("Ice.Admin.InstanceName") ?? "");
                    if (adminIdentity.Category.Length == 0)
                    {
                        adminIdentity = new Identity(adminIdentity.Name, Guid.NewGuid().ToString());
                    }

                    _adminIdentity = adminIdentity;
                    _adminAdapter = adminAdapter;
                    AddAllAdminFacets();
                    // continue below outside synchronization
                }
                else
                {
                    return null;
                }
            }

            try
            {
                adminAdapter.Activate();
            }
            catch (Exception)
            {
                // We cleanup _adminAdapter, however this error is not recoverable
                // (can't call again getAdmin() after fixing the problem)
                // since all the facets (servants) in the adapter are lost
                adminAdapter.Destroy();
                lock (this)
                {
                    _adminAdapter = null;
                }
                throw;
            }

            SetServerProcessProxy(adminAdapter, adminIdentity);
            return adminAdapter.CreateProxy(adminIdentity, IObjectPrx.Factory);
        }

        /// <summary>
        /// Check whether the communicator has been shut down.
        /// </summary>
        /// <returns>True if the communicator has been shut down; false otherwise.</returns>
        public bool IsShutdown()
        {
            lock (this)
            {
                return _isShutdown;
            }
        }

        /// <summary>Removes an admin facet servant previously added with AddAdminFacet.</summary>
        /// <param name="facet">The Admin facet.</param>
        /// <returns>The admin facet servant that was just removed, or null if the facet was not found.</returns>
        public IObject? RemoveAdminFacet(string facet)
        {
            lock (this)
            {
                if (_adminFacets.TryGetValue(facet, out IObject? result))
                {
                    _adminFacets.Remove(facet);
                }
                else
                {
                    return null;
                }
                if (_adminAdapter != null)
                {
                    Debug.Assert(_adminIdentity != null);
                    _adminAdapter.Remove(_adminIdentity.Value, facet);
                }
                return result;
            }
        }

        public IceInternal.Timer Timer()
        {
            lock (this)
            {
                if (_state == StateDestroyed)
                {
                    throw new CommunicatorDestroyedException();
                }
                return _timer;
            }
        }

        internal int CheckRetryAfterException(System.Exception ex, Reference reference, ref int cnt)
        {
            ILogger logger = Logger;

            if (reference.InvocationMode == InvocationMode.BatchOneway ||
                reference.InvocationMode == InvocationMode.BatchDatagram)
            {
                Debug.Assert(false); // batch no longer implemented anyway
                throw ex;
            }

            //
            // If it's a fixed proxy, retrying isn't useful as the proxy is tied to
            // the connection and the request will fail with the exception.
            //
            if (reference.IsFixed)
            {
                throw ex;
            }

            if (ex is ObjectNotExistException one)
            {
                RouterInfo? ri = reference.RouterInfo;
                if (ri != null && one.Operation.Equals("ice_add_proxy"))
                {
                    //
                    // If we have a router, an ObjectNotExistException with an
                    // operation name "ice_add_proxy" indicates to the client
                    // that the router isn't aware of the proxy (for example,
                    // because it was evicted by the router). In this case, we
                    // must *always* retry, so that the missing proxy is added
                    // to the router.
                    //

                    ri.ClearCache(reference);

                    if (TraceLevels.Retry >= 1)
                    {
                        logger.Trace(TraceLevels.RetryCat, $"retrying operation call to add proxy to router\n {ex}");
                    }
                    return 0; // We must always retry, so we don't look at the retry count.
                }
                else if (reference.IsIndirect)
                {
                    //
                    // We retry ObjectNotExistException if the reference is
                    // indirect.
                    //

                    if (reference.IsWellKnown)
                    {
                        reference.LocatorInfo?.ClearCache(reference);
                    }
                }
                else
                {
                    //
                    // For all other cases, we don't retry ObjectNotExistException.
                    //
                    throw ex;
                }
            }

            //
            // Don't retry if the communicator is destroyed, object adapter is deactivated,
            // or connection is manually closed.
            //
            if (ex is CommunicatorDestroyedException ||
                ex is ObjectAdapterDeactivatedException ||
                ex is ConnectionClosedLocallyException)
            {
                throw ex;
            }

            //
            // Don't retry on timeout and operation canceled exceptions.
            //
            if (ex is TimeoutException || ex is OperationCanceledException)
            {
                throw ex;
            }

            ++cnt;
            Debug.Assert(cnt > 0);

            int interval;
            if (cnt == (_retryIntervals.Length + 1) && ex is Ice.ConnectionClosedByPeerException)
            {
                //
                // A connection closed exception is always retried at least once, even if the retry
                // limit is reached.
                //
                interval = 0;
            }
            else if (cnt > _retryIntervals.Length)
            {
                if (TraceLevels.Retry >= 1)
                {
                    string s = "cannot retry operation call because retry limit has been exceeded\n" + ex;
                    logger.Trace(TraceLevels.RetryCat, s);
                }
                throw ex;
            }
            else
            {
                interval = _retryIntervals[cnt - 1];
            }

            if (TraceLevels.Retry >= 1)
            {
                string s = "retrying operation call";
                if (interval > 0)
                {
                    s += " in " + interval + "ms";
                }
                s += " because of exception\n" + ex;
                logger.Trace(TraceLevels.RetryCat, s);
            }

            return interval;
        }

        internal Reference CreateReference(Identity ident, string facet, Reference tmpl,
            IReadOnlyList<Endpoint> endpoints)
        {
            return CreateReference(ident, facet, tmpl.InvocationMode, tmpl.Protocol, tmpl.Encoding,
                          endpoints, null, null);
        }

        internal Reference CreateReference(Identity ident, string facet, Reference tmpl, string adapterId)
        {
            //
            // Create new reference
            //
            return CreateReference(ident, facet, tmpl.InvocationMode, tmpl.Protocol, tmpl.Encoding,
                          Array.Empty<Endpoint>(), adapterId, null);
        }

        internal Reference CreateReference(Identity identity, Connection connection)
        {
            // Fixed reference
            return new Reference(
                communicator: this,
                compress: null,
                context: DefaultContext,
                encoding: DefaultEncoding,
                facet: "",
                fixedConnection: connection,
                identity: identity,
                invocationMode: connection.Endpoint.IsDatagram ? InvocationMode.Datagram : InvocationMode.Twoway,
                invocationTimeout: -1);
        }

        internal BufSizeWarnInfo GetBufSizeWarn(EndpointType type)
        {
            lock (_setBufSizeWarn)
            {
                BufSizeWarnInfo info;
                if (!_setBufSizeWarn.ContainsKey(type))
                {
                    info = new BufSizeWarnInfo();
                    info.SndWarn = false;
                    info.SndSize = -1;
                    info.RcvWarn = false;
                    info.RcvSize = -1;
                    _setBufSizeWarn.Add(type, info);
                }
                else
                {
                    info = _setBufSizeWarn[type];
                }
                return info;
            }
        }

        internal OutgoingConnectionFactory OutgoingConnectionFactory()
        {
            lock (this)
            {
                if (_state == StateDestroyed)
                {
                    throw new CommunicatorDestroyedException();
                }
                return _outgoingConnectionFactory;
            }
        }

        // Return the C# class associated with this Slice type-id
        // Used for both Slice classes and exceptions
        internal Type? ResolveClass(string typeId)
        {
            return _typeIdCache.GetOrAdd(typeId, typeId =>
            {
                // First attempt corresponds to no cs:namespace metadata in the
                // enclosing top-level module
                string className = TypeIdToClassName(typeId);
                Type? classType = AssemblyUtil.FindType(className);

                // If this fails, look for helper classes in the typeIdNamespaces namespace(s)
                if (classType == null)
                {
                    foreach (string ns in _typeIdNamespaces)
                    {
                        Type? helper = AssemblyUtil.FindType($"{ns}.{className}");
                        if (helper != null)
                        {
                            try
                            {
                                classType = helper.GetProperty("targetClass")!.PropertyType;
                                break; // foreach
                            }
                            catch (Exception)
                            {
                            }
                        }
                    }
                }

                // Ensure the class can be instantiate.
                if (classType != null && !classType.IsAbstract && !classType.IsInterface)
                {
                    return classType;
                }

                return null;
            });
        }

        internal Type? ResolveCompactId(int compactId)
        {
            return _compactIdCache.GetOrAdd(compactId, compactId =>
            {
                foreach (string ns in _compactIdNamespaces)
                {
                    try
                    {
                        Type? classType = AssemblyUtil.FindType($"{ns}.TypeId_{compactId}");
                        if (classType != null)
                        {
                            string? result = (string?)classType.GetField("typeId")!.GetValue(null);
                            if (result != null)
                            {
                                return ResolveClass(result);
                            }
                            return null;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

                return null;
            });
        }

        internal void SetRcvBufSizeWarn(EndpointType type, int size)
        {
            lock (_setBufSizeWarn)
            {
                BufSizeWarnInfo info = GetBufSizeWarn(type);
                info.RcvWarn = true;
                info.RcvSize = size;
                _setBufSizeWarn[type] = info;
            }
        }

        internal void SetServerProcessProxy(ObjectAdapter adminAdapter, Identity adminIdentity)
        {
            IObjectPrx? admin = adminAdapter.CreateProxy(adminIdentity, IObjectPrx.Factory);
            ILocatorPrx? locator = adminAdapter.Locator;
            string? serverId = GetProperty("Ice.Admin.ServerId");

            if (locator != null && serverId != null)
            {
                IProcessPrx process = admin.Clone(facet: "Process", factory: IProcessPrx.Factory);
                try
                {
                    //
                    // Note that as soon as the process proxy is registered, the communicator might be
                    // shutdown by a remote client and admin facets might start receiving calls.
                    //
                    locator.GetRegistry()!.SetServerProcessProxy(serverId, process);
                }
                catch (Exception ex)
                {
                    if (TraceLevels.Location >= 1)
                    {
                        Logger.Trace(TraceLevels.LocationCat,
                            $"could not register server `{serverId}' with the locator registry:\n{ex}");
                    }
                    throw;
                }

                if (TraceLevels.Location >= 1)
                {
                    Logger.Trace(TraceLevels.LocationCat, $"registered server `{serverId}' with the locator registry");
                }
            }
        }

        internal void SetSndBufSizeWarn(EndpointType type, int size)
        {
            lock (_setBufSizeWarn)
            {
                BufSizeWarnInfo info = GetBufSizeWarn(type);
                info.SndWarn = true;
                info.SndSize = size;
                _setBufSizeWarn[type] = info;
            }
        }

        internal void UpdateConnectionObservers()
        {
            try
            {
                _outgoingConnectionFactory.UpdateConnectionObservers();

                ObjectAdapter[] adapters;
                lock (this)
                {
                    adapters = _adapters.ToArray();
                }

                foreach (ObjectAdapter adapter in adapters)
                {
                    adapter.UpdateConnectionObservers();
                }
            }
            catch (CommunicatorDestroyedException)
            {
            }
        }

        internal void UpdateThreadObservers()
        {
            try
            {
                UpdateEndpointHostResolverObserver();

                Debug.Assert(Observer != null);
                _timer.UpdateObserver(Observer);
            }
            catch (CommunicatorDestroyedException)
            {
            }
        }

        private static string TypeIdToClassName(string typeId)
        {
            if (!typeId.StartsWith("::", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"`{typeId}' is not a valid Ice type ID");
            }
            return typeId.Substring(2).Replace("::", ".");
        }

        private void AddAllAdminFacets()
        {
            lock (this)
            {
                Debug.Assert(_adminAdapter != null);
                foreach (KeyValuePair<string, IObject> entry in _adminFacets)
                {
                    if (_adminFacetFilter.Count == 0 || _adminFacetFilter.Contains(entry.Key))
                    {
                        Debug.Assert(_adminIdentity != null);
                        _adminAdapter.Add(_adminIdentity.Value, entry.Key, entry.Value);
                    }
                }
            }
        }

        private void CheckForUnknownProperties(string prefix)
        {
            //
            // Do not warn about unknown properties if Ice prefix, ie Ice, Glacier2, etc
            //
            foreach (string name in PropertyNames.clPropNames)
            {
                if (prefix.StartsWith(string.Format("{0}.", name), StringComparison.Ordinal))
                {
                    return;
                }
            }

            var unknownProps = new List<string>();
            Dictionary<string, string> props = GetProperties(forPrefix: $"{prefix}.");
            foreach (string prop in props.Keys)
            {
                bool valid = false;
                for (int i = 0; i < _suffixes.Length; ++i)
                {
                    string pattern = "^" + Regex.Escape(prefix + ".") + _suffixes[i] + "$";
                    if (new Regex(pattern).Match(prop).Success)
                    {
                        valid = true;
                        break;
                    }
                }

                if (!valid)
                {
                    unknownProps.Add(prop);
                }
            }

            if (unknownProps.Count != 0)
            {
                var message = new StringBuilder("found unknown properties for proxy '");
                message.Append(prefix);
                message.Append("':");
                foreach (string s in unknownProps)
                {
                    message.Append("\n    ");
                    message.Append(s);
                }
                Logger.Warning(message.ToString());
            }
        }

        private INetworkProxy? CreateNetworkProxy(int protocolSupport)
        {
            string? proxyHost = GetProperty("Ice.SOCKSProxyHost");
            if (proxyHost != null)
            {
                if (protocolSupport == Network.EnableIPv6)
                {
                    throw new InvalidConfigurationException("IPv6 only is not supported with SOCKS4 proxies");
                }
                return new SOCKSNetworkProxy(proxyHost, GetPropertyAsInt("Ice.SOCKSProxyPort") ?? 1080);
            }

            proxyHost = GetProperty("Ice.HTTPProxyHost");
            if (proxyHost != null)
            {
                return new HTTPNetworkProxy(proxyHost, GetPropertyAsInt("Ice.HTTPProxyPort") ?? 1080);
            }

            return null;
        }

        private Reference CreateReference(
            Identity ident,
            string facet,
            InvocationMode mode,
            Protocol protocol,
            Encoding encoding,
            IReadOnlyList<Endpoint> endpoints,
            string? adapterId,
            string? propertyPrefix)
        {
            lock (this)
            {
                if (_state == StateDestroyed)
                {
                    throw new CommunicatorDestroyedException();
                }
            }

            //
            // Default local proxy options.
            //
            LocatorInfo? locatorInfo = null;
            if (_defaultLocator != null)
            {
                if (!_defaultLocator.IceReference.Encoding.Equals(encoding))
                {
                    locatorInfo = GetLocatorInfo(_defaultLocator.Clone(encoding: encoding));
                }
                else
                {
                    locatorInfo = GetLocatorInfo(_defaultLocator);
                }
            }
            RouterInfo? routerInfo = null;
            if (_defaultRouter != null)
            {
                routerInfo = GetRouterInfo(_defaultRouter);
            }
            bool collocOptimized = DefaultCollocationOptimized;
            bool cacheConnection = true;
            bool preferNonSecure = DefaultPreferNonSecure;
            EndpointSelectionType endpointSelection = DefaultEndpointSelection;
            int locatorCacheTimeout = DefaultLocatorCacheTimeout;
            int invocationTimeout = DefaultInvocationTimeout;
            IReadOnlyDictionary<string, string>? context = null;

            //
            // Override the defaults with the proxy properties if a property prefix is defined.
            //
            if (propertyPrefix != null && propertyPrefix.Length > 0)
            {
                //
                // Warn about unknown properties.
                //
                if ((GetPropertyAsInt("Ice.Warn.UnknownProperties") ?? 1) > 0)
                {
                    CheckForUnknownProperties(propertyPrefix);
                }

                string property = $"{propertyPrefix}.Locator";
                ILocatorPrx? locator = GetPropertyAsProxy(property, ILocatorPrx.Factory);
                if (locator != null)
                {
                    if (!locator.IceReference.Encoding.Equals(encoding))
                    {
                        locatorInfo = GetLocatorInfo(locator.Clone(encoding: encoding));
                    }
                    else
                    {
                        locatorInfo = GetLocatorInfo(locator);
                    }
                }

                property = $"{propertyPrefix}.Router";
                IRouterPrx? router = GetPropertyAsProxy(property, IRouterPrx.Factory);
                if (router != null)
                {
                    if (propertyPrefix.EndsWith(".Router", StringComparison.Ordinal))
                    {
                        Logger.Warning($"`{property}={GetProperty(property)}': cannot set a router on a router; setting ignored");
                    }
                    else
                    {
                        routerInfo = GetRouterInfo(router);
                    }
                }

                property = $"{propertyPrefix}.CollocationOptimized";
                collocOptimized = (GetPropertyAsInt(property) ?? (collocOptimized ? 1 : 0)) > 0;

                property = $"{propertyPrefix}.ConnectionCached";
                cacheConnection = (GetPropertyAsInt(property) ?? (cacheConnection ? 1 : 0)) > 0;

                property = $"{propertyPrefix}.PreferNonSecure";
                preferNonSecure = (GetPropertyAsInt(property) ?? (preferNonSecure ? 1 : 0)) > 0;

                property = propertyPrefix + ".EndpointSelection";
                string? val = GetProperty(property);
                if (val != null)
                {
                    endpointSelection = Enum.Parse<EndpointSelectionType>(val);
                }

                property = $"{propertyPrefix}.LocatorCacheTimeout";
                val = GetProperty(property);
                if (val != null)
                {
                    locatorCacheTimeout = GetPropertyAsInt(property) ?? locatorCacheTimeout;
                    if (locatorCacheTimeout < -1)
                    {
                        locatorCacheTimeout = -1;
                        Logger.Warning($"invalid value for {property} `{val}': defaulting to -1");
                    }
                }

                property = $"{propertyPrefix}.InvocationTimeout";
                val = GetProperty(property);
                if (val != null)
                {
                    invocationTimeout = GetPropertyAsInt(property) ?? invocationTimeout;
                    if (invocationTimeout < 1 && invocationTimeout != -1)
                    {
                        invocationTimeout = -1;
                        Logger.Warning($"invalid value for {property} `{val}': defaulting to -1");
                    }
                }

                property = $"{propertyPrefix}.Context.";
                context = GetProperties(forPrefix: property).ToDictionary(e => e.Key.Substring(property.Length),
                                                                          e => e.Value);
            }

            if (context == null || context.Count == 0)
            {
                context = DefaultContext;
            }

            // routable reference
            return new Reference(adapterId: adapterId ?? "", // TODO: make adapterId parameter non null
                                 cacheConnection: cacheConnection,
                                 collocationOptimized: collocOptimized,
                                 communicator: this,
                                 compress: null,
                                 connectionId: "",
                                 connectionTimeout: null,
                                 context: context,
                                 encoding: encoding,
                                 endpointSelection: endpointSelection,
                                 endpoints: endpoints,
                                 facet: facet,
                                 identity: ident,
                                 invocationMode: mode,
                                 invocationTimeout: invocationTimeout,
                                 locatorCacheTimeout: locatorCacheTimeout,
                                 locatorInfo: locatorInfo,
                                 preferNonSecure: preferNonSecure,
                                 protocol: protocol,
                                 routerInfo: routerInfo);
        }
    }
}
