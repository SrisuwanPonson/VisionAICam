using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClearEngine.Logging;
using ClearEngine.Model.Inference;
using System.Net;
using System.Net.Sockets;
using Modbus.Device;
using Modbus.Data;
using VisionAICam.Pages;
using VisionAICam.Modbus;
using VisionAICam.Services;


namespace VisionAICam.Core
{
    /// <summary>
    /// Core MasterController — application-wide singleton and service container.
    /// Other classes obtain the central controller via `MasterController.Instance`.
    /// Provides initialization, shutdown and a small DI-like Register/Get API.
    /// </summary>
    public sealed class MasterController : IAsyncDisposable, IDisposable
    {
        private static readonly Lazy<MasterController> _lazy = new(() => new MasterController());
        public static MasterController Instance => _lazy.Value;

        private readonly ConcurrentDictionary<Type, object> _services = new();
        private readonly ILogger _logger = global::ClearEngine.Logging.Logger.Instance;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private CancellationTokenSource? _cts;
        private bool _initialized;

        // Page instances (created on UI thread on-demand)
        private Production? _production;
        private CameraPage? _cameraPage;
        private DataSetPage? _dataSetPage;
        private ModelPage? _modelPage;
        private SettingPage? _settingPage;
        private DiagnosticsPage? _diagnosticsPage;
        private DataSetPage? _dataSetPage2;
        private UserPage? _userPage;

        // RobotPage backing field (new)
        private RobotPage? _robotPage;

        // Backing field (add near other page fields)
        private RobotService? _robotService;

        // Modbus TCP server (slave) fields
        private TcpListener? _modbusListener;
        private ModbusTcpSlave? _modbusSlave;
        private Task? _modbusListenTask;
        // Expose status and event
        public bool IsModbusServerRunning => _modbusSlave != null && _modbusListener != null;
        public event Action<bool>? ModbusServerStatusChanged;
        // Raised when server-side DataStore content changes (e.g., detections or test writes)
        public event Action? ModbusDataChanged;

        private MasterController() { }

        // Expose pages (created on UI thread)
        public Production Production => EnsureOnUi(ref _production, () => new Production());
        public CameraPage CameraPage => EnsureOnUi(ref _cameraPage, () => new CameraPage());
        public DataSetPage DataSetPage => EnsureOnUi(ref _dataSetPage, () => new DataSetPage());
        public ModelPage ModelPage => EnsureOnUi(ref _modelPage, () => new ModelPage());
        public SettingPage SettingPage => EnsureOnUi(ref _settingPage, () => new SettingPage());
        public DiagnosticsPage DiagnosticsPage => EnsureOnUi(ref _diagnosticsPage, () => new DiagnosticsPage());
        public DataSetPage DataSetPage2 => EnsureOnUi(ref _dataSetPage2, () => new DataSetPage());
        public UserPage UserPage => EnsureOnUi(ref _userPage, () => new UserPage());

        // RobotPage property (new) - leverages EnsureOnUi same as other pages
        public RobotPage RobotPage => EnsureOnUi(ref _robotPage, () => new RobotPage());

        // Expose RobotService (create on UI thread like pages if not already created)
        public RobotService RobotService => EnsureOnUi(ref _robotService, () => new RobotService());

        public ILogger Logger => _logger;

        // Simple service registration / resolution
        public void RegisterService<T>(T service) where T : class
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            _services[typeof(T)] = service;
        }

