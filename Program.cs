using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using Timer = System.Windows.Forms.Timer;

namespace StressDeez;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new BurnForm());
    }
}

internal sealed record Sensors(
    string CpuName = "CPU not found", string GpuName = "NVIDIA GPU not found",
    double? CpuWatts = null, double? GpuWatts = null,
    double? CpuTemp = null, double? GpuTemp = null,
    double? CpuLoad = null, double? GpuLoad = null,
    double? CpuClock = null, double? GpuClock = null,
    DateTime WhenUtc = default)
{
    public double? CombinedWatts => CpuWatts is double c && GpuWatts is double g ? c + g : null;
    public double? GpuLimitWatts { get; init; }
}

internal sealed class SensorReader : IDisposable
{
    private readonly Computer _computer = new() { IsCpuEnabled = true, IsMotherboardEnabled = true };
    private readonly object _sync = new();
    private Nvml? _nvml;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pollTask;
    private Sensors _last = new();
    public Sensors Latest => Volatile.Read(ref _last);
    public string SensorError { get; private set; } = "";
    private string _cpuDiagnostics = "CPU sensors have not been polled yet.";
    private string _cpuSource = "N/A";
    public string CpuDiagnostics => Volatile.Read(ref _cpuDiagnostics);
    public string CpuSource => Volatile.Read(ref _cpuSource);

    public SensorReader()
    {
        try { _computer.Open(); } catch (Exception e) { SensorError = "CPU sensor access: " + e.Message; }
        try { _nvml = new Nvml(); } catch (Exception e) { SensorError += " NVML: " + e.Message; }
        _pollTask = Task.Run(PollLoop);
    }
    private async Task PollLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { Update(); }
            catch (Exception e) { SensorError = "Sensor read: " + e.Message; }
            try { await Task.Delay(350, _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }
    private static IEnumerable<ISensor> SensorsFor(IHardware h)
    {
        h.Update();
        foreach (var s in h.Sensors) yield return s;
        foreach (var child in h.SubHardware)
            foreach (var s in SensorsFor(child)) yield return s;
    }
    private static double? Pick(IEnumerable<ISensor> sensors, SensorType type, params string[] names)
    {
        var list = sensors.Where(x => x.SensorType == type && x.Value.HasValue && float.IsFinite(x.Value.Value)).ToArray();
        foreach (string name in names)
        {
            ISensor? found = list.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found.Value;
        }
        foreach (string name in names)
        {
            ISensor? found = list.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found.Value;
        }
        return null;
    }
    private void Update()
    {
        lock (_sync)
        {
            var old = Latest;
            string cpuName = old.CpuName;
            double? power = null, temp = null, load = null, clock = null;
            try
            {
                var cpus = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu).ToArray();
                if (cpus.Length > 0)
                {
                    cpuName = cpus[0].Name;
                    var sensors = cpus.SelectMany(SensorsFor).ToArray();
                    power = Pick(sensors, SensorType.Power, "CPU Package", "CPU Package Power", "Package Power", "CPU PPT", "CPU Power");
                    temp = Pick(sensors, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "CPU (Tctl/Tdie)", "Core Max", "Tctl/Tdie", "CPU CCD1", "CPU Die");
                    load = Pick(sensors, SensorType.Load, "CPU Total", "Total");
                    clock = Pick(sensors, SensorType.Clock, "Core Average", "CPU Core #1", "Core #1");
                    // Zero/invalid package readings from a blocked sensor driver are not real wattage.
                    if (power is <= 0 or > 2000) power = null;
                    if (temp is <= 0 or > 150) temp = null;
                    if (clock is <= 0) clock = null;
                    string dump = string.Join(Environment.NewLine, sensors
                        .Where(x => x.SensorType is SensorType.Power or SensorType.Temperature or SensorType.Clock)
                        .Take(65).Select(x => $"{x.SensorType}: {x.Name} = {x.Value?.ToString() ?? "N/A"}"));
                    Volatile.Write(ref _cpuDiagnostics, $"CPU: {cpuName}{Environment.NewLine}LHM sensors: {sensors.Length}{Environment.NewLine}" + dump);
                    if (power.HasValue || temp.HasValue) Volatile.Write(ref _cpuSource, "LibreHardwareMonitor");
                    if (clock is null)
                    {
                        var clocks = sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue).Select(s => (double)s.Value!.Value).ToArray();
                        if (clocks.Length > 0) clock = clocks.Average();
                    }
                }
            }
            catch (Exception e)
            {
                SensorError = "CPU monitoring: " + e.Message;
                Volatile.Write(ref _cpuDiagnostics, SensorError);
            }
            // A read-only, optional fallback when Windows blocks LHM's CPU driver.
            // This uses measurements HWiNFO itself exposes; never estimates watts.
            if ((!power.HasValue || !temp.HasValue || !clock.HasValue) &&
                HwInfoShared.TryRead(out double? hwPower, out double? hwTemp, out double? hwClock, out string hwSource))
            {
                if (!power.HasValue) power = hwPower;
                if (!temp.HasValue) temp = hwTemp;
                if (!clock.HasValue) clock = hwClock;
                if (hwPower.HasValue || hwTemp.HasValue)
                    Volatile.Write(ref _cpuSource, "HWiNFO shared memory" + hwSource);
            }
            if (!power.HasValue || !temp.HasValue)
            {
                string reason = _computer.Hardware.Any(h => h.HardwareType == HardwareType.Cpu)
                    ? "No CPU package power/temperature reading from current sensor provider."
                    : "No CPU hardware sensor device found.";
                string last = CpuDiagnostics;
                if (!last.StartsWith(reason, StringComparison.Ordinal))
                    Volatile.Write(ref _cpuDiagnostics, reason + "\n" + last);
                if (!power.HasValue && !temp.HasValue) Volatile.Write(ref _cpuSource, "N/A");
            }
            string gpuName = old.GpuName;
            double? gpuWatts = null, gpuTemp = null, gpuLoad = null, gpuClock = null;
            try
            {
                if (_nvml != null)
                {
                    gpuName = _nvml.Name;
                    gpuWatts = _nvml.PowerWatts();
                    gpuTemp = _nvml.Temperature();
                    gpuLoad = _nvml.Utilization();
                    gpuClock = _nvml.GraphicsClock();
                }
            }
            catch (Exception e) { SensorError = "NVML: " + e.Message; }
            Volatile.Write(ref _last, new Sensors(cpuName, gpuName, power, gpuWatts, temp, gpuTemp, load, gpuLoad, clock, gpuClock, DateTime.UtcNow)
                { GpuLimitWatts = TryGpuLimit() });
        }
    }
    private double? TryGpuLimit()
    {
        try { return _nvml?.PowerLimitWatts(); } catch { return null; }
    }
    public void Dispose()
    {
        _stop.Cancel();
        try { _pollTask.Wait(1200); } catch { }
        lock (_sync) { _nvml?.Dispose(); _computer.Close(); }
        _stop.Dispose();
    }
}

