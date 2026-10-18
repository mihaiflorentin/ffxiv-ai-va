# Line start latency and mid-line pauses (diagnosis and plan)

Date: 2026-10-18. Read-only investigation of branch `feat/gpu-voices-ui-redesign` at 9003c7b (0.0.36). No code changed. Everything marked "verified" was read in the code; everything marked "estimate" is arithmetic on the numbers in `docs/plans/2026-10-gpu-spike-results.md` and has to be confirmed with the stage timings proposed in section 7.

Reported symptoms:

1. Voiced lines take 5 to 10 s to START (Turbo, "Most expressive", RX 9070 XT, WebGPU/Vulkan under Wine, game rendering at about 99 percent GPU).
2. Some lines pause 2 to 5 s BETWEEN WORDS mid-sentence ("... willing to serve" [silence] ", it is forever.").
3. The engine card says "Ready - Running on your processor" while Processor = Graphics card.

"Even out loudness" turned off made no difference, which matches the code: streamed lines measure only the first 500 ms (`Domain/PolishedPcmSource.cs:14-16, 56-62`) and the whole-line path levels in microseconds (`Domain/AudioPolish.cs`). It never waits for the whole line.

## 0. Read this first: the "Running on your processor" card has a code-level explanation, and it is not the hot reload

`Ready` plus a CPU device can only come out of `StatusFor` (`Infrastructure/Onnx/ChatterboxSynthesizer.cs:219-250`) when the plan has `WantedGpu == false`. With `Device.Mode = gpu` the policy (`Domain/DeviceModePolicy.cs:73-125`) always sets `WantedGpu = true` for a CPU plan, and every GPU failure path (WebGPU not coming up, EP load failure, session build failure, crash guard) ends in `EngineState.FellBack` with a reason and a banner (`ChatterboxSynthesizer.cs:956-980`, `UI/State/EngineViewModel.cs:50-54`, `UI/State/DeviceViewModel.cs:180-196`). So a failed GPU bring-up after a hot reload would show "Using your processor" plus the reason plus a Retry, not "Ready".

The one place that builds a `WantedGpu: false` CPU plan while Mode is gpu is the explicit precision pin: `ChatterboxSynthesizer.Resolve` (`ChatterboxSynthesizer.cs:885-888`) returns `DevicePlan.OnCpu("Running the full-precision voice files on the CPU, as you chose.", wantedGpu: false)` for `Precision == "fp32"`. `Precision` is written only by the Advanced picker (`UI/State/EnginePrecisionModel.cs:75-85`, `UI/Pages/AdvancedEngines.cs:127-157`).

Evidence on the dev machine: `~/.xlcore/pluginConfigs/AIVoiceActing.json` (modified 2026-10-18 13:00) has `Engine.PerEngine["chatterbox-turbo"].Precision = "fp32"`, `Device.Mode = "gpu"`, `Device.CpuImpact = "high"` (Fast, 8 threads), `Voices.UniquePerCharacter = true`. If the player's install looks the same, this is the whole story for symptom 1 and 3: language model on the CPU at about 118 ms per token (spike table S2), `SupportsStreaming` false (`ChatterboxSynthesizer.cs:561-562`, so the whole line renders before a sound), RTF well above 1.

I cannot tell from here whether the player's config matches. First action for the player: Settings > Advanced > Engines > Chatterbox Turbo > Precision = "Automatic (recommended)", then Retry on GPU. The diagnostics report does not print precision, which is why this was invisible (section 7).

Symptom 2 (pause mid-line) cannot happen on the CPU path (no streaming), so those observations were taken with the GPU language model running. Two different states probably occurred during testing; the plan below treats them separately.

## 1. Path from line arrival to first sample (verified)

