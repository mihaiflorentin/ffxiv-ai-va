// S0/S2 Turbo harness: encoder -> (embed + LM decode loop) -> decoder, per-phase timing, per-role device placement.
// Args: dir=hf/onnx prec=fp16|q4f16|fp32 enc/emb/lm/dec=cpu|webgpu (default = dev) dev=<webgpu device idx> steps=N kv=managed|iobind
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;

static class Turbo
{
    const int Layers = 24, Heads = 16, HeadDim = 64, Hidden = 1024, Vocab = 6563, StartTok = 6561, StopTok = 6562, Silence = 4299;

    const string Tsl = "/model/attn_mask_reformat/attn_mask_subgraph/Gather/Cast/output_0";
    static double Ms(long t0) => Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

    static float[] ReadWav(string path)
    {
        var b = File.ReadAllBytes(path);
        int pos = 12; int fmt = 0, bits = 0, rate = 0, ch = 0;
        while (pos + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int sz = BitConverter.ToInt32(b, pos + 4);
            if (id == "fmt ") { fmt = BitConverter.ToUInt16(b, pos + 8); ch = BitConverter.ToUInt16(b, pos + 10); rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToUInt16(b, pos + 22); }
            if (id == "data")
            {
                if (rate != 24000 || ch != 1) throw new Exception($"ref wav must be 24k mono (got {rate}/{ch})");
                var n = sz / (bits / 8);
                var f = new float[n];
                for (int i = 0; i < n; i++)
                    f[i] = fmt == 3 ? BitConverter.ToSingle(b, pos + 8 + i * 4) : BitConverter.ToInt16(b, pos + 8 + i * 2) / 32768f;
                return f;
            }
            pos += 8 + sz + (sz & 1);
        }
        throw new Exception("no data chunk");
    }

    sealed class Sess : IDisposable
    {
        public InferenceSession S = null!; public SessionOptions O = null!; public string Dev = "cpu"; public OrtEpDevice? Ep;
        public void Dispose() { S.Dispose(); O.Dispose(); }
    }

    static Sess Make(OrtEnv env, Dictionary<string, string> a, List<OrtEpDevice>? devs, string role, string path, out double ms)
    {
        var dev = a.GetValueOrDefault(role, a.GetValueOrDefault("dev_all", "cpu"));
        var so = P.MakeOptions(env, a, dev, devs);
        var sw = Stopwatch.StartNew();
        var s = new InferenceSession(path, so);
        ms = sw.Elapsed.TotalMilliseconds;
        var r = new Sess { S = s, O = so, Dev = dev };
        if (dev == "webgpu") r.Ep = devs!.Where(d => d.EpName == WebGpuEp.GetEpName()).ElementAt(int.Parse(a.GetValueOrDefault("dev", "0")));
        return r;
    }

