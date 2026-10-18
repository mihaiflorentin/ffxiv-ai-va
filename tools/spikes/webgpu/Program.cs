// Scratch harness for GPU spikes S0-S2 (not part of the solution).
// Usage: Spike <mode> key=value ...
using System.Diagnostics;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;
using Microsoft.ML.OnnxRuntime.Tensors;

static class P
{
    static Dictionary<string, string> A = new();
    static string Get(string k, string d = "") => A.TryGetValue(k, out var v) ? v : d;

    static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "enum";
        foreach (var a in args.Skip(1))
        {
            var i = a.IndexOf('=');
            if (i > 0) A[a[..i]] = a[(i + 1)..];
        }
        try
        {
            return mode switch
            {
                "enum" => Enum(),
                "inspect" => Inspect(),
                "kokoro" => Kokoro(),
                "turbo" => Turbo.Run(A),
                "dml" => Dml(),
                "load" => Load(),
                "loadlib" => LoadLib(),
                "runmodel" => RunModel(),
                "api" => Api(),
                "embed" => Embed(),
                "iobind" => Turbo.IoBind(A),
                _ => 2,
            };
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED: " + e.GetType().Name + ": " + e.Message);
            Console.WriteLine(e.StackTrace);
            return 1;
        }
    }

    public static OrtEnv Env()
    {
        var lvl = Get("log", "warning") switch
        {
            "verbose" => OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE,
            "info" => OrtLoggingLevel.ORT_LOGGING_LEVEL_INFO,
            _ => OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
        };
        var o = new EnvironmentCreationOptions { logLevel = lvl }; var env = OrtEnv.CreateInstanceWithOptions(ref o);
        return env;
    }

    public static string EpLibPath(Dictionary<string, string> a)
    {
        if (a.TryGetValue("eplib", out var p) && p.Length > 0) return p;
        return WebGpuEp.GetLibraryPath();
    }

    static List<OrtEpDevice> Register(OrtEnv env, bool print)
    {
        var sw = Stopwatch.StartNew();
        var path = EpLibPath(A);
        Console.WriteLine($"EP library: {path}");
        env.RegisterExecutionProviderLibrary("webgpu_ep", path);
        Console.WriteLine($"RegisterExecutionProviderLibrary: {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        var devs = env.GetEpDevices().ToList();
        Console.WriteLine($"GetEpDevices: {sw.ElapsedMilliseconds} ms, {devs.Count} device(s) total");
        if (print)
        {
            int i = 0;
            foreach (var d in devs)
            {
                var hw = d.HardwareDevice;
                Console.WriteLine($"  [{i++}] ep={d.EpName} vendor={d.EpVendor} hwType={hw.Type} hwVendor={hw.Vendor} vid=0x{hw.VendorId:x} did=0x{hw.DeviceId:x}");
                foreach (var kv in hw.Metadata.Entries) Console.WriteLine($"       hwmeta {kv.Key}={kv.Value}");
                foreach (var kv in d.EpMetadata.Entries) Console.WriteLine($"       epmeta {kv.Key}={kv.Value}");
                foreach (var kv in d.EpOptions.Entries) Console.WriteLine($"       epopt  {kv.Key}={kv.Value}");
            }
        }
        return devs;
    }

    static int Enum()
    {
        var env = Env();
        Register(env, true);
        return 0;
    }

    static int Inspect()
    {
        using var s = new InferenceSession(Get("model"));
        Console.WriteLine("inputs:");
        foreach (var kv in s.InputMetadata) Console.WriteLine($"  {kv.Key} {kv.Value.ElementDataType} [{string.Join(",", kv.Value.Dimensions)}]");
        Console.WriteLine("outputs:");
        foreach (var kv in s.OutputMetadata) Console.WriteLine($"  {kv.Key} {kv.Value.ElementDataType} [{string.Join(",", kv.Value.Dimensions)}]");
        return 0;
    }

    /// Build session options for device=cpu|webgpu|dml. Extra WebGPU options come from "opts=k:v,k:v" (applied verbatim).
    public static SessionOptions MakeOptions(OrtEnv env, Dictionary<string, string> a, string device, List<OrtEpDevice>? devs)
    {
        var so = new SessionOptions();
        if (a.TryGetValue("log", out var l) && l == "verbose") { so.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE; so.LogVerbosityLevel = 1; }
        if (a.TryGetValue("graphopt", out var g) && g == "basic") so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
        if (a.TryGetValue("threads", out var t)) so.IntraOpNumThreads = int.Parse(t);
        if (a.ContainsKey("profile")) so.EnableProfiling = true;
        if (device == "webgpu")
        {
            var opts = new Dictionary<string, string>();
            if (a.TryGetValue("opts", out var o) && o.Length > 0)
                foreach (var kv in o.Split(',', StringSplitOptions.RemoveEmptyEntries)) { var j = kv.IndexOf(':'); opts[kv[..j]] = kv[(j + 1)..]; }
            if (a.TryGetValue("forcecpu", out var fc) && fc.Length > 0)
            {
                // forcecpu=file.txt (one node name per line) or inline names separated by ';'
                var names = File.Exists(fc) ? File.ReadAllLines(fc).Where(x => x.Length > 0) : fc.Split(';');
                opts["forceCpuNodeNames"] = string.Join("\n", names);
            }
            var idx = int.Parse(a.GetValueOrDefault("dev", "0"));
            var wg = devs!.Where(d => d.EpName == WebGpuEp.GetEpName()).ToList();
            Console.WriteLine($"WebGPU EP devices: {wg.Count}; using index {idx}; options: {string.Join(",", opts.Select(k => k.Key + "=" + k.Value))}");
            so.AppendExecutionProvider(env, new[] { wg[idx] }, opts);
        }
        else if (device == "dml")
        {
            // needs the DirectML package; handled in Dml()
            throw new NotSupportedException();
        }
        return so;
    }

    static float[] LoadNpyVoice(string path, out int rows)
    {
        var b = File.ReadAllBytes(path);
        int hl = BitConverter.ToUInt16(b, 8);
        var data = b.AsSpan(10 + hl);
        var f = new float[data.Length / 4];
        Buffer.BlockCopy(b, 10 + hl, f, 0, data.Length);
        rows = f.Length / 256;
        return f;
    }

    static int Kokoro()
    {
        var device = Get("device", "cpu");
        var env = Env();
        List<OrtEpDevice>? devs = null;
        if (device == "webgpu") devs = Register(env, Get("printdevs") == "1");
        var sw = Stopwatch.StartNew();
        using var so = MakeOptions(env, A, device, devs);
        using var sess = new InferenceSession(Get("model"), so);
        Console.WriteLine($"session creation: {sw.ElapsedMilliseconds} ms");

        var voice = LoadNpyVoice(Get("voice"), out var rows);
        var n = int.Parse(Get("ntok", "60"));
        var rnd = new Random(1234);
        var toks = new long[n + 2];
        for (int i = 1; i <= n; i++) toks[i] = rnd.Next(16, 120); // plausible phoneme ids, seeded for reproducibility
        var style = new float[256];
        Array.Copy(voice, n * 256, style, 0, 256);

        var runs = int.Parse(Get("runs", "6"));
        double firstMs = 0; var times = new List<double>(); float[]? lastWav = null;
        for (int r = 0; r < runs; r++)
        {
            using var tIn = OrtValue.CreateTensorValueFromMemory(toks, new long[] { 1, toks.Length });
            using var sIn = OrtValue.CreateTensorValueFromMemory(style, new long[] { 1, 256 });
            var speed = new float[] { 1f };
            using var spIn = OrtValue.CreateTensorValueFromMemory(speed, new long[] { 1 });
            var t0 = Stopwatch.GetTimestamp();
            using var res = sess.Run(new RunOptions(), new Dictionary<string, OrtValue> { ["tokens"] = tIn, ["style"] = sIn, ["speed"] = spIn }, sess.OutputNames);
            var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            var wav = res[0].GetTensorDataAsSpan<float>().ToArray();
            lastWav = wav;
            if (r == 0) firstMs = ms; else times.Add(ms);
            Console.WriteLine($"run {r}: {ms:F0} ms, samples={wav.Length} ({wav.Length / 24000.0:F2} s audio)");
        }
        var steady = times.Count > 0 ? times.Average() : firstMs;
        var audioS = lastWav!.Length / 24000.0;
        Console.WriteLine($"SUMMARY device={device} ntok={n} first={firstMs:F0}ms steady={steady:F0}ms audio={audioS:F2}s RTF_steady={steady / 1000 / audioS:F3} RTF_first={firstMs / 1000 / audioS:F3}");
        if (A.TryGetValue("outwav", out var ow)) { File.WriteAllBytes(ow, lastWav.SelectMany(BitConverter.GetBytes).ToArray()); }
        if (A.ContainsKey("profile")) Console.WriteLine("profile: " + sess.EndProfiling());
        return 0;
    }

    static int Load()
    {
        var device = Get("device", "cpu");
        var env = Env();
        List<OrtEpDevice>? devs = null;
        if (device == "webgpu") devs = Register(env, false);
        var sw = Stopwatch.StartNew();
        using var so = MakeOptions(env, A, device, devs);
        using var sess = new InferenceSession(Get("model"), so);
        Console.WriteLine($"LOAD OK {Get("model")} on {device}: {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    static int Embed()
    {
        var device = Get("device", "cpu");
        var env = Env();
        List<OrtEpDevice>? devs = null;
        if (device == "webgpu") devs = Register(env, false);
        using var so = MakeOptions(env, A, device, devs);
        using var sess = new InferenceSession(Get("model"), so);
        var ids = new long[] { 1, 2, 3, 4, 5 };
        for (int r = 0; r < 3; r++)
        {
            using var inp = OrtValue.CreateTensorValueFromMemory(ids, new long[] { 1, 5 });
            var t0 = Stopwatch.GetTimestamp();
            using var res = sess.Run(new RunOptions(), new Dictionary<string, OrtValue> { ["input_ids"] = inp }, sess.OutputNames);
            Console.WriteLine($"embed run {r}: {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1} ms, out elems={res[0].GetTensorTypeAndShape().ElementCount}");
        }
        return 0;
    }

    static int Api()
    {
        foreach (var tn in Get("types").Split(','))
        {
            var t = typeof(OrtEnv).Assembly.GetType("Microsoft.ML.OnnxRuntime." + tn);
            Console.WriteLine("## " + tn);
            if (t == null) { Console.WriteLine("  (missing)"); continue; }
            foreach (var m in t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly).OrderBy(x => x.Name))
                if (!(m is System.Reflection.MethodInfo mi && mi.IsSpecialName)) Console.WriteLine("  " + m.ToString());
        }
        return 0;
    }

    static int RunModel()
    {
        var device = Get("device", "cpu");
        var env = Env();
        List<OrtEpDevice>? devs = null;
        if (device == "webgpu") devs = Register(env, false);
        using var so = MakeOptions(env, A, device, devs);
        using var sess = new InferenceSession(Get("model"), so);
        var keep = new List<IDisposable>(); var inputs = new Dictionary<string, OrtValue>();
        foreach (var kv in sess.InputMetadata)
        {
            var shape = kv.Value.Dimensions.Select(d => d <= 0 ? 4L : d).ToArray();
            if (kv.Key.StartsWith("past_key_values")) shape = new long[] { 1, 16, 0, 64 };
            else if (kv.Key is "attention_mask" or "position_ids") shape = new long[] { 1, 4 };
            else if (kv.Key == "inputs_embeds") shape = new long[] { 1, 4, 1024 };
            if (kv.Key == "attention_mask") { var ones = OrtValue.CreateTensorValueFromMemory(new long[] { 1, 1, 1, 1 }, shape); keep.Add(ones); inputs[kv.Key] = ones; continue; }
            if (kv.Key == "position_ids") { var pos = OrtValue.CreateTensorValueFromMemory(new long[] { 0, 1, 2, 3 }, shape); keep.Add(pos); inputs[kv.Key] = pos; continue; }
            long n = shape.Aggregate(1L, (x, y) => x * y);
            OrtValue v;
            if (shape.Length == 0 && (kv.Key is "s" or "e" or "d"))
            {
                var val = kv.Key == "s" ? 0 : kv.Key == "e" ? 6 : 1;
                v = kv.Value.ElementDataType == TensorElementType.Float ? OrtValue.CreateTensorValueFromMemory(new[] { (float)val }, new long[0]) : OrtValue.CreateTensorValueFromMemory(new[] { (long)val }, new long[0]);
                keep.Add(v); inputs[kv.Key] = v; continue;
            }
            switch (kv.Value.ElementDataType)
            {
                case TensorElementType.Float: v = OrtValue.CreateTensorValueFromMemory(Enumerable.Range(0, (int)n).Select(i => (float)(i % 7) / 7f).ToArray(), shape); break;
                case TensorElementType.Int64: v = OrtValue.CreateTensorValueFromMemory(Enumerable.Range(0, (int)n).Select(i => (long)(i % 5 + (kv.Key == "e" ? 3 : 0))).ToArray(), shape); break;
                case TensorElementType.Float16: v = OrtValue.CreateTensorValueFromMemory(Enumerable.Range(0, (int)n).Select(i => new Float16(BitConverter.HalfToUInt16Bits((Half)((float)(i % 7) / 7f)))).ToArray(), shape); break;
                default: throw new NotSupportedException(kv.Value.ElementDataType.ToString());
            }
            keep.Add(v); inputs[kv.Key] = v;
        }
        using var res = sess.Run(new RunOptions(), inputs, sess.OutputNames);
        Console.WriteLine($"RUN OK {Path.GetFileName(Get("model"))} on {device}: out0 elems={res[0].GetTensorTypeAndShape().ElementCount}");
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Ansi, SetLastError = true)]
    static extern IntPtr LoadLibraryExA(string name, IntPtr file, uint flags);

    static int LoadLib()
    {
        var name = Get("name", "vulkan-1.dll");
        foreach (var (label, flags) in new (string, uint)[] { ("0", 0), ("SEARCH_DEFAULT_DIRS 0x1000", 0x1000), ("SEARCH_SYSTEM32 0x800", 0x800), ("SEARCH_APPLICATION_DIR 0x200", 0x200), ("SEARCH_USER_DIRS 0x400", 0x400), ("SEARCH_SYSTEM32|APPDIR", 0xA00) })
        {
            var h = LoadLibraryExA(name, IntPtr.Zero, flags);
            Console.WriteLine($"LoadLibraryExA({name}, flags={label}) -> {(h == IntPtr.Zero ? "NULL err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error() : "0x" + h.ToString("x"))}");
        }
        return 0;
    }

    static int Dml() { Console.WriteLine("dml mode is built in the Dml project"); return 2; }
}