| # | Step | Where | Thread | Can add seconds |
|---|---|---|---|---|
| 1 | Talk/BattleTalk poll on framework tick, dedupe, gates | `Infrastructure/Dalamud/TalkDialogueSource.cs:68-110`, `Domain/Pipeline/SpeechPipeline.cs:124-215` | game | no, no delays anywhere (grep for Delay/Sleep/Debounce is empty) |
| 2 | Channel to ONE background worker, one line at a time | `SpeechPipeline.cs:35-37, 149-158` | pool | line N+1 waits for line N to finish RENDERING (not playing) |
| 3 | `ResolveProfile`: casting, `GeneratedVoiceStore`, `VoiceGenerator.Ensure`, JSON save, all under the container-wide `lock(gate)` | `Container/ServiceContainer.cs:1284-1366` | worker | first sight of a character: tens of ms (file writes under Wine), blocks every other container accessor meanwhile |
| 4 | Text prep, director (rules by default; classifier session only if "Director" is on) | `SpeechRequestHandler.cs:103-111`, `Infrastructure/Onnx/EmotionClassifierDirector.cs:123` | worker | ms; classifier session load once |
| 5 | Chunking: only at `. ! ? ...`, lines under 120 chars stay whole, pieces merge to at least 40 chars | `Domain/SentenceChunker.cs:15-18, 61-72` | worker | creates N sequential renders (section 4) |
| 6 | Streaming decision: `StreamSentences` (default true, `Configuration.cs:152`) AND engine `SupportsStreaming` AND `SupportsStreamingFor` AND not cached | `SpeechRequestHandler.cs:136-143, 155` | worker | falls back to whole line if any is false |
| 7 | Reference clip for the voice: user clip, pack clip (blend: heaviest source with a clip), pitched copy, else Kokoro renders a 10 s passage once per voice and pitch | `ServiceContainer.cs:717-770, 794-832, 588-590` | worker | 2 to 5 s the first time per (voice, pitch) per machine (section 2, cause A) |
| 8 | Synthesis gate (single flight) | `ChatterboxSynthesizer.cs:55, 575` | pool | waits for previous line's render |
| 9 | Speech encoder on the clip, cached in memory per (precision, path) | `ChatterboxSynthesizer.cs:738-739, 779-814` | pool | 0.4 to 1 s CPU on a miss; lost on every plugin load |
| 10 | LM prefill (about 150 condition frames plus text) then one `embed` run, one LM run, one logits read-back per token | `Infrastructure/Onnx/TurboDecodeLoop.cs:77-153, 167-254` | LM thread | per token cost depends on the GPU being shared with the game |
| 11 | First decoder window released at 38 tokens (32 plus 6 lookahead); decoder = 100 prompt tokens + 12 context + 38 on the CPU | `Domain/ChunkSchedule.cs:12-18, 94-128`, `StreamingOptions.cs:19`, `ChatterboxSynthesizer.cs:708-736` | consumer | 0.6 s idle (spike) up to 2 s+ when the CPU is contended |
| 12 | `onSamples` creates the `SpeechItem` only now | `SpeechRequestHandler.cs:216-240` | consumer | |
| 13 | Playback worker dequeues, `PlayStream` opens a NEW output device per item (enumerate endpoints, `new WasapiOut`, `Init`, `Play`; 200 ms latency, polling mode) | `Infrastructure/Audio/PlaybackSpeechQueue.cs:109-176`, `Infrastructure/Audio/NAudioSink.cs:96-147, 225-264` | playback | estimate 0.2 to 0.8 s under Wine, per sentence chunk |

Not found, checked: no Task.Delay, polling interval, debounce or "wait for the game's voice" delay on the path; no reference-clip work on the framework thread; `InvalidateSpeechSynthesizer` is only called from UI actions (`UI/Pages/AdvancedEngines.cs`, `QualityCard.cs`, `HomeSetupCard.cs`, `ProcessorActions.cs`, `DownloadsCard.cs`), never per line; no idle unload of sessions.

## 2. Ranked causes of the 5 to 10 s start

### A. Reference clip preparation on the critical path of the first line of every new voice (high; verified in code, magnitude estimated)

With "Mix voices" on, every character gets its own generated blend id (`ServiceContainer.cs:1330-1352`). For Turbo as main engine `everyLine` is true, so each line goes through `PrepareExpressiveVoiceAsync` (`ServiceContainer.cs:717-770`):

* A blend whose sources include a pack voice reuses that voice's recorded clip (`RealClipFor`, 794-832), pitch-resampled to `voices-kokoro-ref/<id>_pack_pNNN.wav` (`PitchedCopy`, 834-858): cheap.
* A blend of stock Kokoro voices (for example `bf_emma`, `bf_lily`) has no recorded clip. Kokoro renders `ReferencePassage` (about 10 s of audio, line 588) with the blend tensor, at RTF 0.12 to 0.23 on 4 to 8 threads, so 1.5 to 3 s of CPU next to the game. The result is cached on disk per (voice id, pitch tag), so a pitch change from age casting makes another render. This machine's `voices-kokoro-ref/` holds 17 such files against 32 generated voices.
* The Kokoro used for this is a SECOND, separate instance (`KokoroReferenceRenderer`, `ServiceContainer.cs:569-582`) that nothing warms: `OnClientLogin` only warms `services.SpeechSynthesizer`, the Turbo engine (`AIVoiceActingPlugin.cs:524-541`). The first render therefore also pays Kokoro session creation (0.4 to 0.8 s in the spike), voice bank load (`KokoroSynthesizer.LoadEngine`, lines 340-374) and the phonemizer dictionary decompression that `KokoroSynthesizer.WarmUpAsync` exists to hide.
* Then the Turbo speech encoder runs on the new clip (`EncodeReference`): spike encoder 0.43 to 0.64 s CPU for the 7.4 s default clip. The result lives only in `referenceCache` (memory), so it is paid again after each game start for every voice.

Estimate for a new stock-source character: 2 to 4 s (Kokoro) + 0.5 to 1 s (encoder) before the language model starts, in addition to the base 1.5 to 3 s of section 2.D. That alone explains 5 to 8 s on the first line of each new NPC, and it recurs for every NPC of a quest. For a repeat speaker it costs nothing, which would match "some lines".