    public static int Run(Dictionary<string, string> a)
    {
        var dir = a.GetValueOrDefault("dir", "hf/onnx");
        var prec = a.GetValueOrDefault("prec", "fp16");
        var sfx = prec == "fp32" ? "" : "_" + prec;
        var all = a.GetValueOrDefault("all", "cpu");
        a["dev_all"] = all;
        foreach (var r in new[] { "enc", "emb", "lm", "dec" }) if (!a.ContainsKey(r)) a[r] = all;
        var env = P.Env();
        var needGpu = new[] { "enc", "emb", "lm", "dec" }.Any(r => a[r] == "webgpu");
        List<OrtEpDevice>? devs = null;
        if (needGpu)
        {
            env.RegisterExecutionProviderLibrary("webgpu_ep", P.EpLibPath(a));
            devs = env.GetEpDevices().ToList();
        }
        Console.WriteLine($"CONFIG prec={prec} enc={a["enc"]} emb={a["emb"]} lm={a["lm"]} dec={a["dec"]} dev={a.GetValueOrDefault("dev", "-")} kv={a.GetValueOrDefault("kv", "managed")} steps={a.GetValueOrDefault("steps", "100")}");

        using var enc = Make(env, a, devs, "enc", $"{dir}/speech_encoder{sfx}.onnx", out var tEnc);
        using var emb = Make(env, a, devs, "emb", $"{dir}/embed_tokens{sfx}.onnx", out var tEmb);
        using var lm = Make(env, a, devs, "lm", $"{dir}/language_model{sfx}.onnx", out var tLm);
        using var dec = Make(env, a, devs, "dec", $"{dir}/conditional_decoder{sfx}.onnx", out var tDec);
        Console.WriteLine($"SESSION_CREATE_MS enc={tEnc:F0} emb={tEmb:F0} lm={tLm:F0} dec={tDec:F0}");

        // 1. reference encode
        var wav = ReadWav(a.GetValueOrDefault("ref", "ref.wav"));
        float[] cond = null!; long[] condShape = null!; long[] promptTok = null!; float[] xvec = null!; long[] xvecShape = null!; float[] pfeat = null!; long[] pfeatShape = null!;
        for (int rep = 0; rep < 2; rep++)
        {
            using var inp = OrtValue.CreateTensorValueFromMemory(wav, new long[] { 1, wav.Length });
            var t0 = Stopwatch.GetTimestamp();
            using var res = enc.S.Run(new RunOptions(), new Dictionary<string, OrtValue> { ["audio_values"] = inp }, enc.S.OutputNames);
            var ms = Ms(t0);
            Console.WriteLine($"ENCODER run{rep}: {ms:F0} ms (ref {wav.Length / 24000.0:F1}s)");
            if (rep == 1)
            {
                cond = res[0].GetTensorDataAsSpan<float>().ToArray(); condShape = res[0].GetTensorTypeAndShape().Shape;
                promptTok = res[1].GetTensorDataAsSpan<long>().ToArray();
                xvec = res[2].GetTensorDataAsSpan<float>().ToArray(); xvecShape = res[2].GetTensorTypeAndShape().Shape;
                pfeat = res[3].GetTensorDataAsSpan<float>().ToArray(); pfeatShape = res[3].GetTensorTypeAndShape().Shape;
            }
        }
        Console.WriteLine($"ref cond frames={condShape[1]} promptTokens={promptTok.Length}");

        // 2. decode loop
        var steps = int.Parse(a.GetValueOrDefault("steps", "100"));
        var textIds = Enumerable.Range(0, 24).Select(i => (long)(1000 + i * 37 % 4000)).ToArray();
        bool fp16kv = prec != "fp32";
        List<long> generated;
        double prefillMs; List<double> stepMs = new(); double embedTotal = 0;
        if (fp16kv) generated = Loop<Float16>(a, emb, lm, cond, condShape, textIds, steps, out prefillMs, stepMs, ref embedTotal);
        else generated = Loop<float>(a, emb, lm, cond, condShape, textIds, steps, out prefillMs, stepMs, ref embedTotal);
        Console.WriteLine("TOKENS: " + string.Join(",", generated.Skip(1).Take(40)));
        var steady = stepMs.Skip(3).DefaultIfEmpty(0).Average();
        Console.WriteLine($"LM prefill={prefillMs:F0} ms; decode steps={stepMs.Count}; per-token(ms) first3={string.Join("/", stepMs.Take(3).Select(x => x.ToString("F1")))} steady_avg={steady:F1} p95={Pct(stepMs, .95):F1}; embed total={embedTotal:F0} ms");

        // 3. decoder
        var speech = new List<long>(promptTok);
        speech.AddRange(generated.Skip(1));
        speech.AddRange(new long[] { Silence, Silence, Silence });
        double audioS = 0; double decMs = 0;
        for (int rep = 0; rep < 2; rep++)
        {
            using var st = OrtValue.CreateTensorValueFromMemory(speech.ToArray(), new long[] { 1, speech.Count });
            using var xv = OrtValue.CreateTensorValueFromMemory(xvec, xvecShape);
            using var pf = OrtValue.CreateTensorValueFromMemory(pfeat, pfeatShape);
            var t0 = Stopwatch.GetTimestamp();
            using var res = dec.S.Run(new RunOptions(), new Dictionary<string, OrtValue> { ["speech_tokens"] = st, ["speaker_embeddings"] = xv, ["speaker_features"] = pf }, dec.S.OutputNames);
            decMs = Ms(t0);
            audioS = res[0].GetTensorDataAsSpan<float>().Length / 24000.0;
            Console.WriteLine($"DECODER run{rep}: {decMs:F0} ms, total audio {audioS:F2}s (incl. {promptTok.Length / 25.0:F1}s prompt)");
            if (rep == 1 && a.TryGetValue("outwav", out var ow)) File.WriteAllBytes(ow, res[0].GetTensorDataAsSpan<float>().ToArray().SelectMany(BitConverter.GetBytes).ToArray());
        }
        var newAudio = (generated.Count - 1) / 25.0;
        var totalMs = prefillMs + stepMs.Sum() + embedTotal + decMs;
        Console.WriteLine($"SUMMARY prec={prec} lm={a["lm"]} kv={a.GetValueOrDefault("kv", "managed")} newAudio={newAudio:F2}s lm_ms_per_tok={steady:F1} lm_total={prefillMs + stepMs.Sum():F0}ms dec={decMs:F0}ms RTF(lm+embed+dec)={totalMs / 1000 / newAudio:F2}");
        return 0;
    }