// Optional sensor-provider bridge: HWiNFO shared memory, version 2.
// It does not launch HWiNFO, require any third-party DLL or alter CPU settings.
internal static class HwInfoShared
{
    private static string ReadCString(byte[] data, int start, int max)
    {
        int end = start;
        int limit = Math.Min(data.Length, start + max);
        while (end < limit && data[end] != 0) end++;
        return Encoding.Latin1.GetString(data, start, end - start).Trim();
    }
    private static int Score(string group, string label, int kind)
    {
        string n = label.ToLowerInvariant();
        string g = group.ToLowerInvariant();
        if (n.Contains("gpu") || g.Contains("gpu") || g.Contains("nvidia") || g.Contains("radeon")) return 0;
        if (!(g.Contains("cpu") || g.Contains("ryzen") || g.Contains("intel") || g.Contains("processor"))) return 0;
        if (kind == 5) // HWiNFO power sensor type
        {
            if (n == "cpu package power" || n.StartsWith("cpu package power (")) return 100;
            if (n == "cpu ppt" || n.StartsWith("cpu ppt (")) return 95;
            if (n == "cpu power" || n == "package power") return 85;
            if (n.Contains("package") && n.Contains("power")) return 75;
        }
        if (kind == 1)
        {
            if (n.Contains("tctl/tdie")) return 100;
            if (n == "cpu package" || n == "cpu (tctl/tdie)") return 95;
            if (n.Contains("cpu die") || n == "core max") return 85;
            if (n.StartsWith("cpu ccd") && n.Contains("tdie")) return 70;
        }
        if (kind == 6 && (n == "core effective clocks" || n == "core clocks" || n == "cpu clock")) return 80;
        return 0;
    }
    public static bool TryRead(out double? power, out double? temp, out double? clock, out string source)
    {
        power = null; temp = null; clock = null; source = "";
        try
        {
            using var mem = MemoryMappedFile.OpenExisting("Global\\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read);
            using var view = mem.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            long length = view.Capacity;
            if (length < 44) return false;
            byte[] header = new byte[44];
            view.ReadArray(0, header, 0, header.Length);
            if (Encoding.ASCII.GetString(header, 0, 4) != "HWiS") return false;
            long unix = BitConverter.ToInt64(header, 12);
            if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix) > 12) return false;
            uint sensorOffset = BitConverter.ToUInt32(header, 20);
            uint sensorSize = BitConverter.ToUInt32(header, 24);
            uint sensorCount = BitConverter.ToUInt32(header, 28);
            uint entryOffset = BitConverter.ToUInt32(header, 32);
            uint entrySize = BitConverter.ToUInt32(header, 36);
            uint entryCount = BitConverter.ToUInt32(header, 40);
            if (sensorCount > 1000 || entryCount > 10000 || sensorSize < 264 || entrySize < 316 ||
                (long)sensorOffset + (long)sensorSize * sensorCount > length ||
                (long)entryOffset + (long)entrySize * entryCount > length) return false;
            string[] groups = new string[(int)sensorCount];
            byte[] sensor = new byte[(int)sensorSize];
            for (int i = 0; i < groups.Length; i++)
            {
                view.ReadArray((long)sensorOffset + (long)i * sensorSize, sensor, 0, sensor.Length);
                string original = ReadCString(sensor, 8, 128);
                string customized = ReadCString(sensor, 136, 128);
                groups[i] = original.Length > 0 ? original : customized;
            }
            byte[] entry = new byte[(int)entrySize];
            int powerScore = 0, tempScore = 0, clockScore = 0;
            for (int i = 0; i < (int)entryCount; i++)
            {
                view.ReadArray((long)entryOffset + (long)i * entrySize, entry, 0, entry.Length);
                int kind = BitConverter.ToInt32(entry, 0);
                uint sensorIndex = BitConverter.ToUInt32(entry, 4);
                if (sensorIndex >= (uint)groups.Length) continue;
                string label = ReadCString(entry, 12, 128);
                int score = Score(groups[(int)sensorIndex], label, kind);
                if (score == 0) continue;
                double val = BitConverter.ToDouble(entry, 284);
                if (!double.IsFinite(val) || val <= 0) continue;
                if (kind == 5 && score > powerScore && val < 2000) { power = val; powerScore = score; }
                if (kind == 1 && score > tempScore && val < 150) { temp = val; tempScore = score; }
                if (kind == 6 && score > clockScore && val < 12000) { clock = val; clockScore = score; }
            }
            return power.HasValue || temp.HasValue || clock.HasValue;
        }
        catch (FileNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (Exception) { return false; }
    }
}