Fixes:

1. Warm the reference Kokoro at login, after the Turbo warm-up, at low priority: new `ServiceContainer.WarmReferenceRendererAsync()` called from `AIVoiceActingPlugin.OnClientLogin` (line 524). Expected gain: 1 to 2 s on the first new-voice line of the session.
2. Shorten `ReferencePassage` (`ServiceContainer.cs:588`) from about 10 s to 5 s of speech. Halves the Kokoro render and the encoder time, shrinks condition frames. Needs a listening check (cloning quality, spike used a 7.4 s default clip and saw no decoder speed-up from trimming, so the gain is Kokoro plus encoder only). Expected: 1 to 2 s per new voice.
3. Persist encoder results to disk: `ReferenceEmbeddings` (cond_emb about 0.6 MB, prompt tokens, x-vector, mel) keyed by SHA of the clip plus precision label, under `models/voices-kokoro-ref/*.emb`. Expected: 0.5 to 1 s per voice after each restart. Lower priority.
4. Prepare a new speaker's clip as soon as the speaker is known, not when synthesis needs it: call a new `IExpressivePrefetch.Prepare(voiceId, pitch)` from `ResolveProfile` right after `Ensure`, and let the first line await the same `Task` (single flight per key). This overlaps the clip render with director planning and chunk splitting, only a few ms, so only worthwhile with 5 below.
5. Optional player setting "Faster first line": for a stock-source blend use a pre-rendered clip of the heaviest source voice, pitched, exactly like the pack path already does (`RealClipFor` plus `PitchedCopy`). One idle-time job pre-renders the casting set once (about 30 voices, a minute of background CPU). Trade-off: the clone gets the heaviest source's timbre instead of the blend, which weakens "Mix voices". Keep it opt-in.

### B. First line after a game start is cold (high for the first line only; estimate)

`WarmUpAsync` only builds the four sessions (`ChatterboxSynthesizer.cs:138-151`). It does not run a language model step, so Dawn compiles its pipelines on the first real line (spike: 13 to 22 s for the very first line, and the RADV disk cache was warm there), does not encode the default clip, does not run the decoder once (ORT CPU arena growth, kernel selection for the 2 s graph). Also `SupportsStreaming` is `engine.IsValueCreated && LmEp != Cpu` (line 561-562): a line that arrives before the warm-up finished goes down the whole-line path and waits for the entire render. The same is true right after Retry on GPU or the CPU fallback, which replace the `Lazy<Engine>` (lines 279, 768).

Fix: make `WarmUpAsync` also run one short synthetic line through `StreamCore` with the default clip (output discarded) and one decoder window; report completion through `Status` (Loading until done). Make `SupportsStreaming` depend on the PLAN (`Resolve(cheap: true).Plan.IsGpu && GpuLoopReady`) when the engine is not built yet, so the routing decision does not flip on a load race. Expected: removes 5 to 15 s from the first line of a session and the whole-line detour.

### C. The decoder thread default is dead code in the plugin; "Fast (8 threads)" gives the decoder 8 threads (high; verified)