    static double Pct(List<double> v, double p) { if (v.Count == 0) return 0; var s = v.OrderBy(x => x).ToList(); return s[(int)Math.Min(s.Count - 1, p * s.Count)]; }

    static List<long> Loop<T>(Dictionary<string, string> a, Sess emb, Sess lm, float[] cond, long[] condShape, long[] textIds, int steps,
        out double prefillMs, List<double> stepMs, ref double embedTotal) where T : unmanaged
    {
        var iobind = a.GetValueOrDefault("kv", "managed") == "iobind";
        var generated = new List<long> { StartTok };
        var names = new List<string>(); // past names + present names, layer-major key,value
        for (int l = 0; l < Layers; l++) { names.Add($"{l}.key"); names.Add($"{l}.value"); }
        // managed state
        var past = new T[names.Count][]; var pastShape = new long[names.Count][];
        for (int i = 0; i < names.Count; i++) { past[i] = Array.Empty<T>(); pastShape[i] = new long[] { 1, Heads, 0, HeadDim }; }
        // iobind state
        var pastOv = new OrtValue?[names.Count];
        OrtMemoryInfo? devInfo = null, cpuInfo = null;
        OrtIoBinding? bind = null; IDisposable? prevOuts = null;
        if (iobind)
        {
            devInfo = lm.Ep!.GetMemoryInfo(OrtDeviceMemoryType.DEFAULT);
            cpuInfo = OrtMemoryInfo.DefaultInstance;
            Console.WriteLine($"IOBIND device memInfo: name={devInfo.Name} id={devInfo.Id} vendor=0x{devInfo.GetVendorId():x} devMemType={devInfo.GetDeviceMemoryType()} allocType={devInfo.GetAllocatorType()}");
        }

        long[] ids = textIds; long lastPos = 0; long total = 0; long[] posIds = null!; long[] mask = null!;
        var rnd = new Random(7);
        var hist = new HashSet<long>();
        prefillMs = 0;
        for (int step = 0; step < steps; step++)
        {
            // embed
            var te = Stopwatch.GetTimestamp();
            float[] te_out;
            using (var idv = OrtValue.CreateTensorValueFromMemory(ids, new long[] { 1, ids.Length }))
            using (var r = emb.S.Run(new RunOptions(), new Dictionary<string, OrtValue> { ["input_ids"] = idv }, emb.S.OutputNames))
                te_out = r[0].GetTensorDataAsSpan<float>().ToArray();
            embedTotal += Ms(te);
            float[] embeds; int frames;
            if (step == 0)
            {
                frames = (int)condShape[1] + ids.Length;
                embeds = new float[frames * Hidden];
                cond.CopyTo(embeds, 0); te_out.CopyTo(embeds, cond.Length);
                posIds = Enumerable.Range(0, frames).Select(i => (long)i).ToArray();
                mask = Enumerable.Repeat(1L, frames).ToArray(); total = frames; lastPos = frames - 1;
            }
            else
            {
                frames = 1; embeds = te_out; lastPos++; posIds = new[] { lastPos };
                total++; mask = Enumerable.Repeat(1L, (int)total).ToArray();
            }

            var t0 = Stopwatch.GetTimestamp();
            float[] logits;
            using (var ro = new RunOptions())
            {
                if (!iobind)
                {
                    var owned = new List<OrtValue>();
                    var inputs = new Dictionary<string, OrtValue>();
                    owned.Add(inputs["inputs_embeds"] = OrtValue.CreateTensorValueFromMemory(embeds, new long[] { 1, frames, Hidden }));
                    owned.Add(inputs["attention_mask"] = OrtValue.CreateTensorValueFromMemory(mask, new long[] { 1, mask.Length }));
                    owned.Add(inputs["position_ids"] = OrtValue.CreateTensorValueFromMemory(posIds, new long[] { 1, posIds.Length }));
                    if (lm.S.InputMetadata.ContainsKey(Tsl)) owned.Add(inputs[Tsl] = OrtValue.CreateTensorValueFromMemory(new int[] { mask.Length }, new long[0]));
                    for (int i = 0; i < names.Count; i++)
                        owned.Add(inputs["past_key_values." + names[i]] = OrtValue.CreateTensorValueFromMemory(past[i], pastShape[i]));
                    var outNames = lm.S.OutputNames;
                    using var res = lm.S.Run(ro, inputs, outNames);
                    logits = res[0].GetTensorDataAsSpan<float>()[^Vocab..].ToArray();
                    for (int i = 0; i < names.Count; i++) // the managed KV round trip: clone each present back to CPU arrays
                    {
                        var span = res[1 + i].GetTensorDataAsSpan<T>();
                        past[i] = span.ToArray();
                        pastShape[i] = res[1 + i].GetTensorTypeAndShape().Shape;
                    }
                    foreach (var o in owned) o.Dispose();
                }
                else
                {
                    bind?.Dispose();
                    bind = lm.S.CreateIoBinding();
                    var owned = new List<OrtValue>();
                    owned.Add(OrtValue.CreateTensorValueFromMemory(embeds, new long[] { 1, frames, Hidden })); bind.BindInput("inputs_embeds", owned[^1]);
                    owned.Add(OrtValue.CreateTensorValueFromMemory(mask, new long[] { 1, mask.Length })); bind.BindInput("attention_mask", owned[^1]);
                    owned.Add(OrtValue.CreateTensorValueFromMemory(posIds, new long[] { 1, posIds.Length })); bind.BindInput("position_ids", owned[^1]);
                    if (lm.S.InputMetadata.ContainsKey(Tsl)) { owned.Add(OrtValue.CreateTensorValueFromMemory(new int[] { mask.Length }, new long[0])); bind.BindInput(Tsl, owned[^1]); }
                    OrtValue? emptyCpu = null;
                    for (int i = 0; i < names.Count; i++)
                    {
                        if (pastOv[i] == null)
                        {
                            emptyCpu = OrtValue.CreateTensorValueFromMemory(Array.Empty<T>(), new long[] { 1, Heads, 0, HeadDim });
                            bind.BindInput("past_key_values." + names[i], emptyCpu); owned.Add(emptyCpu);
                        }
                        else bind.BindInput("past_key_values." + names[i], pastOv[i]!);
                        bind.BindOutputToDevice("present." + names[i], devInfo!);
                    }
                    bind.BindOutputToDevice("logits", cpuInfo!);
                    lm.S.RunWithBinding(ro, bind);
                    bind.SynchronizeBoundOutputs();
                    var outs = bind.GetOutputValues();
                    var onames = bind.GetOutputNames();
                    int li = Array.IndexOf(onames, "logits");
                    logits = outs[li].GetTensorDataAsSpan<float>()[^Vocab..].ToArray();
                    for (int i = 0; i < names.Count; i++) pastOv[i] = outs[Array.IndexOf(onames, "present." + names[i])];
                    prevOuts?.Dispose(); // previous step's buffers are no longer referenced by the new inputs
                    prevOuts = outs;
                    foreach (var o in owned) o.Dispose();
                }
            }
            var dt = Ms(t0);
            if (step == 0) prefillMs = dt; else stepMs.Add(dt);

            foreach (var g in generated) { if (g < Vocab) logits[g] = logits[g] > 0 ? logits[g] / 1.2f : logits[g] * 1.2f; }
            logits[StopTok] = float.NegativeInfinity; // force fixed-length decode for timing
            int best = 0; for (int i = 1; i < Vocab; i++) if (logits[i] > logits[best]) best = i;
            generated.Add(best);
            ids = new long[] { best };
        }
        bind?.Dispose(); prevOuts?.Dispose();
        return generated;
    }

    public static int IoBind(Dictionary<string, string> a) => Run(a);
}