// Dynamically resolve NVML from the installed NVIDIA driver; never ship driver libraries.
internal sealed class Nvml : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ShutdownFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CountFn(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HandleFn(uint index, out nint device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetUIntFn(nint device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TemperatureFn(nint device, uint sensorType, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ClockFn(nint device, uint clockType, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NameFn(nint device, byte[] name, uint length);
    [StructLayout(LayoutKind.Sequential)] private struct UtilRates { public uint GPU; public uint Memory; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UtilFn(nint device, out UtilRates util);
    private readonly nint _dll;
    private nint _device;
    private bool _initialized;
    private readonly ShutdownFn _shutdown;
    private readonly GetUIntFn _power;
    private readonly GetUIntFn? _powerLimit;
    private readonly TemperatureFn _temp;
    private readonly ClockFn _clock;
    private readonly UtilFn _util;
    public string Name { get; private set; } = "NVIDIA GPU";
    private T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_dll, name));
    public Nvml()
    {
        if (!NativeLibrary.TryLoad("nvml.dll", out _dll))
        {
            string candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
            if (!NativeLibrary.TryLoad(candidate, out _dll)) throw new DllNotFoundException("NVIDIA NVML driver library unavailable");
        }
        try
        {
            _shutdown = Bind<ShutdownFn>("nvmlShutdown");
            _power = Bind<GetUIntFn>("nvmlDeviceGetPowerUsage");
            if (NativeLibrary.TryGetExport(_dll, "nvmlDeviceGetPowerManagementLimit", out nint powerLimitProc))
                _powerLimit = Marshal.GetDelegateForFunctionPointer<GetUIntFn>(powerLimitProc);
            _temp = Bind<TemperatureFn>("nvmlDeviceGetTemperature");
            _clock = Bind<ClockFn>("nvmlDeviceGetClockInfo");
            _util = Bind<UtilFn>("nvmlDeviceGetUtilizationRates");
            if (Bind<InitFn>("nvmlInit_v2")() != 0) throw new InvalidOperationException("NVML init failed");
            _initialized = true;
            if (Bind<CountFn>("nvmlDeviceGetCount_v2")(out uint count) != 0 || count == 0) throw new InvalidOperationException("No NVML GPU");
            if (Bind<HandleFn>("nvmlDeviceGetHandleByIndex_v2")(0, out _device) != 0) throw new InvalidOperationException("Cannot get GPU handle");
            byte[] bytes = new byte[128];
            if (Bind<NameFn>("nvmlDeviceGetName")(_device, bytes, (uint)bytes.Length) == 0)
                Name = System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        }
        catch { Dispose(); throw; }
    }
    public double? PowerWatts() => _power(_device, out uint v) == 0 ? v / 1000.0 : null;
    public double? PowerLimitWatts() => _powerLimit is not null && _powerLimit(_device, out uint v) == 0 ? v / 1000.0 : null;
    public double? Temperature() => _temp(_device, 0, out uint v) == 0 ? v : null;
    public double? Utilization() => _util(_device, out UtilRates v) == 0 ? v.GPU : null;
    public double? GraphicsClock() => _clock(_device, 0, out uint v) == 0 ? v : null;
    public void Dispose()
    {
        if (_initialized) { try { _shutdown(); } catch { } _initialized = false; }
        if (_dll != 0) NativeLibrary.Free(_dll);
    }
}

// One user control: measured combined target power in W, 0 = no compute.
// GPU launches successfully before CPU FFT workers are ever created.
// NVML/LHM readings control an approximate feedback loop; no sensor values are invented.
internal sealed class BurnEngine : IDisposable
{
    public const int MaxTargetWatts = 420;
    private readonly SensorReader _sensors;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _gpuTask, _controlTask;
    private Task? _cpuTask;
    private int _target, _gpuOnline, _cpuStarted, _offEpoch, _gpuSamples, _gpuOnlyPinned;
    private float _gpuDuty, _cpuDuty, _gpuPeak, _cpuBaseline = -1;
    // Observed minimum while one or more OpenCL workgroups remain active.
    // A lower requested target cannot be achieved with continuously active compute.
    private float _gpuActiveFloor = -1;
    private double _threshold = -1;
    private string _status = "OFF";
    // Lock the exact intent of the CPU marker, not a watt value that can go stale.
    // The target/readout follows a newly measured GPU ceiling while the lock stays active.
    public int Target => GpuOnlyPinned ? Math.Clamp((int)Math.Round(CpuEngageWatts), 1, MaxTargetWatts) : Volatile.Read(ref _target);
    public bool GpuOnlyPinned => Volatile.Read(ref _gpuOnlyPinned) != 0;
    public bool GpuReady => Volatile.Read(ref _gpuOnline) != 0;
    public bool CpuEngaged => Volatile.Read(ref _cpuDuty) > .01f && Target > 0 && GpuReady;
    // The marker exists immediately. Until full-load samples are available it is
    // an ESTIMATE based on the NVML driver power limit, never presented as sensor power.
    public double CpuEngageWatts
    {
        get
        {
            double learned = Volatile.Read(ref _threshold);
            if (learned > 0) return Math.Clamp(learned, 40, MaxTargetWatts - 10);
            double limit = _sensors.Latest.GpuLimitWatts ?? 285.0;
            return Math.Clamp(limit + Math.Max(0, Volatile.Read(ref _cpuBaseline)), 40, MaxTargetWatts - 10);
        }
    }
    public bool CpuEngageMeasured => Volatile.Read(ref _threshold) > 0;

    public string Status => Volatile.Read(ref _status);
    private void State(string s) => Volatile.Write(ref _status, s);
    public BurnEngine(SensorReader sensors)
    {
        _sensors = sensors;
        _gpuTask = Task.Factory.StartNew(() => GpuLoop(_stop.Token), _stop.Token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _controlTask = Task.Factory.StartNew(() => ControlLoop(_stop.Token), _stop.Token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public void SetTarget(int watts)
    {
        int requested = Math.Clamp(watts, 0, MaxTargetWatts);
        Volatile.Write(ref _gpuOnlyPinned, 0);
        Volatile.Write(ref _target, requested);
        if (requested == 0)
        {
            Volatile.Write(ref _cpuDuty, 0);
            Volatile.Write(ref _gpuDuty, 0);
            Interlocked.Increment(ref _offEpoch);
            State("OFF - NO COMPUTE WORK");
        }
        else if (!GpuReady && !Status.StartsWith("GPU ERROR", StringComparison.Ordinal))
            State("STARTING NVIDIA GPU...");
    }
    // Snap mode is independent of the moving threshold and ordinary feedback target.
    // A full GPU workload is requested; CPU FFT workers remain parked.
    public void SetGpuOnlyPin()
    {
        Volatile.Write(ref _target, Math.Clamp((int)Math.Round(CpuEngageWatts), 1, MaxTargetWatts));
        Volatile.Write(ref _gpuOnlyPinned, 1);
        State(GpuReady ? "GPU MAX / CPU OFF - LOCKED" : "STARTING NVIDIA GPU - LOCKED");
    }

    private void CpuStart(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _cpuStarted, 1) != 0) return;
        if (!Avx.IsSupported || !Fma.IsSupported)
        { State("CPU AVX/FMA NOT SUPPORTED"); return; }
        Task[] workers = Enumerable.Range(0, Environment.ProcessorCount)
            .Select(i => Task.Factory.StartNew(() => CpuLoop(i, token), token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        _cpuTask = Task.WhenAll(workers);
    }
    private void GpuLoop(CancellationToken token)
    {
        OpenClCompute? gpu = null;
        bool blocked = false;
        int blockedEpoch = 0;
        long zeroEffortSince = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (Target == 0)
                {
                    Volatile.Write(ref _gpuOnline, 0);
                    if (gpu != null) { gpu.Dispose(); gpu = null; }
                    blocked = false;
                    token.WaitHandle.WaitOne(15);
                    continue;
                }
                if (blocked)
                {
                    if (Volatile.Read(ref _offEpoch) == blockedEpoch)
                    { token.WaitHandle.WaitOne(35); continue; }
                    blocked = false;
                }
                try
                {
                    float duty = Volatile.Read(ref _gpuDuty);
                    // At low requested power the controller is allowed to command ZERO
                    // GPU work. Keeping an OpenCL context and dispatching one minimum
                    // workgroup permanently can hold the card at its active-power floor.
                    if (GpuReady && duty <= 0f)
                    {
                        if (zeroEffortSince == 0) zeroEffortSince = Environment.TickCount64;
                        // Release the compute context so NVIDIA can return to idle clocks.
                        if (gpu != null && Environment.TickCount64 - zeroEffortSince >= 900)
                        { gpu.Dispose(); gpu = null; }
                        token.WaitHandle.WaitOne(80);
                        continue;
                    }
                    zeroEffortSince = 0;
                    if (gpu == null)
                    {
                        State("INITIALIZING NVIDIA OPENCL...");
                        gpu = new OpenClCompute();
                        // The GPU completes genuine compute before CPU FFT threads exist.
                        // Keep the startup probe minimal for low-watt targets.
                        gpu.Execute(token, .005f);
                        Volatile.Write(ref _gpuOnline, 1);
                        CpuStart(token);
                        State("GPU ONLINE - STABILIZING POWER");
                    }
                    if (duty <= 0f) { token.WaitHandle.WaitOne(40); continue; }
                    // Above the minimum active power, dispatches remain continuous.
                    gpu.Execute(token, duty);
                    // Keep GPU occupancy roughly proportional to duty; avoid long
                    // sleep/wake bursts that showed up as jagged NVML power readings.
                    // No forced idle/sleep at partial load: always feed real
                    // compute blocks at the reduced occupancy level.
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Volatile.Write(ref _gpuOnline, 0);
                    Volatile.Write(ref _gpuDuty, 0);
                    Volatile.Write(ref _cpuDuty, 0);
                    State("GPU ERROR: " + ex.GetBaseException().Message.Split('\n')[0]);
                    blocked = true;
                    blockedEpoch = Volatile.Read(ref _offEpoch);
                    try { gpu?.Dispose(); } catch { }
                    gpu = null;
                }
            }
        }
        finally { Volatile.Write(ref _gpuOnline, 0); gpu?.Dispose(); }
    }
    // Conservative closed-loop regulator. NVML power is usually updated more slowly
    // than our sensor polling, so never chase every 350 ms read as if it were fresh.
    // Make a bounded change, allow the clocks/telemetry to settle, then remeasure.
    private void ControlLoop(CancellationToken token)
    {
        double filtered = double.NaN;
        int lastTarget = 0, fullTicks = 0, lowSamples = 0;
        bool cpuPhase = false, parkedBelowFloor = false;
        long lastAdjustmentMs = 0, lastTargetChangeMs = 0;
        int lastErrorSign = 0;

        while (!token.IsCancellationRequested)
        {
            int target = Target;
            Sensors s = _sensors.Latest;
            bool fresh = s.WhenUtc != default && DateTime.UtcNow - s.WhenUtc < TimeSpan.FromSeconds(2);
            bool combinedAvailable = fresh && s.CombinedWatts.HasValue;
            bool gpuAvailable = fresh && s.GpuWatts.HasValue;
            double? observed = combinedAvailable ? s.CombinedWatts : (gpuAvailable ? s.GpuWatts : null);
            // With missing CPU telemetry, NVML is an honest GPU-only measurement.
            // Subtract a known idle CPU baseline when available; never invent one.
            bool gpuOnlySensor = !combinedAvailable && gpuAvailable;
            double baseline = Math.Max(0, Volatile.Read(ref _cpuBaseline));
            double requestedMeasurement = gpuOnlySensor ? Math.Max(0, target - baseline) : target;
            long now = Environment.TickCount64;

            if (target == 0 || !GpuReady)
            {
                Volatile.Write(ref _gpuDuty, 0);
                Volatile.Write(ref _cpuDuty, 0);
                filtered = double.NaN;
                fullTicks = 0;
                lowSamples = 0;
                cpuPhase = false;
                parkedBelowFloor = false;
                lastErrorSign = 0;
                lastTarget = target;
                lastAdjustmentMs = now;
                token.WaitHandle.WaitOne(target == 0 ? 400 : 150);
                continue;
            }

            float gpuDuty = Volatile.Read(ref _gpuDuty);
            float cpuDuty = Volatile.Read(ref _cpuDuty);
            if (target != lastTarget)
            {
                // If the user moves the slider, old filtered watts are not a
                // valid response to the new setpoint. Keep the existing effort.
                lastTarget = target;
                lastTargetChangeMs = now;
                lastAdjustmentMs = now;
                filtered = double.NaN;
                lastErrorSign = 0;
                parkedBelowFloor = false;
                if (gpuDuty < .004f) gpuDuty = .075f;
            }

            // GPU-only pin bypasses all feedback, including an obsolete watt marker.
            if (GpuOnlyPinned)
            {
                Volatile.Write(ref _gpuDuty, 1f);
                Volatile.Write(ref _cpuDuty, 0f);
                cpuPhase = false;
                State("GPU MAX / CPU OFF - LOCKED");
                token.WaitHandle.WaitOne(900);
                continue;
            }

            if (combinedAvailable && s.CpuWatts is double idleCpu && cpuDuty < .002f)
            {
                float previous = Volatile.Read(ref _cpuBaseline);
                Volatile.Write(ref _cpuBaseline,
                    previous < 0 ? (float)idleCpu : .98f * previous + .02f * (float)idleCpu);
            }
            if (observed.HasValue)
            {
                // Once per control tick, not once for each duplicate NVML value.
                filtered = double.IsNaN(filtered) ? observed.Value : .64 * filtered + .36 * observed.Value;
            }
            else filtered = double.NaN;

            // Learn the GPU capacity only after consecutive full-load samples.
            if (gpuDuty >= .985f && cpuDuty < .002f && gpuAvailable && s.GpuWatts is double gp)
            {
                fullTicks++;
                if (fullTicks >= 7)
                {
                    float previous = Volatile.Read(ref _gpuPeak);
                    Volatile.Write(ref _gpuPeak, Math.Max(previous * .999f, (float)gp));
                    Interlocked.Increment(ref _gpuSamples);
                    double ceiling = Volatile.Read(ref _gpuPeak) + Math.Max(0, Volatile.Read(ref _cpuBaseline));
                    double old = Volatile.Read(ref _threshold);
                    Volatile.Write(ref _threshold,
                        Math.Clamp(old <= 0 ? ceiling : .96 * old + .04 * ceiling, 1, MaxTargetWatts));
                }
            }
            else if (gpuDuty < .975f || cpuDuty > .002f) fullTicks = 0;

            // A continuously executing OpenCL kernel has a real minimum active
            // board power. Below it, GPU compute must remain OFF (no forced PWM).
            if (gpuDuty >= .003f && gpuDuty <= .028f && cpuDuty < .002f && gpuAvailable
                && s.GpuWatts is double lowGp && lowGp > 0)
            {
                lowSamples++;
                if (lowSamples >= 6)
                {
                    float old = Volatile.Read(ref _gpuActiveFloor);
                    Volatile.Write(ref _gpuActiveFloor, old < 0 ? (float)lowGp : Math.Min(old, (float)lowGp));
                }
            }
            else lowSamples = 0;
            float activeFloor = Volatile.Read(ref _gpuActiveFloor);
            double combinedFloor = activeFloor + baseline;
            if (activeFloor > 0 && (target < combinedFloor - 5 ||
                (parkedBelowFloor && target < combinedFloor + 10)))
            {
                parkedBelowFloor = true;
                cpuPhase = false;
                Volatile.Write(ref _gpuDuty, 0);
                Volatile.Write(ref _cpuDuty, 0);
                State($"TARGET BELOW CONTINUOUS GPU FLOOR ({combinedFloor:0}W) - IDLE");
                token.WaitHandle.WaitOne(950);
                continue;
            }
            if (parkedBelowFloor)
            {
                parkedBelowFloor = false;
                filtered = double.NaN;
                gpuDuty = .04f;
                cpuDuty = 0;
                lastAdjustmentMs = now;
            }

            if (double.IsNaN(filtered))
            {
                // Never push blindly toward full load if a power sensor disappears.
                Volatile.Write(ref _gpuDuty, Math.Clamp(gpuDuty, .02f, 1f));
                Volatile.Write(ref _cpuDuty, 0);
                State("POWER SENSOR UNAVAILABLE - HOLDING GPU LOAD");
                token.WaitHandle.WaitOne(950);
                continue;
            }

            double gate = Volatile.Read(ref _threshold);
            bool gpuAtCapacity = gpuDuty >= .985f && fullTicks >= 8;
            if (!cpuPhase && combinedAvailable && gpuAtCapacity &&
                gate > 0 && target > gate + 8 && target > filtered + 8)
                cpuPhase = true;
            if (cpuPhase && (!combinedAvailable || (gate > 0 && target < gate - 12)))
                cpuPhase = false;

            if (!cpuPhase && cpuDuty > .001f)
            {
                // Drop CPU first when returning to GPU-only range; avoid
                // simultaneously varying both workloads in opposite directions.
                cpuDuty = Math.Max(0, cpuDuty - .14f);
                Volatile.Write(ref _gpuDuty, 1f);
                Volatile.Write(ref _cpuDuty, cpuDuty);
                State("REDUCING CPU BEFORE GPU");
                token.WaitHandle.WaitOne(950);
                continue;
            }

            double error = requestedMeasurement - filtered;
            double deadband = cpuPhase ? 6.0 : 4.5;
            // Hold for at least two sensor polling cycles after changing effort.
            bool settled = now - lastAdjustmentMs >= 1900 && now - lastTargetChangeMs >= 650;
            if (settled && Math.Abs(error) > deadband)
            {
                double maxStep = cpuPhase ? .026 : .045;
                double correction = Math.Clamp(error * (cpuPhase ? .00085 : .00095), -maxStep, maxStep);
                int errorSign = Math.Sign(error);
                // Overshoot reversal -> smaller response, not a pendulum swing.
                if (lastErrorSign != 0 && errorSign != lastErrorSign) correction *= .45;
                lastErrorSign = errorSign;
                if (cpuPhase)
                {
                    gpuDuty = 1f;
                    cpuDuty = Math.Clamp(cpuDuty + (float)correction, 0f, 1f);
                }
                else
                    gpuDuty = Math.Clamp(gpuDuty + (float)correction, 0f, 1f);
                lastAdjustmentMs = now;
            }

            Volatile.Write(ref _gpuDuty, gpuDuty);
            Volatile.Write(ref _cpuDuty, cpuPhase ? cpuDuty : 0f);
            State(cpuPhase ? (Math.Abs(error) <= deadband ? "GPU + CPU / HOLDING TARGET" :
                    "GPU + CPU / SETTLING") :
                gpuOnlySensor && gpuDuty >= .995f && error > deadband ?
                    "GPU AT CAPACITY / CPU POWER N/A" :
                Math.Abs(error) <= deadband ? "GPU / HOLDING TARGET" :
                gpuOnlySensor ? "GPU / REGULATING (CPU SENSOR N/A)" : "GPU / REGULATING");
            token.WaitHandle.WaitOne(950);
        }
    }
    private static float _sink;
    private void CpuLoop(int index, CancellationToken token)
    {
        // 4096-point FFT, eight parallel transforms packed into AVX vectors.
        // The same cache-sized arrays are used for each forward/inverse cycle.
        const int n = 4096;
        var real = new Vector256<float>[n];
        var imag = new Vector256<float>[n];
        for (int i = 0; i < n; i++)
        {
            real[i] = Vector256.Create((float)(0.3 * Math.Sin((i + 1) * (index + 1) * 0.017)));
            imag[i] = Vector256.Create((float)(0.3 * Math.Cos((i + 1) * (index + 1) * 0.019)));
        }
        int levels = 12;
        var cos = new Vector256<float>[levels][];
        var sin = new Vector256<float>[levels][];
        for (int stage = 0, len = 2; stage < levels; stage++, len <<= 1)
        {
            int half = len >> 1;
            cos[stage] = new Vector256<float>[half];
            sin[stage] = new Vector256<float>[half];
            for (int j = 0; j < half; j++)
            {
                double angle = 2 * Math.PI * j / len;
                cos[stage][j] = Vector256.Create((float)Math.Cos(angle));
                sin[stage][j] = Vector256.Create((float)Math.Sin(angle));
            }
        }
        var scale = Vector256.Create(1f / n);
        while (!token.IsCancellationRequested)
        {
            // Smooth CPU power by activating a fraction of cores continuously,
            // rather than pulsing every core in synchronization.
            float share = Math.Clamp(Volatile.Read(ref _cpuDuty) * Environment.ProcessorCount - index, 0f, 1f);
            if (Target == 0 || share < .002f || Volatile.Read(ref _gpuOnline) == 0)
            {
                token.WaitHandle.WaitOne(20);
                continue;
            }
            long ticks = Stopwatch.GetTimestamp();
            // Both directions are actual decimation-in-time FFTs with bit reversal.
            for (int pass = 0; pass < 2; pass++)
            {
                int reversed = 0;
                for (int i = 1; i < n; i++)
                {
                    int bit = n >> 1;
                    while ((reversed & bit) != 0) { reversed ^= bit; bit >>= 1; }
                    reversed ^= bit;
                    if (i < reversed)
                    {
                        (real[i], real[reversed]) = (real[reversed], real[i]);
                        (imag[i], imag[reversed]) = (imag[reversed], imag[i]);
                    }
                }
                for (int stage = 0, len = 2; stage < levels; stage++, len <<= 1)
                {
                    int half = len >> 1;
                    for (int block = 0; block < n; block += len)
                    {
                        for (int j = 0; j < half; j++)
                        {
                            int a = block + j, b = a + half;
                            var cr = cos[stage][j];
                            var ci = pass == 0 ? Avx.Subtract(Vector256<float>.Zero, sin[stage][j]) : sin[stage][j];
                            var tr = Fma.MultiplySubtract(cr, real[b], Avx.Multiply(ci, imag[b]));
                            var ti = Fma.MultiplyAdd(cr, imag[b], Avx.Multiply(ci, real[b]));
                            var ar = real[a]; var ai = imag[a];
                            real[a] = Avx.Add(ar, tr); imag[a] = Avx.Add(ai, ti);
                            real[b] = Avx.Subtract(ar, tr); imag[b] = Avx.Subtract(ai, ti);
                        }
                    }
                }
                if (pass == 1)
                    for (int i = 0; i < n; i++)
                    { real[i] = Avx.Multiply(real[i], scale); imag[i] = Avx.Multiply(imag[i], scale); }
                if (token.IsCancellationRequested) break;
            }
            Volatile.Write(ref _sink, real[index & (n - 1)].GetElement(0));
            Throttle(token, Stopwatch.GetElapsedTime(ticks).TotalMilliseconds, share);
        }
    }
    private static void Throttle(CancellationToken token, double workMs, float duty)
    {
        if (duty >= .995f) return;
        if (duty <= .003f) { token.WaitHandle.WaitOne(12); return; }
        // A short batch followed by a short cancellable wait; fractional duty is approximate.
        double idleMs = Math.Clamp(workMs * (1.0 - duty) / duty, 0, 90);
        if (idleMs >= 1) token.WaitHandle.WaitOne((int)Math.Round(idleMs));
        else if (idleMs > .1) Thread.Yield();
    }
    public void Dispose()
    {
        SetTarget(0);
        _stop.Cancel();
        try { _gpuTask.Wait(TimeSpan.FromSeconds(4)); } catch { }
        try { _controlTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _cpuTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _stop.Dispose();
    }
}
// OpenCL 1.2 compute backend; uses the installed NVIDIA driver without bundling driver DLLs.
internal sealed class OpenClCompute : IDisposable
{
    private nint _context, _queue, _program, _kernel, _buffer;
    private const ulong DeviceTypeGpu = 4, MemReadWrite = 1;
    private const int MaxGlobalThreads = 262144;
    private int _computeUnits = 1;
    private int _maxGroups = MaxGlobalThreads / 256;
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int clGetPlatformIDs(uint count, [Out] nint[]? platforms, out uint actual);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int clGetDeviceIDs(nint platform, ulong deviceType, uint count, [Out] nint[]? devices, out uint actual);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int clGetDeviceInfo(nint device, uint name, nuint size, [Out] byte[]? data, out nuint written);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint clCreateContext(nint props, uint numDevices, [In] nint[] devices, nint callback, nint user, out int error);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint clCreateCommandQueue(nint context, nint device, ulong props, out int error);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint clCreateProgramWithSource(nint context, uint count, [In] nint[] strings, nint lengths, out int error);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int clBuildProgram(nint program, uint numDevices, [In] nint[] devices, [MarshalAs(UnmanagedType.LPStr)] string options, nint callback, nint user);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int clGetProgramBuildInfo(nint program, nint device, uint param, nuint size, [Out] byte[]? data, out nuint written);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint clCreateKernel(nint program, [MarshalAs(UnmanagedType.LPStr)] string name, out int error);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint clCreateBuffer(nint context, ulong flags, nuint size, nint host, out int error);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int clSetKernelArg(nint kernel, uint index, nuint size, ref nint buffer);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "clSetKernelArg")]
    private static extern int clSetKernelArgUInt(nint kernel, uint index, nuint size, ref uint value);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int clEnqueueNDRangeKernel(nint queue, nint kernel, uint dimensions, nint offset, [In] nuint[] global, nint local, uint events, nint wait, nint result);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int clFinish(nint queue);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int clReleaseMemObject(nint obj);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int clReleaseKernel(nint obj);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int clReleaseProgram(nint obj);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int clReleaseCommandQueue(nint obj);
    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int clReleaseContext(nint obj);
    private static void Ok(int error, string operation)
    { if (error != 0) throw new InvalidOperationException($"{operation} ({error})"); }
    private static string DeviceString(nint device, uint name)
    {
        if (clGetDeviceInfo(device, name, 0, null, out nuint bytes) != 0 || bytes == 0 || bytes > 1024) return "";
        byte[] data = new byte[(int)bytes];
        if (clGetDeviceInfo(device, name, bytes, data, out _) != 0) return "";
        return System.Text.Encoding.UTF8.GetString(data).TrimEnd('\0');
    }
    public OpenClCompute()
    {
        try
        {
            Ok(clGetPlatformIDs(0, null, out uint count), "OpenCL platform list");
            if (count == 0) throw new InvalidOperationException("No OpenCL platforms");
            var platforms = new nint[count];
            Ok(clGetPlatformIDs(count, platforms, out _), "OpenCL platforms");
            nint device = 0;
            foreach (var platform in platforms)
            {
                int status = clGetDeviceIDs(platform, DeviceTypeGpu, 0, null, out uint number);
                if (status != 0 || number == 0) continue;
                var devices = new nint[number];
                if (clGetDeviceIDs(platform, DeviceTypeGpu, number, devices, out _) != 0) continue;
                foreach (nint candidate in devices)
                    if (DeviceString(candidate, 0x102C).Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    { device = candidate; break; }
                if (device != 0) break;
            }
            if (device == 0) throw new InvalidOperationException("No NVIDIA OpenCL GPU found");
            var selected = new[] { device };
            // CL_DEVICE_MAX_COMPUTE_UNITS lets us scale resident GPU workgroups.
            // 50% effort should not dispatch hundreds of groups that instantly saturate every SM.
            var units = new byte[4];
            if (clGetDeviceInfo(device, 0x1002, (nuint)units.Length, units, out _) == 0)
            {
                int reported = BitConverter.ToInt32(units, 0);
                if (reported is >= 1 and <= 512) _computeUnits = reported;
            }
            _maxGroups = Math.Min(MaxGlobalThreads / 256, Math.Max(32, _computeUnits * 16));
            _context = clCreateContext(0, 1, selected, 0, 0, out int err);
            Ok(err, "Create OpenCL context");
            _queue = clCreateCommandQueue(_context, device, 0, out err);
            Ok(err, "Create OpenCL command queue");
            nint source = Marshal.StringToHGlobalAnsi(KernelSource);
            try { _program = clCreateProgramWithSource(_context, 1, new[] { source }, 0, out err); }
            finally { Marshal.FreeHGlobal(source); }
            Ok(err, "Create OpenCL program");
            int built = clBuildProgram(_program, 1, selected, "", 0, 0);
            if (built != 0)
            {
                string msg = $"OpenCL kernel build ({built})";
                if (clGetProgramBuildInfo(_program, device, 0x1183, 0, null, out nuint size) == 0 && size is > 0 and < 4096)
                {
                    byte[] log = new byte[(int)size];
                    if (clGetProgramBuildInfo(_program, device, 0x1183, size, log, out _) == 0)
                        msg += ": " + System.Text.Encoding.UTF8.GetString(log).TrimEnd('\0').Trim();
                }
                throw new InvalidOperationException(msg);
            }
            _kernel = clCreateKernel(_program, "burn", out err);
            Ok(err, "Create OpenCL kernel");
            _buffer = clCreateBuffer(_context, MemReadWrite, (nuint)(MaxGlobalThreads * 16), 0, out err);
            Ok(err, "Create OpenCL buffer");
            Ok(clSetKernelArg(_kernel, 0, (nuint)IntPtr.Size, ref _buffer), "Bind OpenCL output buffer");
        }
        catch { Dispose(); throw; }
    }
    public void Execute(CancellationToken token, float effort)
    {
        token.ThrowIfCancellationRequested();
        // Occupancy-based partial loading: dispatch enough long-running blocks
        // to occupy only some compute units at low effort. This is not PWM.
        // More than ~1 block/SM can fully saturate a GPU, hence this mapping
        // reserves >2 blocks/SM for the very top end of the slider.
        double demand = Math.Clamp((double)effort, .001, 1);
        double blocksPerUnit = demand <= .70
            ? .025 + (demand / .70) * .975
            : 1.0 + ((demand - .70) / .30) * 15.0;
        int groups = Math.Clamp((int)Math.Round(blocksPerUnit * _computeUnits), 1, _maxGroups);
        nuint[] size = { (nuint)(groups * 256) };
        // Keep per-workgroup math and batch size constant across slider levels.
        // Only workgroup occupancy changes; no discontinuous iteration/launch
        // thresholds which previously created large wattage steps at 30%/75%.
        uint iterations = 8192;
        Ok(clSetKernelArgUInt(_kernel, 1, (nuint)sizeof(uint), ref iterations), "Set GPU iteration count");
        const int launches = 6;
        for (int i = 0; i < launches; i++)
        {
            token.ThrowIfCancellationRequested();
            Ok(clEnqueueNDRangeKernel(_queue, _kernel, 1, 0, size, 0, 0, 0, 0), "GPU enqueue");
        }
        Ok(clFinish(_queue), "GPU batch finish");
    }
    public void Dispose()
    {
        if (_buffer != 0) { clReleaseMemObject(_buffer); _buffer = 0; }
        if (_kernel != 0) { clReleaseKernel(_kernel); _kernel = 0; }
        if (_program != 0) { clReleaseProgram(_program); _program = 0; }
        if (_queue != 0) { clReleaseCommandQueue(_queue); _queue = 0; }
        if (_context != 0) { clReleaseContext(_context); _context = 0; }
    }
    private const string KernelSource = """
    __kernel void burn(__global float4* output, uint iterations)
    {
        const uint gid = get_global_id(0);
        float seed = (float)(gid + 1) * 0.0000001f;
        float4 a = (float4)(0.11f,0.17f,0.23f,0.29f) + seed;
        float4 b = (float4)(0.31f,0.37f,0.41f,0.43f) + seed;
        float4 c = (float4)(0.47f,0.53f,0.59f,0.61f) + seed;
        float4 d = (float4)(0.67f,0.71f,0.73f,0.79f) + seed;
        for (uint k = 0; k < iterations; ++k)
        {
            a = fma(a, (float4)(0.813f), b * (float4)(0.181f)) + (float4)(0.00001f);
            b = fma(b, (float4)(0.837f), c * (float4)(0.159f)) + (float4)(0.00001f);
            c = fma(c, (float4)(0.857f), d * (float4)(0.137f)) + (float4)(0.00001f);
            d = fma(d, (float4)(0.829f), a * (float4)(0.167f)) + (float4)(0.00001f);
        }
        output[gid] = a + b + c + d;
    }
    """;
}

// Static illustrated UI: flat colors, crisp outlines and clean typography.
// Tall borderless UI: two rear orb panels and a foreground rounded column.
// The rounded column's top cap is an interactive exit control.
internal sealed class BurnForm : Form
{
    private readonly SensorReader _sensors;
    private readonly BurnEngine _engine;
    private readonly Timer _timer;
    private readonly Font _title = new("Segoe UI", 27, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _big = new("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _normal = new("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _small = new("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _tiny = new("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Color Paper = Color.FromArgb(250, 245, 234);
    private static readonly Color GPUColor = Color.FromArgb(252, 165, 121);
    private static readonly Color CPUColor = Color.FromArgb(187, 169, 245);
    private static readonly Color ShaftColor = Color.FromArgb(251, 219, 190);
    private const int W = 740, H = 920;
    private const float TrackX = 506f, TrackTop = 363f, TrackBottom = 619f;
    private const int SnapDistance = 13;
    private int _targetWatts;
    private bool _dragSlider, _hoverTip;
    private float _visibleGpu, _visibleCpu, _visibleTotal;

    public BurnForm()
    {
        Text = "StressDeez — GPU-first power target";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(W, H);
        MinimumSize = MaximumSize = Size;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Paper;
        DoubleBuffered = true;
        KeyPreview = true;
        ShowInTaskbar = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        using (var shape = WindowPath()) Region = new Region(shape);
        try
        {
            System.Drawing.Icon? appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (appIcon != null) Icon = appIcon;
        }
        catch { /* The assembly icon is still embedded in the executable. */ }
        _sensors = new SensorReader();
        _engine = new BurnEngine(_sensors);
        // Polling redraw only: no wobble, particles, pulses or bounce effects.
        _timer = new Timer { Interval = 350 };
        _timer.Tick += (_, _) =>
        {
            Sensors sensor = _sensors.Latest;
            if (IsFresh(sensor))
            {
                if (sensor.GpuWatts.HasValue) _visibleGpu = (float)sensor.GpuWatts.Value;
                if (sensor.CpuWatts.HasValue) _visibleCpu = (float)sensor.CpuWatts.Value;
                if (sensor.CombinedWatts.HasValue) _visibleTotal = (float)sensor.CombinedWatts.Value;
            }
            Invalidate();
        };
        _timer.Start();
    }

    private static bool IsFresh(Sensors s) => s.WhenUtc != default && (DateTime.UtcNow - s.WhenUtc).TotalSeconds < 3;
    private static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float diameter = rad * 2;
        p.AddArc(r.X, r.Y, diameter, diameter, 180, 90);
        p.AddArc(r.Right - diameter, r.Y, diameter, diameter, 270, 90);
        p.AddArc(r.Right - diameter, r.Bottom - diameter, diameter, diameter, 0, 90);
        p.AddArc(r.X, r.Bottom - diameter, diameter, diameter, 90, 90);
        p.CloseFigure();
        return p;
    }
    private static GraphicsPath WindowPath()
    {
        var path = new GraphicsPath(FillMode.Winding);
        path.AddEllipse(20, 645, 340, 269);
        path.AddEllipse(380, 645, 340, 269);
        using (var column = Rounded(new RectangleF(180, 40, 380, 701), 174)) path.AddPath(column, false);
        return path;
    }
    private static void TextAt(Graphics g, string text, Font font, Color color, RectangleF r,
        StringAlignment align = StringAlignment.Center)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = align, LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(text, font, brush, r, format);
    }
    private static string F(double? n, string fmt, string suffix) => n.HasValue ? n.Value.ToString(fmt) + suffix : "N/A";
    private static RectangleF TipRect() => new(265f, 54f, 210f, 121f);
    private static bool TipContains(Point p) =>
        new Rectangle(260, 48, 221, 139).Contains(p);
    private static bool SliderContains(Point p) =>
        p.X >= 484 && p.X <= 555 && p.Y >= TrackTop - 22 && p.Y <= TrackBottom + 19;
    private float MarkerY() => TrackBottom - (float)(_engine.CpuEngageWatts / BurnEngine.MaxTargetWatts) * (TrackBottom - TrackTop);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        // Crisp flat illustration with anti-aliased outlines (no animated motion).
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Paper);
        // Two gauges are behind and below the central column, with their data unoccluded.
        DrawOrb(g, new RectangleF(20, 645, 340, 269), true);
        DrawOrb(g, new RectangleF(380, 645, 340, 269), false);
        DrawColumn(g);
        DrawSlider(g);
        DrawTip(g);
    }
    private void DrawOrb(Graphics g, RectangleF bounds, bool gpu)
    {
        var s = _sensors.Latest;
        bool fresh = IsFresh(s);
        Color accent = gpu ? GPUColor : CPUColor;
        double? power = fresh ? (gpu ? s.GpuWatts : s.CpuWatts) : null;
        double? temp = fresh ? (gpu ? s.GpuTemp : s.CpuTemp) : null;
        double? util = fresh ? (gpu ? s.GpuLoad : s.CpuLoad) : null;
        float visible = gpu ? _visibleGpu : _visibleCpu;
        using (var brush = new SolidBrush(accent)) g.FillEllipse(brush, bounds);
        using (var pen = new Pen(Color.FromArgb(67, 49, 69), 4)) g.DrawEllipse(pen, bounds);
        float cx = bounds.X + bounds.Width / 2;
        TextAt(g, gpu ? "GPU  •  BOARD POWER" : "CPU  •  PACKAGE POWER", _normal, Color.Black,
            new RectangleF(cx - 97, 747, 194, 28));
        var meter = new RectangleF(cx - 63, 784, 126, 91);
        using (var brush = new SolidBrush(gpu ? Color.FromArgb(255, 218, 125) : Color.FromArgb(232, 210, 255)))
            g.FillEllipse(brush, meter);
        using (var pen = new Pen(Color.Black, 4)) g.DrawEllipse(pen, meter);
        TextAt(g, power.HasValue ? $"{visible:0} W" : "N/A", _big, Color.Black,
            new RectangleF(cx - 70, 795, 140, 44));
        TextAt(g, $"{F(temp, "0", "°C")}  {F(util, "0", "%")}", _tiny, Color.Black,
            new RectangleF(cx - 100, 875, 200, 22));
    }
    private void DrawColumn(Graphics g)
    {
        using var shape = Rounded(new RectangleF(180, 40, 380, 701), 174);
        using (var brush = new SolidBrush(ShaftColor)) g.FillPath(brush, shape);
        using (var pen = new Pen(Color.FromArgb(67, 49, 69), 5)) g.DrawPath(pen, shape);
        TextAt(g, "STRESSDEEZ", _title, Color.FromArgb(56, 42, 60), new RectangleF(213, 184, 319, 43));
        TextAt(g, "you cant get a photograph pregnant", _small, Color.FromArgb(92, 57, 91),
            new RectangleF(207, 228, 327, 30));
        Sensors s = _sensors.Latest;
        bool fresh = IsFresh(s);
        TextAt(g, fresh && s.CombinedWatts.HasValue ? $"{_visibleTotal:0.0} W" : "N/A W", _big, Color.Black,
            new RectangleF(210, 272, 319, 44));
        TextAt(g, "MEASURED GPU BOARD + CPU PACKAGE", _tiny, Color.FromArgb(87, 67, 83), new RectangleF(215, 309, 310, 21));
        TextAt(g, _engine.GpuOnlyPinned ? "GPU MAX   CPU OFF" : $"TARGET {_targetWatts} W",
            _small, _engine.GpuOnlyPinned ? Color.DarkBlue : Color.Black,
            new RectangleF(220, 343, 266, 30));
        DrawTempCard(g, new RectangleF(218, 393, 150, 75), "GPU TEMP", fresh ? s.GpuTemp : null);
        DrawTempCard(g, new RectangleF(218, 480, 150, 75), "CPU TEMP", fresh ? s.CpuTemp : null);
        TextAt(g, fresh && s.CpuWatts.HasValue ? $"CPU POWER {s.CpuWatts.Value:0} W" : "CPU POWER N/A",
            _tiny, Color.Black, new RectangleF(216, 567, 253, 26), StringAlignment.Near);
        TextAt(g, s.GpuLimitWatts.HasValue ? $"GPU LIMIT {s.GpuLimitWatts.Value:0} W" : "GPU LIMIT N/A",
            _tiny, Color.Black, new RectangleF(216, 595, 255, 26), StringAlignment.Near);
        TextAt(g, _engine.GpuOnlyPinned ? "PINNED TO CPU LINE" :
            _engine.CpuEngageMeasured ? "CPU LINE: MEASURED" : "CPU LINE: ESTIMATE",
            _tiny, Color.DarkBlue, new RectangleF(215, 627, 276, 24), StringAlignment.Near);
        TextAt(g, _engine.Status, _tiny, Color.Black, new RectangleF(211, 690, 323, 26));
    }
    private void DrawTempCard(Graphics g, RectangleF bounds, string label, double? reading)
    {
        using (var brush = new SolidBrush(Color.White)) g.FillRectangle(brush, bounds);
        using (var pen = new Pen(Color.Black, 3)) g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        TextAt(g, label, _tiny, Color.Black,
            new RectangleF(bounds.X + 6, bounds.Y + 4, bounds.Width - 12, 20));
        TextAt(g, reading.HasValue ? $"{reading.Value:0}°C" : "N/A", _big, Color.Black,
            new RectangleF(bounds.X + 5, bounds.Y + 25, bounds.Width - 10, 42));
    }
    private void DrawSlider(Graphics g)
    {
        float height = TrackBottom - TrackTop;
        float gateY = MarkerY();
        float knobY = _engine.GpuOnlyPinned ? gateY :
            TrackBottom - _targetWatts * height / BurnEngine.MaxTargetWatts;
        using (var brush = new SolidBrush(Color.White))
            g.FillRectangle(brush, TrackX - 9, TrackTop - 8, 18, height + 16);
        using (var pen = new Pen(Color.Black, 3))
            g.DrawRectangle(pen, TrackX - 9, TrackTop - 8, 18, height + 16);
        int target = _engine.GpuOnlyPinned ? _engine.Target : _targetWatts;
        if (target > 0)
        {
            float gpuStart = Math.Max(knobY, gateY);
            if (gpuStart < TrackBottom)
            {
                using var brush = new SolidBrush(GPUColor);
                g.FillRectangle(brush, TrackX - 6, gpuStart, 12, TrackBottom - gpuStart);
            }
            if (knobY < gateY && !_engine.GpuOnlyPinned)
            {
                using var brush = new SolidBrush(CPUColor);
                g.FillRectangle(brush, TrackX - 6, knobY, 12, gateY - knobY);
            }
        }
        // Marker is ALWAYS visible, even before NVML supplies an estimated limit.
        using (var pen = new Pen(Color.Blue, _engine.GpuOnlyPinned ? 5f : 3f))
        {
            if (!_engine.CpuEngageMeasured) pen.DashStyle = DashStyle.Dash;
            g.DrawLine(pen, TrackX - 22, gateY, TrackX + 24, gateY);
        }
        TextAt(g, _engine.GpuOnlyPinned ? "LOCK" : "CPU", _tiny,
            _engine.GpuOnlyPinned ? Color.DarkBlue : Color.Black,
            new RectangleF(525, gateY - 14, 36, 25));
        foreach (int tick in new[] { 67, 69 })
        {
            float y = TrackBottom - tick * height / BurnEngine.MaxTargetWatts;
            bool left = tick == 67;
            using var pen = new Pen(Color.Black, 2);
            g.DrawLine(pen, left ? TrackX - 19 : TrackX + 10, y,
                left ? TrackX - 5 : TrackX + 25, y);
            TextAt(g, $"{tick}", _tiny, Color.Black,
                left ? new RectangleF(459, y - 23, 29, 18) : new RectangleF(525, y + 5, 28, 17));
        }
        using (var brush = new SolidBrush(_engine.GpuOnlyPinned ? Color.LightGreen :
                   knobY < gateY ? CPUColor : GPUColor))
            g.FillEllipse(brush, TrackX - 17, knobY - 17, 34, 34);
        using (var pen = new Pen(Color.Black, _engine.GpuOnlyPinned ? 5f : 3f))
            g.DrawEllipse(pen, TrackX - 17, knobY - 17, 34, 34);
        TextAt(g, "420 W", _tiny, Color.Black, new RectangleF(481, TrackTop - 29, 81, 22));
        TextAt(g, "OFF", _tiny, Color.Black, new RectangleF(520, TrackBottom - 4, 41, 23));
    }
    private void DrawTip(Graphics g)
    {
        RectangleF cap = TipRect();
        using (var brush = new SolidBrush(_hoverTip ? Color.Yellow : Color.FromArgb(255, 186, 178)))
            g.FillEllipse(brush, cap);
        using (var pen = new Pen(Color.FromArgb(67, 49, 69), _hoverTip ? 5f : 4f)) g.DrawEllipse(pen, cap);
        TextAt(g, "×", _big, Color.FromArgb(56, 42, 60),
            new RectangleF(cap.X + 80, cap.Y + 29, 53, 43));
        TextAt(g, "CLICK TO CLOSE", _tiny, Color.FromArgb(67, 48, 67),
            new RectangleF(cap.X + 32, cap.Y + 84, 152, 19));
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (TipContains(e.Location)) { _engine.SetTarget(0); Close(); return; }
        if (SliderContains(e.Location))
        {
            _dragSlider = true;
            Capture = true;
            SetFromMouse(e.Y);
            return;
        }
        // Native caption dragging from the unused part of the borderless window.
        ReleaseCapture();
        SendMessage(Handle, 0xA1, (nint)2, 0);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool overTip = TipContains(e.Location);
        if (_hoverTip != overTip) { _hoverTip = overTip; Invalidate(); }
        Cursor = overTip || SliderContains(e.Location) ? Cursors.Hand : Cursors.Default;
        if (_dragSlider) SetFromMouse(e.Y);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragSlider = false;
        Capture = false;
        base.OnMouseUp(e);
    }
    private void SetFromMouse(int mouseY)
    {
        float line = MarkerY();
        float separation = Math.Abs(mouseY - line);
        // Magnetic snap with hysteresis: the thumb cannot accidentally fall off
        // the moving CPU marker until dragged well away from it.
        if (separation <= (_engine.GpuOnlyPinned ? SnapDistance + 8 : SnapDistance))
        {
            if (!_engine.GpuOnlyPinned)
            {
                _engine.SetGpuOnlyPin();
                _targetWatts = _engine.Target;
            }
            Invalidate();
            return;
        }
        int watts = Math.Clamp((int)Math.Round((TrackBottom - mouseY) * BurnEngine.MaxTargetWatts /
            (TrackBottom - TrackTop)), 0, BurnEngine.MaxTargetWatts);
        if (!_engine.GpuOnlyPinned && watts == _targetWatts) return;
        _targetWatts = watts;
        _engine.SetTarget(watts); // Explicitly releases the GPU-only lock.
        Invalidate();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.F1)
        {
            string text = "CPU sensor provider: " + _sensors.CpuSource + Environment.NewLine +
                "Unavailable power/temperature readings are shown as N/A, not guessed." + Environment.NewLine +
                "Try running StressDeez as administrator, or HWiNFO Sensors with Shared Memory Support." + Environment.NewLine +
                Environment.NewLine + _sensors.SensorError + Environment.NewLine + _sensors.CpuDiagnostics;
            MessageBox.Show(this, text[..Math.Min(text.Length, 4000)],
                "StressDeez CPU sensor diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
            e.Handled = true;
            return;
        }
        if (e.KeyCode is Keys.Up or Keys.Down or Keys.Left or Keys.Right)
        {
            int delta = e.KeyCode is Keys.Up or Keys.Right ? 5 : -5;
            int next = Math.Clamp(_engine.Target + delta, 0, BurnEngine.MaxTargetWatts);
            _targetWatts = next;
            _engine.SetTarget(next); // Keyboard adjustment releases marker lock.
            Invalidate();
            e.Handled = true;
        }
        if (e.KeyCode == Keys.Escape)
        {
            _targetWatts = 0;
            _engine.SetTarget(0);
            Invalidate();
            e.Handled = true;
        }
    }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, uint msg, nint wparam, nint lparam);
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _engine.Dispose();
        _sensors.Dispose();
        _timer.Dispose();
        _title.Dispose(); _big.Dispose(); _normal.Dispose(); _small.Dispose(); _tiny.Dispose();
        base.OnFormClosing(e);
    }
}