`OnnxSessionFactory.DecoderThreads` (`Infrastructure/Onnx/OnnxSessionFactory.cs:88-95`) is meant to default the decoder to 2 threads ("an explicit setting is respected"). But `AIVoiceActingPlugin.cs:210` always passes `config.Device.CpuThreads()` and `ConfigurationSections.cs:141-146` always returns 2, 4 or 8, so the argument is never null and the decoder always gets the full CPU-impact thread count: 4 on the default "medium", 8 on "high" (the player's setting). The spike showed the opposite is better for the short calls of a streamed line: one 40-token call costs 0.71 / 1.17 / 1.89 / 3.45 CPU seconds on 1 / 2 / 4 / 8 threads, with wall time best at 2 and worse at 8 (0.97 vs 1.50 s under load). Since 0.0.34 every pool also has `allow_spinning=0` (`Infrastructure/Onnx/OrtThreading.cs:12-16`), which means each of the thousands of small parallel sections in the decoder now parks and wakes its 7 workers through the OS (futex / Wine condvar), which I expect to be slower per call than spinning; the old spin-wait behaviour hid that. This is unmeasured. A/B it with the stage timings.

There is a second effect: the LM driver thread (embed run, bind 49 buffers, read logits) is a managed thread that competes with the decoder's 8 workers and the game's own threads for cores, so a wide decoder also slows the LM, which feeds the decoder.

Fixes:
1. `Infrastructure/Onnx/OnnxSessionFactory.cs:88-95`: ignore the explicit count for the decoder, use `Math.Min(explicit, DecoderMaxThreads)` with `DecoderMaxThreads = 2` (3 as a measured alternative). Keep the 8 for Kokoro. Expected: first window 2 to 1 s faster under load, and fewer cores taken from the game.
2. Add an Advanced developer switch "ORT thread spinning" (default off) read by `OrtThreading.Quiet` for the decoder only, to measure on the player's machine. If spinning on is faster for the decoder at 2 threads, enable it for the decoder only (2 spinning threads is cheap).

### D. Base time to first audio under GPU contention (high; unmeasured on the player's machine)

Idle projection from the spike: about 1 s (0.3 s for 38 tokens at 5 to 10 ms, 0.6 s decoder). Terms that grow with a shared GPU or CPU:

* LM per token: one `RunWithBinding`, `SynchronizeBoundOutputs` and a 26 KB logits copy per token (`TurboDecodeLoop.cs:214-236`), plus a CPU `embed` run (`:155-165`, with a new undisposed `RunOptions` per token). Each step is at least one GPU submit and one blocking read-back. With the game at 99 percent GPU the dispatch waits behind the game's queued frame work, so per token cost moves from 5 ms toward a frame time (the coordinator measured LM time for 156 tokens between 1.5 and 7.2 s, i.e. 10 to 46 ms per token).
* Prefill of 150 condition frames plus text: 40 to 70 ms idle, more under contention.
* First decoder window: 100 prompt tokens is the dominant cost (spike table: prompt 100 + 30 new tokens = 0.95 CPU s single thread).
* Output device open (13 above).

Estimate under contention: LM 38 tokens at 20 to 40 ms = 0.8 to 1.5 s, prefill 0.2 s, first window 1 to 2 s, device 0.3 to 0.8 s, so 2.3 to 4.5 s without any cold start or reference work. Added to A or B, 5 to 10 s.

Mitigations, in order of value:
1. Smaller first window: `ChunkScheduleOptions.FirstTokens` 32 to 20 and the first window only with a 60-token prompt (`ReferenceEmbeddings.Trimmed(60)`, `ChatterboxSynthesizer.cs:660`). Speaker cosine falls from 0.912 to about 0.905 at 60 (spike table), and only for the first 0.8 s. Saves 0.4 to 0.8 s. Pair with the pre-roll of section 3, otherwise a smaller first window makes underruns worse.
2. Open the output device before the first samples exist: enqueue the `SpeechItem` with its still-empty `SpeechAudioStream` when the line starts rendering instead of in `OnSamples` (`SpeechRequestHandler.cs:216-240`), and let `NAudioSink.PlayStream` (which already fills gaps with silence, `NAudioSink.cs:282-300`) open the device immediately. Order is preserved (one render at a time). Drop the early `WaitForData` gate (`NAudioSink.cs:104-119`) only when the item is the queue head, or keep it and move device creation before it. Expected: 0.2 to 0.8 s. A stream that never gets samples must be `Complete()`d, which the handler already does on failure (`SpeechRequestHandler.cs:271`).
3. Keep one output stream open (mixer with silence) instead of one per item. Bigger change, removes the per-sentence device cost entirely. Do 2 first.
4. GPU sharing: the only lever in this EP is the adapter class. On a machine with an iGPU (the dev machine has a Raphael iGPU, spike S0), `Device.GpuPreference = LowPower` runs the LM on the iGPU, which the game is not using: spike S2 measured q4f16 at 15.5 ms per token on that iGPU, about 64 tokens per second, above the 25 needed for real time and independent of the game's load. Worth a one-line experiment on the player's machine and a recommendation in the UI when a second adapter exists. `Device.GpuImpact` ("balanced") is stored but read nowhere (`ConfigurationSections.cs:76` is its only mention), so the UI offers a knob that does nothing; either wire it to chunk size / first-window size or remove it.
5. Reduce per-token overhead: replace the per-token ORT `embed` run with a managed table lookup of the embedding matrix (6563 x width floats, loaded once), remove `new RunOptions()` per call. Small (0.2 to 0.6 ms per token idle, more with a contended CPU).

### E. Smaller items (verified, low)

* `ResolveProfile` and generated-voice creation run under the container lock (`ServiceContainer.cs:1284-1290`), blocking `RoutedSynthesizer`/`SpeechQueue` getters and UI reads for the duration of two file writes. Move the file writes out of the lock.
* `ChatterboxSynthesizer.IsReady` re-stats about a dozen files per call (`FindNotReadyReason`, `AnySetInstalled`), and the router and handler call it several times per line (`ExpressiveRouter.cs:114`, `SpeechRequestHandler.cs:128`). Slow on Wine's file system; cache for 2 s as Kokoro already does (`KokoroSynthesizer.cs:125-130`).
* "Skip lines that arrive late" (`StaleLineSeconds`, default 20 s, `PlaybackSpeechQueue.cs:136-147`) is measured from line arrival, per chunk. A long line rendered at RTF above 1 can lose its last sentences. Count it as a symptom, not a cause.

## 3. Mid-line pauses of 2 to 5 s: underruns of the streamed line (high; mechanism verified, magnitude from the coordinator's measurements)

### Mechanism

* The sink never inserts silence between windows on purpose and never trims silence. Silence appears only when the device asks for samples and the stream is empty: `PcmSourceSampleProvider.Read` zero-fills the shortfall (`NAudioSink.cs:286-300`), and `PolishedPcmSource.Read` returns 0 when `buffered <= fadeSamples` (`PolishedPcmSource.cs:44-51`). So any pause means the decoder had no audio ready.
* The schedule is just-in-time with almost no buffer. The first window is 32 tokens = 1.28 s of audio, minus the 80 ms held for the cross-fade (`ChunkStitcher.cs:47-60`). The second window is 48 tokens and is released only when 32 + 48 + 6 = 86 tokens exist (`ChunkSchedule.cs:114-117`). Playback therefore has about 1.2 s to get from token 38 to token 86 AND decode a 100 + 12 + 54 token window (about 1.1 to 1.4 s idle on 2 threads). Idle that works (86 tokens at 100 per second = 0.86 s; 0.5 s of slack). At 40 tokens per second the second window arrives about 0.2 s late; at 20 per second it is a 2 s hole; the later windows (72, 108, 120 tokens) are bigger holes. Each window needs both the LM to be far enough ahead AND the decoder to finish, and the decoder (RTF 0.6 to 1.0 even before contention, spike: +28 percent CPU for chunking) leaves no margin.
* The final window cannot start before the LM finishes: while the stream is open a chunk needs `end + 6` tokens, and the closing silence tokens only exist after STOP (`ChunkSchedule.cs:96-121`, `ChatterboxSynthesizer.cs:676`). For the example, the tail window ("... it is forever.") is decoded only after the LM's last token plus one decoder call. With LM time varying between 1.5 and 7.2 s (RTF up to 1.29), the audio from windows 1 and 2 (3 to 4 s) runs out long before. That is a 2 to 5 s hole after the last full window, matching "stops after 'serve'".
* Sentence boundaries restart everything (verified): `SpeakAsync` awaits each chunk's `StreamChunkAsync` in a loop (`SpeechRequestHandler.cs:144-188, 157`), so sentence 2 starts only after sentence 1 finished RENDERING. When rendering ends about when playback ends (RTF about 1) the listener hears the full cold start of sentence 2 again: prefill, 38 tokens, first decoder window, new device open: estimate 1.5 to 4 s, and each sentence also repeats the 100-token decoder prompt and the 150-frame LM prefill. The example is three chunks of 46, 72 and 88 characters, because `SentenceChunker` merges to 40 and splits only at terminators, never at commas or em dashes (`SentenceChunker.cs:61-72`). So a pause after "Raubahn Aldynn." or "Traders." is this mechanism; a pause after "serve," is a window underrun inside sentence 3.
* Alternative explanation to rule out: the model itself generating a long run of silence tokens after a comma. Discriminate in the log: if every `emit` arrived after its predecessor's audio ended it is an underrun; if the cached whole line (`SynthesisCache`) contains a quiet stretch, it is the model. The stage timings below separate the two.

### Fix options, ranked

1. Adaptive pre-roll (recommended, do first). Start the sound only when the audio already decoded can carry playback until generation finishes. Let `R` = audio seconds still to be produced, `q` = production rate in audio seconds per second (`min(LM tokens per s, 25 / decoder_cost_per_audio_second) / 25`), `tail` = decode time of the final window. No underrun requires `buffered >= max(minPreroll, R * (1 / q - 1) + tail + margin)`. With `q >= 1` the requirement collapses to `minPreroll + tail`; with `q` below 1 it grows until the whole line is buffered, which is the right answer when the machine cannot keep up (a clean line after a wait beats a stuttering one). Inputs are all in `RunStreaming`: tokens so far and elapsed (LM rate, smoothed over the last 20 tokens), the total estimated as `max(tokens so far, 0.68 * chars)` (156 tokens for the 231-character example) and refined when the LM stops, and the measured decoder seconds per audio second from completed windows. Hold the first windows in a local list and call `emit` for all of them the first time the rule is satisfied or a cap is hit (`MaxPrerollMs`, default 6000, an Advanced setting; the player sees "Starting soon" in Now Speaking). Also release immediately when the LM finished (nothing left to wait for) or the line is shorter than `minPreroll`. Where: `ChatterboxSynthesizer.cs:654-706` (new `PrerollGate` class under `Domain/` so it is unit-testable with a fake clock; `StreamingStats` gains `PrerollMs` and `Underruns`). No port change: the handler's `OnSamples` simply fires later, and the queue item is created then (this conflicts with item 2 of section 2.D; if the item is enqueued early, give `SpeechAudioStream` a `StartGate`, a `bool ReadyToStart` the pre-roll sets, and have `PlayStream` wait on it, which keeps the early device open).
2. Make chunks cheaper so `q` rises: decoder threads 2 (section 2.C), decoder prompt 100 to 80 for non-first windows (spike: WER unchanged, cosine 0.900), larger `LookaheadTokens`/`FirstTokens` only if pre-roll is in place.
3. Stop restarting at every sentence. When streaming audio is available, group sentences up to about 300 characters (about 200 tokens, below `MaxNewTokens = 256`, `TurboDecodeLoop.cs:25`) into ONE render, and split only longer lines at sentence boundaries. Where: `SentenceChunker.Split(text, maxChars)` plus the call at `SpeechRequestHandler.cs:136-138`. Removes the repeated prefill, device open and cold TTFA between sentences for ordinary dialogue; the example becomes one stream. The 40-character merge rule stays for the Kokoro path.
4. Pipeline the sentence boundary instead (only if 3 is not enough for long lines): start the LM of sentence N+1 while the decoder finishes sentence N. Needs the single `synthesisGate` split into an LM gate and a decoder gate (`ChatterboxSynthesizer.cs:55`) and a hand-off in the handler; high complexity for the engineer track.
5. Holding back only the last short window: weaker than pre-roll. It cures the final-window hole only if the whole tail is held, and it does nothing for the middle windows or the sentence restarts. Not recommended on its own.
6. Softer failure when an underrun still happens: fade out to silence and fade in when data returns instead of the hard zero fill and abrupt resume (`NAudioSink.cs:286-300`), and count it for the report.

Best choice: 1 plus 3. Pre-roll is the general cure (it turns an unpredictable stutter into a predictable wait whose length is the machine's real shortfall), and 3 removes the structural restart that pre-roll alone cannot hide.

## 4. Hot reload and the GPU runtime (the coordinator's hypothesis)

What the code does, verified:

* `WebGpuRuntime.EnumerateCore` (`Infrastructure/Onnx/WebGpuRuntime.cs:105-153`) copies `vulkan-1.dll` beside the EP dll (Wine), `NativeLibrary.TryLoad`s the Dawn companions (`dxil.dll`, `dxcompiler.dll`, handles kept in a static list that is never freed, `NativeLibraryPreloader.cs:25-31`), then calls `OrtEnv.Instance().RegisterExecutionProviderLibrary("aiva_webgpu_ep", path)`. A second registration that throws "already" is treated as success (lines 125-134) and the code goes on to `GetEpDevices()`.
* Nothing is ever undone. There is no `UnregisterExecutionProviderLibrary`, no `IDisposable` on `WebGpuRuntime` or `OnnxSessionFactory`, the `OrtEnv` singleton is never disposed, the `OrtMemoryInfo` from `devices[0].GetMemoryInfo` is cached (lines 80-89) but never released, and `ServiceContainer.Dispose` (line 656 onward) only disposes engines. The `Chatterbox` engines dispose their sessions (`Engine.Dispose`, `ChatterboxSynthesizer.cs:1185-1199`).
* The failure is cached per `WebGpuRuntime` instance (`cached`, line 78) and the instance dies with the container, so a reload retries; but a process-level failure repeats.

What I cannot establish from here (needs a run inside the game): whether, after the plugin AssemblyLoadContext unloads, the managed `OrtEnv` is finalized (then the native env is destroyed, the EP library freed, and Dawn's static state torn down inside a DLL that is then loaded again), or kept alive (then the second load sees "already registered" and hands out device wrappers from the first load). Either can fail under Wine; the EP is pinned to 0.2.1 and was never tested reload-wise (spike: "Not tested: in-process inside Dalamud, a second Vulkan instance next to DXVK"). Dalamud's dev-plugin reload may also load from a different (shadow) directory each time while the first path stays mapped. All of these end in the same visible symptom: the second `Enumerate()` returns `Unavailable`, or `Append`/session creation throws, or (worst) a native abort, which the crash guard catches on the next start.

Whatever the reason, the UI would not say "Ready" for it (section 0): `CreateTurboEngine` turns a GPU build exception into `FellBack` with "The GPU could not be started, so Chatterbox Turbo runs on the CPU." and `CanRetryGpu` (`ChatterboxSynthesizer.cs:956-980`). The reload hypothesis is therefore compatible with the symptom "slow lines" but not with the exact card text "Ready - Running on your processor".

Proposal:

1. Detect reloads plainly. Keep a process-wide load counter that survives the AssemblyLoadContext: `AppDomain.CurrentDomain.GetData/SetData("AIVoiceActing.Loads", n)` (AppDomain data is not collectible) or an environment variable. Log `Plugin load #n in this process` at construction (`AIVoiceActingPlugin` constructor) and add it to the diagnostics report.
2. When load number is above 1 AND the GPU bring-up fails or the runtime reports "already registered", set the engine reason to: "The graphics card cannot be restarted after the plugin was reloaded while the game was running. Restart the game to use the graphics card again." through a new `DevicePlan` reason on that path (`WebGpuRuntime.EnumerateCore` returns it as `Failure`), with `CanRetryGpu = false` so the banner does not offer a retry that cannot work. A reload where the GPU came up fine needs no message.
3. Make reload work if it can. Experiment first (engineer task): a SmokeSynth mode that, in one process, creates a `WebGpuRuntime`, builds the LM session, runs 10 tokens, disposes everything, then repeats; then the same inside a collectible AssemblyLoadContext under the XIVLauncher Wine build. Expected outcomes: (a) works: nothing to do; (b) fails only after the ALC unload: root `OrtEnv.Instance()` and the `OrtEpDevice` list in a static for the process lifetime and never unregister (intentional leak; the leaked ALC is Dalamud's warning, not a crash); (c) fails regardless: step 2 only.
4. `IDisposable` for `WebGpuRuntime`: dispose `deviceMemory`, drop `devices`; do NOT unregister the library while any session might still exist. Called from `ServiceContainer.Dispose` after the synthesizers.

## 5. Why the card hides the reason, and what to show (verified)

* `EngineViewModel.Describe` (`UI/State/EngineViewModel.cs:44-60`): for `Ready` it shows only `WhereItRuns` ("Running on your processor."), throwing away `status.Reason`. For `FellBack` it shows the reason verbatim. For the fp32 pin the reason string exists ("Running the full-precision voice files on the CPU, as you chose.") but belongs to a `Ready` state, so it is dropped.
* The Processor card/banner (`UI/State/DeviceViewModel.cs:160-200`) renders a banner only for `FellBack` and `Failed`, so `Ready`+CPU with Mode gpu raises none.
* `DiagnosticsReport` (`UI/State/DiagnosticsReport.cs:34-59`) prints State and Reason in `EngineLine`, but not the precision, the loaded file set, `StreamSentences`, `UniquePerCharacter`, the thread count, or the plugin load number.

Fix:
1. `ChatterboxSynthesizer.Resolve` (line 885-888): an explicit fp32 with Mode other than cpu is a CPU plan with `wantedGpu: true` and the reason "You picked the full-size voice files under Advanced > Precision. They only run on the processor. Choose Automatic to use the graphics card." State becomes `FellBack`, banner and Retry appear, the card says why. Test: `ChatterboxSynthesizerStatusTests` for (Mode gpu, Precision fp32) = FellBack with that reason.
2. `EngineViewModel.Describe`: for `Ready` on the CPU when `RequestedDevice != Cpu` append the reason: "Running on your processor. {reason}".
3. `QualityTierModel` / Home: when the tier is "Most expressive" or "Balanced" and the active device is the CPU, show a warning line with the reason; a pinned precision that contradicts the tier is the most likely case.
4. Add to the diagnostics report: precision requested and in use (`ChatterboxSynthesizer.EffectivePrecision`), LM device and adapter, `StreamSentences`, `LevelVoiceLoudness`, `UniquePerCharacter`, effective CPU threads for the decoder, ORT spinning, the plugin load number, and the stage timings below.

## 6. Test checklist for the player (before any code)

1. Advanced > Precision: Automatic. Retry on GPU. The card must say graphics card.
2. Background load "Medium" (4 threads), then "Low" (2 threads): if first-line time drops, section 2.C is confirmed.
3. Settings > Processor > Preference: Low power (if an iGPU is listed), speak the same line.
4. Same quest, second time (reference clips cached): the 5 to 10 s start should shrink to the base time if section 2.A was the cause.
5. Copy details after a slow line once section 7 exists.

## 7. Stage timings in the diagnostics report (propose and implement)

Today the numbers exist only as `Info` log lines (`ChatterboxSynthesizer.cs:484, 534, 643`) and the old Dalamud log on this machine holds only `[ERR]` entries from this plugin, so players cannot paste them.

Design (hexagonal, census style):

* `Ports/LineTimingLog.cs`: DTO `LineTiming` (sentinel exceptions none, record, then interface `ILineTimingLog { void Record(LineTiming t); IReadOnlyList<LineTiming> Recent(int max); }`).
* `LineTiming` fields, all in ms, no text or names: `LineChars`, `Chunks`, `QueueWaitMs` (arrival to the start of `SpeakAsync` work: channel plus previous line's render), `ProfileMs`, `PlanMs`, `ReferenceKind` (cached / pack clip pitched / rendered / default), `ReferencePrepMs` (Kokoro render), `EncoderMs` (0 on a hit), `GateWaitMs`, `PrefillMs`, `LmTokens`, `LmMs`, `LmMsPerTokenP50/P95`, `FirstWindowReadyMs` (tokens 38 available), `FirstDecodeMs`, `DecoderMsTotal`, `Windows`, `FirstAudioMs` (arrival to first sample into the stream), `PrerollMs`, `DeviceOpenMs` (dequeue to `output.Play()`), `StartToSoundMs` (arrival to the device receiving the first non-silent sample), `UnderrunCount`, `SilenceMs` (zero-filled samples counted in `PcmSourceSampleProvider.Read`), `TotalRenderMs`, `AudioMs`, `Engine` ("turbo gpu q4f16"), `Streamed`.
* Collect in `ChatterboxSynthesizer.StreamCore/RunStreaming/SynthesizeCore` (it already holds most clocks), `ServiceContainer.PrepareExpressiveVoiceAsync` (reference), `SpeechRequestHandler.SpeakAsync` (arrival, plan), `NAudioSink.PlayStream` (device open, first sample) and hand them to the log through the `SpeechItem`/`StreamingStats` path; a `LineTimingBuilder` keyed by `lineId` merges the pieces. Ring buffer of the last 20 lines in `Infrastructure/Diagnostics/RingLineTimingLog.cs`.
* Per-token LM latency: a small `LatencyHistogram` in `TurboDecodeLoop.Run` (a stopwatch around `RunLanguageModel`, kept as p50/p95/max), exposed on `StreamingStats`. This is the number that shows GPU contention.
* Report: `DiagnosticsInput` gains `IReadOnlyList<LineTiming> RecentLines` and `ConfigFlags`; `DiagnosticsReport.Build` appends a "Recent lines (newest first)" section, one line each, for example `  #12 3 chunks  start 6.8 s [queue 0.0 ref 3.1 (rendered) enc 0.8 gate 0.0 prefill 0.2 first window 1.1 decode 0.9 device 0.4]  LM 156 tok 4.9 s (p50 21 ms, p95 63 ms)  underruns 1 (2.4 s silence)`. `UI/Pages/DiagnosticsSource.cs:9-52` passes the ring buffer from `UiContext`. Wire the log in `Container/ServiceContainer` as a lazy port accessor (`LineTimingLog`).
* Tests: `DiagnosticsReportTests` (stable format, no names or text), `RingLineTimingLogTests` (capacity, order), `PrerollGateTests` (fake clock: fast LM starts at min pre-roll, slow LM waits, cap releases, LM finished releases), `PolishedPcmSource`/sink fake counts underruns, `ArchitectureScanTests` stays green (port in `Ports/`, adapter in `Infrastructure/`).

## 8. Implementation order

| Step | Change | Files | Track | Expected gain |
|---|---|---|---|---|
| 1 | Status honesty: fp32 pin is `FellBack` with reason, Ready+CPU shows reason, tier warning, diagnostics additions (precision, flags, load counter) | `ChatterboxSynthesizer.cs:885-888`, `UI/State/EngineViewModel.cs`, `QualityTierModel.cs`, `DiagnosticsReport.cs`, `DiagnosticsSource.cs`, tests | implementer | removes the silent CPU state |
| 2 | Stage timings and ring buffer (section 7) | `Ports/LineTimingLog.cs`, `Infrastructure/Diagnostics/`, `ChatterboxSynthesizer.cs`, `TurboDecodeLoop.cs`, `SpeechRequestHandler.cs`, `NAudioSink.cs`, `ServiceContainer.cs` | implementer | makes everything below measurable |
| 3 | Decoder threads cap at 2, spinning switch for the decoder | `OnnxSessionFactory.cs:88-95`, `OrtThreading.cs`, Advanced section | implementer | 1 to 2 s on first window under load (estimate) |
| 4 | Warm-up runs a real short line and the default clip, plan-based `SupportsStreaming`; warm the reference Kokoro at login | `ChatterboxSynthesizer.cs:138-151, 561`, `AIVoiceActingPlugin.cs:524`, `ServiceContainer.cs:569` | implementer | 5 to 15 s off the first line of a session |
| 5 | Adaptive pre-roll (`PrerollGate`) | `Domain/PrerollGate.cs`, `ChatterboxSynthesizer.cs:654-706`, `StreamingStats`, Advanced setting `MaxPrerollMs` | engineer | removes mid-line pauses; start time becomes honest |
| 6 | Merge sentences into one stream up to 300 chars when streaming | `SentenceChunker.cs`, `SpeechRequestHandler.cs:136-143` | implementer | removes per-sentence restarts |
| 7 | Reference prep: 5 s passage, disk-cached embeddings, optional "faster first line" | `ServiceContainer.cs:588, 717-770`, `ChatterboxSynthesizer.cs:738-814` | engineer | 1 to 3 s per new voice |
| 8 | Open the device before the first samples; later a persistent output | `SpeechRequestHandler.cs:216-240`, `NAudioSink.cs` | engineer | 0.2 to 0.8 s per start, and per sentence |
| 9 | Reload handling: load counter, plain-text "restart the game" reason, SmokeSynth/ALC experiment, `IDisposable` runtime | `WebGpuRuntime.cs`, `AIVoiceActingPlugin.cs`, `tools/SmokeSynth` | engineer | clear message, maybe working reload |
| 10 | First window 20 tokens with a 60-token prompt (after 5) and GPU preference hint for dual-GPU machines; remove or wire `GpuImpact` | `StreamingOptions.cs`, `ChunkSchedule.cs`, `ConfigurationSections.cs:76`, device UI | implementer | 0.4 to 0.8 s |

Do steps 1 and 2 first; they are small and turn the rest of this document from estimates into numbers. Re-rank steps 3 to 8 from the first player report with section 7 data.