        /// <summary>
        /// Convenience method to write a small test pattern of detection objects into the DataStore
        /// so a remote Modbus client can read them. This will set register 1000 to 'count' and
        /// then write up to 'count' objects at 1001 + i*3 with classId, X, Y.
        /// </summary>
        public bool WriteTestPattern(int count)
        {
            try
            {
                if (_modbusSlave == null) return false;
                var ds = _modbusSlave.DataStore;
                if (ds == null) return false;

                int maxObjects = 100;
                int n = Math.Max(0, Math.Min(count, maxObjects));
                try { ds.HoldingRegisters[1000] = (ushort)n; } catch { }

                for (int i = 0; i < n; i++)
                {
                    int baseAddr = 1001 + i * 3;
                    try { ds.HoldingRegisters[baseAddr + 0] = (ushort)(i + 1); } catch { }
                    try { ds.HoldingRegisters[baseAddr + 1] = unchecked((ushort)(short)(50 + i * 10)); } catch { }
                    try { ds.HoldingRegisters[baseAddr + 2] = unchecked((ushort)(short)(60 + i * 12)); } catch { }
                }
                try { ModbusDataChanged?.Invoke(); } catch { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Read holding registers from the internal Modbus DataStore in a safe manner.
        /// Returns an array of length 'count' filled with register values (or 0 on error).
        /// </summary>
        public ushort[] GetHoldingRegisters(int startAddress, int count)
        {
            var result = new ushort[count];
            try
            {
                if (_modbusSlave == null) return result;
                var ds = _modbusSlave.DataStore;
                if (ds == null) return result;

                for (int i = 0; i < count; i++)
                {
                    try { result[i] = ds.HoldingRegisters[startAddress + i]; } catch { result[i] = 0; }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// Write a single holding register into the internal Modbus DataStore (for testing).
        /// Returns true on success.
        /// </summary>
        public bool WriteHoldingRegister(int address, ushort value)
        {
            try
            {
                if (_modbusSlave == null) return false;
                var ds = _modbusSlave.DataStore;
                if (ds == null) return false;
                try { ds.HoldingRegisters[address] = value; try { ModbusDataChanged?.Invoke(); } catch { } return true; } catch { return false; }
            }
            catch { return false; }
        }

        /// <summary>
        /// Convenience: produce a simple human-readable snapshot of the Modbus holding registers
        /// starting at startAddress for 'count' registers.
        /// </summary>
        public string GetModbusSnapshot(int startAddress = 1000, int count = 1 + 100 * 3)
        {
            try
            {
                var regs = GetHoldingRegisters(startAddress, count);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Modbus snapshot starting at {startAddress} ({count} regs):");
                for (int i = 0; i < regs.Length; i += 8)
                {
                    sb.Append($"{startAddress + i,6}: ");
                    for (int j = 0; j < 8 && i + j < regs.Length; j++) sb.Append($"0x{regs[i + j]:X4} ");
                    sb.AppendLine();
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                try { _logger.LogError($"GetModbusSnapshot failed: {ex}"); } catch { }
                return string.Empty;
            }
        }

        /// <summary>
        /// Write detection results into Modbus holding registers using the layout:
        /// 1000 = object count (ushort)
        /// for i=0..N-1: base = 1001 + i*3: classID, X, Y (each ushort)
        /// X/Y are written as int16 (rounded) in pixels.
        /// </summary>
        public void WriteDetectionsToModbus(System.Collections.Generic.IEnumerable<VisionAICam.Pages.DetectionResult>? detections)
        {
            try
            {
                if (_modbusSlave == null) return;
                var ds = _modbusSlave.DataStore;
                if (ds == null) return;

                // Convert to list for count and indexing
                var list = (detections == null) ? new System.Collections.Generic.List<VisionAICam.Pages.DetectionResult>() : new System.Collections.Generic.List<VisionAICam.Pages.DetectionResult>(detections);

                // cap to avoid overflowing reserved register space (arbitrary cap: 100 objects)
                int maxObjects = 100;
                if (list.Count > maxObjects) list = list.GetRange(0, maxObjects);

                // Clear the reserved object register area before writing so polling clients don't see stale entries
                try
                {
                    int clearRegs = maxObjects * 3; // 3 registers per object (classId, X, Y)
                    for (int r = 0; r < clearRegs; r++)
                    {
                        try { ds.HoldingRegisters[1001 + r] = 0; } catch { }
                    }
                    try { _logger.LogInfo($"WriteDetectionsToModbus: cleared {clearRegs} object registers starting at 1001"); } catch { }
                    try { Console.WriteLine($"Modbus: cleared {clearRegs} object registers starting at 1001"); } catch { }
                }
                catch { }

                // Write object slots first, then update the count register last to avoid readers seeing a stale count
                int writtenSlots = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    // Resolve class ID: prefer supplied ClassId from the detection DTO, fall back to AppSettings.ClassIdMap by name
                    ushort classId = 0;
                    try
                    {
                        // Try to use item.ClassId (string) if provided
                        try
                        {
                            var supplied = item?.ClassId ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(supplied))
                            {
                                // Try integer parse first
                                if (ushort.TryParse(supplied.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                                {
                                    classId = parsed;
                                }
                                else
                                {
                                    // Try float/double and round
                                    if (double.TryParse(supplied.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dval))
                                    {
                                        var iv = (int)Math.Round(dval);
                                        if (iv < 0) iv = 0; if (iv > ushort.MaxValue) iv = ushort.MaxValue;
                                        classId = (ushort)iv;
                                    }
                                }
                            }
                        }
                        catch { }

                        // If still zero, fall back to mapping by class name from settings
                        if (classId == 0)
                        {
                            var settings = GetService<AppSettings>() ?? SettingsManager.Load();
                            if (settings != null && settings.ClassIdMap != null && settings.ClassIdMap.TryGetValue(item.ClassName ?? string.Empty, out var cid))
                                classId = cid;
                        }
                    }
                    catch { }

                    // Parse center coordinates as floats
                    float cx = 0f, cy = 0f;
                    try
                    {
                        var parts = (item.Box ?? string.Empty).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4)
                        {
                            if (double.TryParse(parts[0], out var x1) && double.TryParse(parts[1], out var y1) && double.TryParse(parts[2], out var x2) && double.TryParse(parts[3], out var y2))
                            {
                                cx = (float)((x1 + x2) / 2.0);
                                cy = (float)((y1 + y2) / 2.0);
                            }
                        }
                        else if (parts.Length >= 2)
                        {
                            if (double.TryParse(parts[0], out var xx) && double.TryParse(parts[1], out var yy))
                            {
                                cx = (float)xx;
                                cy = (float)yy;
                            }
                        }
                    }
                    catch { }
                    // Per-object layout: classID(1), X(1), Y(1) where X/Y are int16 (rounded pixels)
                    int baseAddr = 1001 + i * 3;
                    try { ds.HoldingRegisters[baseAddr] = classId; } catch (Exception ex) { try { _logger.LogError($"WriteDetectionsToModbus: failed writing classId at {baseAddr}: {ex.Message}"); } catch { } }

                    short xi = 0;
                    short yi = 0;
                    try { xi = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, (int)Math.Round(cx))); } catch { xi = 0; }
                    try { yi = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, (int)Math.Round(cy))); } catch { yi = 0; }

                    try { ds.HoldingRegisters[baseAddr + 1] = unchecked((ushort)xi); } catch (Exception ex) { try { _logger.LogError($"WriteDetectionsToModbus: failed writing X at {baseAddr + 1}: {ex.Message}"); } catch { } }
                    try { ds.HoldingRegisters[baseAddr + 2] = unchecked((ushort)yi); } catch (Exception ex) { try { _logger.LogError($"WriteDetectionsToModbus: failed writing Y at {baseAddr + 2}: {ex.Message}"); } catch { } }

                    bool slotEmpty = classId == 0 && xi == 0 && yi == 0;
                    if (!slotEmpty) writtenSlots++;

                    // Log what we wrote for debugging (single X/Y registers as int16)
                    try
                    {
                        try
                        {
                            // Include original class name and confidence so logs show what was mapped into registers
                            string clsName = item?.ClassName ?? string.Empty;
                            string confStr = string.Empty;
                            try { confStr = (item?.Confidence).ToString() ?? string.Empty; } catch { }
                            _logger.LogInfo($"Modbus: wrote obj[{i}] base={baseAddr} className='{clsName}' classId={classId} conf={confStr} XReg=0x{unchecked((ushort)xi):X4} YReg=0x{unchecked((ushort)yi):X4} X={xi} Y={yi}");
                            //try { Console.WriteLine($"Modbus: wrote obj[{i}] base={baseAddr} className='{clsName}' classId={classId} conf={confStr} XReg=0x{unchecked((ushort)xi):X4} YReg=0x{unchecked((ushort)yi):X4} X={xi} Y={yi}"); } catch { }
                        }
                        catch { /* swallow logging formatting errors */ }
                    }
                    catch { }
                }

                // Finally write the object count at register 1000 so readers see a consistent view
                try
                {
                    ushort count = (ushort)writtenSlots;
                    const int maxRetries = 3;
                    bool ok = false;
                    for (int attempt = 0; attempt < maxRetries; attempt++)
                    {
                        try { ds.HoldingRegisters[1000] = count; } catch (Exception ex) { try { _logger.LogError($"WriteDetectionsToModbus: failed writing count register attempt {attempt}: {ex.Message}"); } catch { } }
                        try
                        {
                            // read-back verify
                            ushort readBack = ds.HoldingRegisters[1000];
                            if (readBack == count)
                            {
                                ok = true;
                                try { _logger.LogInfo($"WriteDetectionsToModbus: count register set to {count} (verified)"); } catch { }
                                try { Console.WriteLine($"Modbus: count register set to {count} (verified)"); } catch { }
                                break;
                            }
                            else
                            {
                                try { _logger.LogInfo($"WriteDetectionsToModbus: count register read-back {readBack} (expected {count}), retrying..."); } catch { }
                                try { Console.WriteLine($"Modbus: count register read-back {readBack} (expected {count}), retrying..."); } catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                            try { _logger.LogError($"WriteDetectionsToModbus: failed reading back count register attempt {attempt}: {ex.Message}"); } catch { }
                        }
                        // small pause before retrying
                        try { System.Threading.Thread.Sleep(10); } catch { }
                    }
                    if (!ok)
                    {
                        try { _logger.LogError($"WriteDetectionsToModbus: failed to verify count register after {maxRetries} attempts"); } catch { }
                        try { Console.WriteLine($"Modbus: failed to verify count register after {maxRetries} attempts"); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    try { _logger.LogError($"WriteDetectionsToModbus: failed writing count register: {ex}"); } catch { }
                }

                // Debug: snapshot what was written into registers (hex + decoded floats per object)
                try
                {
                    int totalRegs = 1 + Math.Min(list.Count, maxObjects) * 3;
                    var regs = new System.Collections.Generic.List<ushort>(totalRegs);
                    for (int r = 0; r < totalRegs; r++)
                    {
                        try { regs.Add(ds.HoldingRegisters[1000 + r]); } catch { regs.Add(0); }
                    }

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"Modbus snapshot starting at 1000 ({totalRegs} regs):");
                    // Add clear mapping/meaning to help read the dump
                    try
                    {
                        int numObjectsPossible = Math.Min(list.Count, maxObjects);
                        // show count value first
                        ushort countReg = regs.Count > 0 ? regs[0] : (ushort)0;
                        sb.AppendLine($"Meaning:");
                        sb.AppendLine($"  1000 = object count ({countReg})");
                        for (int oi = 0; oi < numObjectsPossible; oi++)
                        {
                            int b = 1001 + oi * 3;
                            sb.AppendLine($"  {b} = classID object{oi + 1}");
                            sb.AppendLine($"  {b + 1} = X{oi + 1} (int16)");
                            sb.AppendLine($"  {b + 2} = Y{oi + 1} (int16)");
                        }
                    }
                    catch { }
                    for (int i = 0; i < regs.Count; i += 8)
                    {
                        sb.Append($"{1000 + i,6}: ");
                        for (int j = 0; j < 8 && i + j < regs.Count; j++) sb.Append($"0x{regs[i + j]:X4} ");
                        sb.AppendLine();
                    }
                    try { _logger.LogInfo(sb.ToString()); } catch { }
                    try { Console.WriteLine("Raw Data:"); } catch { }
                    try { Console.WriteLine(sb.ToString()); } catch { }

                    // Per-object decoded view
                    for (int i = 0; i < list.Count; i++)
                    {
                        int baseAddr = 1001 + i * 3;
                        try
                        {
                            ushort classId = ds.HoldingRegisters[baseAddr];
                            ushort xReg = ds.HoldingRegisters[baseAddr + 1];
                            ushort yReg = ds.HoldingRegisters[baseAddr + 2];
                            short xVal = unchecked((short)xReg);
                            short yVal = unchecked((short)yReg);
                            try { _logger.LogInfo($"Modbus decode obj[{i}]: classId={classId} XReg=0x{xReg:X4} X={xVal} YReg=0x{yReg:X4} Y={yVal}"); } catch { }
                            try { Console.WriteLine($"Modbus decode obj[{i}]: classId={classId} XReg=0x{xReg:X4} X={xVal} YReg=0x{yReg:X4} Y={yVal}"); } catch { }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    try { _logger.LogError($"WriteDetectionsToModbus: failed to snapshot registers: {ex}"); } catch { }
                }
                // Notify listeners that server-side Modbus data changed
                try { ModbusDataChanged?.Invoke(); } catch { }
            }
            catch (Exception ex)
            {
                try { _logger.LogError($"WriteDetectionsToModbus failed: {ex}"); } catch { }
            }
        }

        public T? GetService<T>() where T : class
        {
            if (_services.TryGetValue(typeof(T), out var svc)) return svc as T;
            return null;
        }

        public T GetRequiredService<T>() where T : class
        {
            var svc = GetService<T>();
            if (svc == null)
                throw new InvalidOperationException($"Required service '{typeof(T).FullName}' is not registered. Ensure MasterController.Instance.InitializeAsync(...) has run and registered the service.");
            return svc;
        }

        /// <summary>
        /// Initialize application core: load settings, optionally initialize inference engine and register services.
        /// Safe to call multiple times; subsequent calls are no-ops.
        /// </summary>
        public async Task<bool> InitializeAsync(bool prewarmInferenceEngine = true, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_initialized)
                {
                    progress?.Report("Already initialized.");
                    return true;
                }

                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                progress?.Report("Loading settings...");
                AppSettings? settings = null;
                try
                {
                    settings = SettingsManager.Load();
                    if (settings == null)
                    {
                        settings = new AppSettings();
                    }

                    // normalize some defaults
                    if (settings.SamplingInterval <= 0) settings.SamplingInterval = 20;
                    if (settings.CameraIndex < 0) settings.CameraIndex = 0;
                    if (string.IsNullOrWhiteSpace(settings.DefaultModelPath)) settings.DefaultModelPath = "model.pt";
                    if (string.IsNullOrWhiteSpace(settings.DefaultImagePath)) settings.DefaultImagePath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    if (string.IsNullOrWhiteSpace(settings.PythonDllPath))
                    {
                        settings.PythonDllPath = Path.Combine(@"C:\ClearEngine\VisionAICam", "NewEnv", "Python313", "python313.dll");
                    }

                    RegisterService(settings);
                    progress?.Report("Settings loaded.");
                }
                catch (Exception ex)
                {
                    try { _logger.LogError($"MasterController: settings load failed: {ex}"); } catch { }
                    progress?.Report("Failed loading settings (continuing).");
                }

                // Ensure required application folders exist under C:\ClearEngine\VisionAICam
                try
                {
                    progress?.Report("Ensuring application folders...");

                    string appRoot = @"C:\ClearEngine\VisionAICam";
                    var requiredFolders = new[]
                    {
                        "Datasets",
                        "Models",
                        "PythonEnv",
                        "PythonScripts",
                        "Runtime",
                        "Setup",
                        "Images"
                    };

                    foreach (var folderName in requiredFolders)
                    {
                        string fullPath = Path.Combine(appRoot, folderName);
                        if (!Directory.Exists(fullPath))
                        {
                            Directory.CreateDirectory(fullPath);
                            try { _logger.LogInfo($"Created application folder: {fullPath}"); } catch { }
                        }
                        else
                        {
                            try { _logger.LogInfo($"Application folder exists: {fullPath}"); } catch { }
                        }
                    }

                    // Ensure a dedicated folder for persisted/saved projects inside the Setup folder
                    try
                    {
                        string projectsFolder = Path.Combine(appRoot, "Setup", "Projects");
                        if (!Directory.Exists(projectsFolder))
                        {
                            Directory.CreateDirectory(projectsFolder);
                            try { _logger.LogInfo($"Created projects folder: {projectsFolder}"); } catch { }
                        }
                        else
                        {
                            try { _logger.LogInfo($"Projects folder exists: {projectsFolder}"); } catch { }
                        }
                    }
                    catch (Exception exProjects)
                    {
                        try { _logger.LogError($"MasterController: failed to ensure projects folder: {exProjects}"); } catch { }
                    }

                    progress?.Report("Application folders ensured.");
                }
                catch (Exception ex)
                {
                    try { _logger.LogError($"MasterController: failed to ensure app folders: {ex}"); } catch { }
                    progress?.Report("Failed to ensure application folders (continuing).");
                }

                //Try to initialize inference engine and register wrapper service
                if (prewarmInferenceEngine)
                {
                    try
                    {
                        //Production.Prewarm();
                        progress?.Report("Inference engine initialized.");
                    }
                    catch (Exception ex)
                    {
                        try { _logger.LogError($"MasterController: engine init error: {ex}"); } catch { }
                    }
                }

                // --- Create and register RobotService; attempt background connect using persisted settings ---
                try
                {
                    var settingsForRobot = settings ?? SettingsManager.Load();

                    var robot = new RobotService();

                    // apply persisted swap flag
                    if (settingsForRobot != null)
                        robot.SwapFloatWords = settingsForRobot.SwapFloatWords;

                    // store and register the service so other consumers obtain the same instance
                    _robotService = robot;
                    RegisterService(robot);

                    // If a host is configured, attempt a non-blocking background connect so startup isn't delayed/fails.
                    if (!string.IsNullOrWhiteSpace(settingsForRobot?.MasterControllerIp))
                    {
                        string host = settingsForRobot.MasterControllerIp;
                        int port = settingsForRobot.MasterControllerPort;
                        progress?.Report($"Starting background robot connect to {host}:{port}...");

                        // Fire-and-forget connect: do not await here so InitializeAsync completes quickly.
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                bool ok = await robot.ConnectTcpAsync(host, port).ConfigureAwait(false);
                                try { _logger.LogInfo($"MasterController: RobotService background connect to {host}:{port} {(ok ? "succeeded" : "failed")}"); } catch { }
                                // Report progress (best-effort)
                                try { progress?.Report(ok ? "Robot connected." : "Robot not connected."); } catch { }
                            }
                            catch (Exception ex)
                            {
                                try { _logger.LogError($"MasterController: RobotService background connect error: {ex}"); } catch { }
                                try { progress?.Report("Robot connect failed (background)."); } catch { }
                            }
                        });
                    }
                    else
                    {
                        progress?.Report("No robot host configured.");
                    }

                    // Start Modbus TCP server (slave) automatically during initialization
                    try
                    {
                        bool ok = StartModbusServer();
                        try { _logger.LogInfo($"MasterController: Modbus server start during init {(ok ? "succeeded" : "failed")}"); } catch { }
                        try { progress?.Report(ok ? "Modbus TCP server started." : "Modbus TCP server failed to start."); } catch { }
                    }
                    catch (Exception ex)
                    {
                        try { _logger.LogError($"MasterController: Modbus server start failed: {ex}"); } catch { }
                        try { progress?.Report("Modbus TCP server failed to start."); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    try { _logger.LogError($"MasterController: RobotService initialization failed: {ex}"); } catch { }
                }

                // Add this block immediately after registering the RobotService in InitializeAsync
                try
                {
                    // Force creation of the PredictionStore singleton and register it with MasterController
                    var predictionStore = VisionAICam.Services.PredictionStore.Instance;
                    RegisterService(predictionStore);
                    progress?.Report("Prediction store ready.");
                }
                catch (Exception ex)
                {
                    try { _logger.LogError($"MasterController: PredictionStore init failed: {ex}"); } catch { }
                    try { progress?.Report("Prediction store initialization failed (continuing)."); } catch { }
                }

                _initialized = true;
                try { progress?.Report("Initialization complete."); } catch { }
                try { _logger.LogInfo("MasterController: initialization complete."); } catch { }
                return true;
            }
            catch (OperationCanceledException)
            {
                try { _logger.LogInfo("MasterController: initialization cancelled."); } catch { }
                try { progress?.Report("Initialization cancelled."); } catch { }
                return false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public Task<bool> InitializeEverythingAsync(bool prewarmInferenceEngine = true, bool prewarmCamera = false, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => InitializeAsync(prewarmInferenceEngine: prewarmInferenceEngine, progress: progress, cancellationToken: cancellationToken);

        /// <summary>
        /// Shutdown: disposes registered IDisposable services and clears container.
        /// </summary>
        public async Task ShutdownAsync(IProgress<string>? progress = null)
        {
            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_initialized && _cts == null)
                {
                    progress?.Report("Nothing to shut down.");
                    return;
                }

                try { progress?.Report("Cancelling operations..."); _cts?.Cancel(); } catch { }

                foreach (var kv in _services)
                {
                    if (kv.Value is IDisposable d)
                    {
                        try { progress?.Report($"Disposing {kv.Key.Name}..."); d.Dispose(); } catch (Exception ex) { try { _logger.LogInfo($"Disposing {kv.Key.Name} failed: {ex.Message}"); } catch { } }
                    }
                }

                // Stop Modbus server if running
                try { StopModbusServer(); } catch { }

                _services.Clear();

                try { progress?.Report("Finalizing GC..."); GC.Collect(); GC.WaitForPendingFinalizers(); } catch { }

                _initialized = false;
                try { _logger.LogInfo("MasterController: shutdown complete."); } catch { }
                progress?.Report("Shutdown complete.");
            }
            finally
            {
                _initLock.Release();
            }
        }

        // Backward-compat helper
        public InferenceEngine? GetInferenceEngine()
        {
            return GetService<InferenceEngine>();
        }

        public async ValueTask DisposeAsync()
        {
            await ShutdownAsync().ConfigureAwait(false);
            _cts?.Dispose();
            _initLock.Dispose();
            try { _robotService?.Dispose(); } catch { }
            _robotService = null;
        }

        public void Dispose()
        {
            try { ShutdownAsync().GetAwaiter().GetResult(); } catch { }
            _cts?.Dispose();
            _initLock.Dispose();
            try { _robotService?.Dispose(); } catch { }
            _robotService = null;
            try { StopModbusServer(); } catch { }
            GC.SuppressFinalize(this);
        }

        // Starts a Modbus TCP slave using NModbus on the configured port. Safe to call multiple times.
        // Returns true when started successfully, false otherwise.
        public bool StartModbusServer(AppSettings? settings = null)
        {
            try
            {
                if (_modbusSlave != null) return true; // already running

                int port = settings?.MasterControllerPort ?? 502;
                _modbusListener = new TcpListener(IPAddress.Any, port);
                _modbusListener.Start();

                // Create slave unit id 1
                byte unitId = 1;
                _modbusSlave = ModbusTcpSlave.CreateTcp(unitId, _modbusListener);

                // Initialize data store
                _modbusSlave.DataStore = DataStoreFactory.CreateDefaultDataStore();

                // Initialize example values in the DataStore: count=3 and three sample objects
                try
                {
                    var ds = _modbusSlave.DataStore;
                    if (ds != null)
                    {
                        // sample three objects
                        ds.HoldingRegisters[1000] = 3;

                        var svcSettings = GetService<AppSettings>() ?? SettingsManager.Load();
                        bool swapWords = svcSettings?.SwapFloatWords ?? false;

                        // object 1: classId=1, X=101, Y=120
                        ds.HoldingRegisters[1001] = 1;
                        ds.HoldingRegisters[1002] = unchecked((ushort)(short)101);
                        ds.HoldingRegisters[1003] = unchecked((ushort)(short)120);

                        // object 2: classId=2, X=201, Y=221
                        int base2 = 1001 + 1 * 3;
                        ds.HoldingRegisters[base2 + 0] = 2;
                        ds.HoldingRegisters[base2 + 1] = unchecked((ushort)(short)201);
                        ds.HoldingRegisters[base2 + 2] = unchecked((ushort)(short)221);

                        // object 3: classId=3, X=300, Y=321
                        int base3 = 1001 + 2 * 3;
                        ds.HoldingRegisters[base3 + 0] = 3;
                        ds.HoldingRegisters[base3 + 1] = unchecked((ushort)(short)300);
                        ds.HoldingRegisters[base3 + 2] = unchecked((ushort)(short)321);
                    }
                }
                catch { }

                // Run listener in background task so it doesn't block initialization
                _modbusListenTask = Task.Run(() =>
                {
                    try
                    {
                        _modbusSlave.Listen();
                    }
                    catch (Exception ex)
                    {
                        try { _logger.LogError($"Modbus slave listen failed: {ex}"); } catch { }
                    }
                });

                try { ModbusServerStatusChanged?.Invoke(true); } catch { }
                return true;
            }
            catch (Exception)
            {
                // Ensure partial startup is cleaned up
                try { StopModbusServer(); } catch { }
                return false;
            }
        }

        public void StopModbusServer()
        {
            try
            {
                try { _modbusSlave?.Dispose(); } catch { }
                _modbusSlave = null;
                try { _modbusListener?.Stop(); } catch { }
                _modbusListener = null;
                _modbusListenTask = null;
                try { ModbusServerStatusChanged?.Invoke(false); } catch { }
            }
            catch { }
        }

        // UI-thread safe factory helper (creates control on UI dispatcher if necessary)
        private T EnsureOnUi<T>(ref T? field, Func<T> factory) where T : class
        {
            if (field != null) return field;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                field = factory();
                return field;
            }

            if (dispatcher.CheckAccess())
            {
                field = factory();
                return field;
            }

            T? created = null;
            dispatcher.Invoke(() =>
            {
                try { created = factory(); } catch (Exception ex) { try { _logger.LogError($"EnsureOnUi factory threw: {ex}"); } catch { } }
            });

            if (field == null && created != null) field = created;
            return field ?? created!;
        }

        // Shared, UI-bound collection of detection results used by DataPage.
        // Uses the Production page DTO type (VisionAICam.Pages.DetectionResult).
        private readonly ObservableCollection<VisionAICam.Pages.DetectionResult> _sharedResults = new();
        public ObservableCollection<VisionAICam.Pages.DetectionResult> SharedResults => _sharedResults;

        private const int DefaultMaxSharedResults = 100;

        private readonly ConcurrentQueue<VisionAICam.Pages.DetectionResult> _resultsQueue = new();
        private int _flushPending = 0; // 0 = not scheduled, 1 = scheduled or running

        // Add results on UI thread in a single dispatched op and keep collection size bounded.
        public void AddDetectionResults(IEnumerable<VisionAICam.Pages.DetectionResult> results)
        {
            if (results == null)
            {
                try { _logger.LogInfo("AddDetectionResults called with null results."); } catch { }
                return;
            }

            // enqueue items quickly from any thread and count them for debug
            int enqueued = 0;
            foreach (var r in results)
            {
                try { _resultsQueue.Enqueue(r); enqueued++; } catch { }
            }

            try { _logger.LogInfo($"AddDetectionResults: enqueued {enqueued} items (queue size approx unknown)."); } catch { }

            // schedule a single UI flush if none is pending
            if (Interlocked.Exchange(ref _flushPending, 1) == 0)
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    // No UI yet — drop or buffer (we already buffered in _resultsQueue)
                    Interlocked.Exchange(ref _flushPending, 0);
                    try { _logger.LogInfo("AddDetectionResults: dispatcher not available, flush deferred."); } catch { }
                    return;
                }

                try { _logger.LogInfo("AddDetectionResults: scheduling UI flush of queued detection results."); } catch { }
                dispatcher.BeginInvoke(new Action(FlushQueuedResults), DispatcherPriority.Normal);
            }
        }

        // runs on UI thread (dispatched)
        private void FlushQueuedResults()
        {
            try
            {
                // drain queue into a list
                var list = new List<VisionAICam.Pages.DetectionResult>();
                while (_resultsQueue.TryDequeue(out var item))
                    list.Add(item);

                if (list.Count > 0)
                    AddRangeAndTrim(_sharedResults, list.ToArray(), DefaultMaxSharedResults);
            }
            catch (Exception ex)
            {
                try { _logger.LogError($"FlushQueuedResults failed: {ex}"); } catch { }
            }
            finally
            {
                // mark not pending
                Interlocked.Exchange(ref _flushPending, 0);

                // If new items arrived while we were flushing, schedule another flush
                if (!_resultsQueue.IsEmpty && Interlocked.Exchange(ref _flushPending, 1) == 0)
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher != null)
                        dispatcher.BeginInvoke(new Action(FlushQueuedResults), DispatcherPriority.Normal);
                    else
                        Interlocked.Exchange(ref _flushPending, 0);
                }
            }
        }

        // Helper: add items and trim oldest to keep collection bounded (must be called on UI thread)
        private static void AddRangeAndTrim(ObservableCollection<VisionAICam.Pages.DetectionResult> target, VisionAICam.Pages.DetectionResult[] items, int maxItems)
        {
            if (target == null || items == null || items.Length == 0) return;

            foreach (var it in items)
            {
                target.Add(it);
            }

            // Trim oldest entries if we've grown too large
            if (maxItems > 0)
            {
                while (target.Count > maxItems)
                {
                    try { target.RemoveAt(0); } catch { break; }
                }
            }
        }

        /// <summary>
        /// Attempts to prewarm the inference engine using an image taken from the configured
        /// DefaultImagePath (file or first image under the directory). This method is safe to
        /// call multiple times and runs the actual detection on a background thread so it does
        /// not block the caller.
        /// Returns true when an image was found and detection completed (or ran without throwing).
        /// </summary>
        public async Task<bool> PrewarmInferenceFromDefaultImageAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var settings = GetService<AppSettings>() ?? SettingsManager.Load();
                string? startPath = settings?.DefaultImagePath;
                if (string.IsNullOrWhiteSpace(startPath))
                    startPath = AppDomain.CurrentDomain.BaseDirectory;

                string? imageFile = null;

                // If path is a file, use it directly
                if (File.Exists(startPath))
                {
                    imageFile = startPath;
                }
                else
                {
                    // Treat as directory and search for common image types (first hit)
                    if (!Directory.Exists(startPath))
                        startPath = AppDomain.CurrentDomain.BaseDirectory;

                    string[] exts = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".webp" };
                    foreach (var ext in exts)
                    {
                        try
                        {
                            imageFile = Directory.EnumerateFiles(startPath, "*" + ext, SearchOption.AllDirectories).FirstOrDefault();
                            if (!string.IsNullOrEmpty(imageFile) && File.Exists(imageFile))
                                break;
                            imageFile = null;
                        }
                        catch (UnauthorizedAccessException) { /* skip inaccessible folders */ }
                        catch (PathTooLongException) { /* skip problematic paths */ }
                    }
                }

                if (string.IsNullOrWhiteSpace(imageFile) || !File.Exists(imageFile))
                {
                    try { _logger.LogInfo("PrewarmInferenceFromDefaultImageAsync: no image found to prewarm."); } catch { }
                    return false;
                }

                // Read image bytes once (small memory cost) and run detection in background to prewarm
                byte[] jpegBuffer = await Task.Run(() => File.ReadAllBytes(imageFile), cancellationToken).ConfigureAwait(false);

                var engine = GetService<InferenceEngine>();
                if (engine == null)
                {
                    try { _logger.LogWarning("PrewarmInferenceFromDefaultImageAsync: InferenceEngine service not registered."); } catch { }
                    return false;
                }

                string modelPath = engine.modelPath ?? settings?.DefaultModelPath ?? "model.pt";
                string logDir = _logger.GetLogDirectory();

                // Run detection on background thread — this will initialize Python + model calls inside the engine
                await Task.Run(() =>
                {
                    try
                    {
                        // Use Detect(byte[]) which already attempts in-memory call and falls back to temp file as needed.
                        var res = engine.Detect(jpegBuffer, modelPath, logDir);
                        try { _logger.LogInfo($"PrewarmInferenceFromDefaultImageAsync: detection ran on {Path.GetFileName(imageFile)}, results: {res?.Length ?? 0}"); } catch { }
                    }
                    catch (Exception ex)
                    {
                        try { _logger.LogError($"PrewarmInferenceFromDefaultImageAsync: detection failed: {ex}"); } catch { }
                        throw;
                    }
                }, cancellationToken).ConfigureAwait(false);

                return true;
            }
            catch (OperationCanceledException)
            {
                try { _logger.LogInfo("PrewarmInferenceFromDefaultImageAsync cancelled."); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                try { _logger.LogError($"PrewarmInferenceFromDefaultImageAsync error: {ex}"); } catch { }
                return false;
            }
        }
    }
}
