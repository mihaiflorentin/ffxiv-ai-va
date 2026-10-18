# GPU audio artifacts: "cuts off" and "weird sounds" (root cause and fix)

Date: 2026-10-18. Plugin 0.0.36 (HEAD 5aa590f plus the coordinator's uncommitted `TokenBudget` change). Player report: Chatterbox Turbo q4f16 on an RX 9070 XT (WebGPU EP 0.2.1, Vulkan, Wine), Most expressive, "Start long lines sooner" on: "the GPU is buggy, it cuts off and i hear weird sounds". Earlier the same player had Turbo on the CPU (fp32, whole line) and heard no artifacts. Later reports: awkward pauses mid-line also with streaming off, and a 150-char Chuchuto line that never ends.

No repo source was changed for this investigation. All experiments ran on private copies under `/tmp/claude-1000/-home-mihai-Workspace-ffxiv-ai-va/0e3c33d9-084a-4d3d-860a-0b102b03b3ff/scratchpad/gpuart/` (`snap2/` is a copy of the working tree with env-var experiment knobs in `TurboDecodeLoop.cs`; `out/<config>/l-N.wav|tokens` hold every sample; `py/` the analysis scripts). That tmpfs is volatile. The shell in this session could not write under `models/samples/artifacts/`, hence the scratchpad.

## Verdict

1. **The GPU is not buggy.** The WebGPU EP 0.2.1 runs the q4f16 language model correctly: against the same graph on the CPU, teacher-forced over 10 lines (~950 steps), top-1 agreement is 97.6 %, KL 0.000, median max |logit difference| 0.07 (fp16 noise). GPU tokens are bit-identical between runs and between the streamed and whole-line paths. Encoder, embedding and decoder never leave the CPU (`OnnxSessionFactory.cs:69-84`: only `SessionRole.Lm` with a GPU plan gets the GPU; the decoder is CPU capped at 2 threads).
2. **What changed with the move to the GPU is the precision, not the device.** The CPU fallback is fp32 (`ChatterboxSynthesizer.CpuFilesFor`), the GPU path is q4f16. The q4f16 language model is a materially different model: teacher-forced on the fp32 token path, top-1 agreement 53 %, KL 0.29 nats (same number on CPU q4f16 and GPU q4f16). Under the card's recipe (greedy argmax plus repetition penalty 1.2 over the **entire** history) that model derails far more often than fp32: it emits runs of silence tokens (pauses of 1.5 to 7 s in the middle of a line) or never emits STOP and fills the budget with silence and murmur (24.9 s from a 141-char line, 40 s from a 326-char line). fp16 on the GPU derails too, less often. Real reference clips (Kokoro-generated, pitched) and director tags (`[sigh]`, `[gasp]`) make it much worse than the bundled default clip.
3. **Fix: penalise only the last 32 tokens, and cap identical-token runs at 12** (`TurboDecodeLoop.cs`). With it q4f16 on the GPU matches fp32 on the CPU for word error rate and has zero runaways in 56 test renders, including real clips and tags. This is the main fix and it is small.
4. **Streaming adds its own "cuts off"**: with the GPU shared with the game the language model is slower than real time and the schedule has no cushion, so every line over 1.3 s plays 1.2 s, goes silent for about 1.1 s, then continues (21 gaps, 24.8 s of silence over 14 lines in my run), and each gap is a hard cut that clicks. The pre-roll work the other engineer is doing addresses that; it does not touch the derailment.
5. Windows seams, the encoder/decoder q4f16 choice and KV state carry-over were checked and are **not** the cause.

## Evidence

Method for everything below: SmokeSynth built from a copy of the source, `--device gpu --precision q4f16 --power high`, models copied to a scratch directory (a symlinked models dir fails the readiness check; the harness writes the crash marker beside the models). The 9070 XT was shared with another job at 100 % busy and 8.6 GB VRAM for the whole session, which is a realistic stand-in for the game: language model 46 to 55 ms per token (about 20 tokens/s, below the 25/s real time). WER: faster-whisper small.en on CPU, normalised words. "Runaway" = more than 2.4 tokens per character (normal is 1.2 to 1.6); "mid-sil" = total silence of at least 0.4 s inside the line (not at its edges). Default reference clip unless stated.

### (b) Language model: device versus precision

| Comparison (teacher-forced on the fp32 token path, 10 lines) | top-1 agreement | KL | median max abs logit diff |
|---|---|---|---|
| q4f16 GPU vs q4f16 CPU | 0.976 | 0.000 | 0.07 |
| q4f16 CPU vs fp32 CPU | 0.528 | 0.292 | 3.90 |
| q4f16 GPU vs fp32 CPU | 0.530 | 0.292 | 3.90 |

Free-running token streams: q4f16 GPU and q4f16 CPU diverge from each other early (greedy is a knife edge on 0.07 logit noise) but both contain the same kind of 40 to 60 token pause runs; fp32 does not (3 of 30 lines with a run of 12 or more identical tokens, against 10 of 30 for q4f16 GPU and 6 of 30 for fp16 GPU).

Pause tokens are 4137 and 6324 (decoded audio RMS 0.0000). Run lengths seen on q4f16: 55 tokens (2.2 s) before "willing to serve ... , for it is forever", 51, 39, 46, 60.

### The decoding experiments (q4f16, GPU, whole-line path, 16 lines including the Chuchuto line)

| Decoding | WER | runaways | tok/char | mid-sil total | longest |
|---|---|---|---|---|---|
| **Current: greedy, penalty 1.2 over the whole history** | 0.069 | 1 | 1.52 | 4.7 s | 2.1 s |
| fp32 CPU, same current recipe (the old behaviour) | 0.022 | 0 | 1.41 | 4.2 s | 2.4 s |
| greedy, no penalty | 0.677 | 15 | 4.76 | 67.7 s | 15.2 s |
| greedy, penalty over last 16 | 0.030 | 0 | 1.32 | 4.3 s | 1.9 s |
| **greedy, penalty over last 32** | **0.022** | **0** | 1.25 | **0.0 s** | 0.0 s |
| greedy, penalty over last 64 | 0.043 | 0 | 1.28 | 3.4 s | 1.6 s |
| greedy, penalty over last 128 | 0.029 | 0 | 1.39 | 10.9 s | 2.4 s |
| greedy, penalty 1.2 whole history, identical run capped at 8 | 0.022 | 0 | 1.28 | 4.6 s | 1.1 s |
| sampling at upstream settings (T 0.8, top-k 1000, top-p 0.95, penalty 1.2 whole history), seed 1 | 0.090 | 0 | 1.22 | 1.7 s | 0.4 s |
| same, seed 2 | 0.035 | 0 | 1.25 | 2.0 s | 1.1 s |
| same, no penalty | 0.057 | 0 | 1.39 | 10.0 s | 1.4 s |
| fp16 GPU, current recipe | 0.040 | 1 | 1.44 | 10.9 s | 2.3 s |
| fp16 GPU, window 32 | 0.022 | 0 | 1.35 | 5.5 s | 2.0 s |

Chuchuto line ("Nothing is more important to a pugilist than stamina. And how does one attain such a thing? By eating well, sleeping well, and training well!"): current recipe 619 tokens, 24.9 s, whisper hears "writing is more important to a future list in stamina ... Why are you eating well?"; fp32 CPU 251 tokens, correct; window 32 168 tokens, 6.8 s, correct; fp16 greedy 207 tokens, correct.

Five long lines (184 to 326 chars, the 1000-token budget now applies): current recipe WER 0.24, mid-sil 27.4 s, one line runs to 999 tokens (40.1 s); window 32 WER 0.028, mid-sil 4.7 s, all lines stop by themselves; upstream-style sampling WER 0.067.

Thirty short lines (76 to 117 chars): current recipe WER 0.034, 2 runaways, longest pause 7.5 s, 29.2 s of mid-line silence in total; window 32 WER 0.026, no runaways, longest pause 1.8 s, 12.2 s total; fp32 CPU WER 0.018, longest 1.9 s, 8.9 s.

Real reference clips and tags (8 lines each, all q4f16 on the GPU):

| Reference / tag | Current recipe: WER, runaways, mid-sil, longest | Window 32: same | Window 32 + run cap 12 |
|---|---|---|---|
| clip `bf_gen_..._p130` (pitched 1.30) | 0.060, 2, 18.1 s, 2.7 s | 0.063, 0, 4.5 s, 1.0 s | not run |
| clip `bf_lily_p112` | 0.060, 3 (one 24.9 s with a 16.7 s dead tail), 17.1 s, 5.7 s | 0.052, 0, 4.4 s, 1.6 s | 0.044, 0, 4.0 s, 0.8 s |
| clip `bm_lewis_p086` | 0.232, 0, 7.7 s, 2.4 s | 0.093, 0, 7.1 s, 1.9 s | 0.106, 0, 5.3 s, 0.7 s |
| tag `[sigh]` | 0.131, 2, 17.2 s, 7.5 s | 0.035, 0, 2.0 s, 0.6 s | not run |
| tag `[gasp]` | 0.059, 2, 11.3 s, 2.5 s | 0.035, 1, 3.3 s, 2.0 s | 0.049, 0, 4.0 s, 0.8 s |

With window 32 alone, identical-token runs of 30 to 46 tokens (1.2 to 1.8 s of silence) still occurred with the `bm_lewis` clip and with `[gasp]`; the run cap removes them (longest mid-line pause 0.7 to 0.8 s in all three runs that used both).

### Why the full-history penalty breaks

The penalty divides the score of every token that appeared anywhere in the line by 1.2. Phoneme tokens are reused all the time, so deep into a line most of the model's natural choices are penalised while tokens it never used (the pause tokens 4137 and 6324 among them) are not. With q4f16's flatter distributions that tips the argmax into pause runs, and a repeated phrase ("eating well, sleeping well, and training well") makes it worse. Upstream avoids this by sampling (temperature 0.8) rather than by a different penalty: the PyTorch `generate()` defaults are temperature 0.8, top-k 1000, top-p 0.95, repetition penalty 1.2 over the full generated sequence (`chatterbox/tts_turbo.py` and `models/t3/t3.py`, ResembleAI chatterbox-turbo-demo Space); the ONNX model card example is the greedy variant the plugin copied, with `max_new_tokens = 1024`. So the plugin runs the card's sample code against a quantised model that sample code was not tuned for. Sampling also cures it (table), but it makes renders non-repeatable and had one noisy seed (WER 0.09), so the windowed greedy is the better trade here.

### (a) Streamed versus whole decode, seams

* Same tokens, streamed and whole: WER on lines 1 to 10 is 0.021 against 0.029 (identical within noise). Streamed and whole token streams are identical (GPU deterministic).
* The vocoder is not deterministic: decoding the same window twice gives a waveform correlation of 0.1 to 0.9, so no sample-level seam comparison is possible. I re-implemented the stitcher in Python (`py/dec.py`, same lengths as the C# output to the sample) and measured the 10 seams of lines 1 to 10: the equal-power sum stays within 1.6 dB of the expected level (mean 0.78 dB), the mel distance between the two sides of a seam is 1.18 against 0.97 for the same window decoded twice, and the mel distance near seams against the whole decode is the same as elsewhere (1.00 against 1.05). The two decodes of one cross-fade region differ in level by up to 4 to 5 dB on three of ten seams (-4.4, -4.9, +4.8 dB), which is a small audible level step spread over 80 ms. **Seams are not the cause of the player's report.**
* Peaks: the q4f16 decoder clips less than fp32 (fp32 whole line: mean 11.9 samples at full scale per line; q4f16 streamed 1.5).

### (c) Cut-offs

1. **Token budget** (fixed in the working tree). HEAD had `MaxNewTokens = 256`. Speech runs at 1.2 to 1.5 tokens per character (1.8 to 2.4 with slow or pitched-down clips), so 256 tokens is about 170 to 210 characters. Lines 11 to 13 of my set (184 to 326 chars) were all cut at exactly 256 tokens. **Warning for `SentenceChunker.SplitForStreaming(…, 300)`** (uncommitted): 300 characters is about 360 to 450 tokens, so a merged chunk can only finish if the budget is at least that; with `TokenBudget` as now (`60 + 16 × text tokens`, up to 1000) it is fine, but the old estimate "0.68 tokens per char" (docs/plans/2026-10-line-latency.md section 3) was wrong by a factor of two. `PrerollOptions.TokensPerChar = 1.6` is right for normal clips; slow or pitched-down clips reach 2.0 to 2.4.
2. **Derailment** (the main one): the budget turned the old cut into a long silent tail instead of a truncation; see above.
3. **Underruns while streaming.** Emit times from the instrumented harness (`py/underrun.py`, GPU shared, language model 46 ms/token): the first window (1.24 s of audio) is ready after 2.3 s; the second arrives 1.1 s after the first has finished playing. Per line: 0.4 to 1.2 s of silence for lines over 1.3 s, 4.3 to 4.5 s in three gaps on the 10 s lines, 21 gaps and 24.8 s over 14 lines. `PolishedPcmSource.Read` (`Domain/PolishedPcmSource.cs:44-51`) returns what is buffered minus the 6 ms held back and then 0, and `PcmSourceSampleProvider.Read` zero-fills; there is no fade at the start of a gap or when data returns, so each underrun is a step discontinuity at the sample where the data ran out (|x| = 0.03 to 0.20 at the cuts in this run). That is the "weird sound" component that is specific to streaming. The other engineer's pre-roll removes most gaps; add a short fade-out on the last delivered samples and a fade-in after a gap anyway (`NAudioSink.cs:286-300`, `PolishedPcmSource.cs`).
4. A mid-stream failure ends the line (`SpeechRequestHandler.StreamChunkAsync`, catch-all, `return false`, which drops the remaining sentences). I did not provoke one; no exception occurred in any run. Worth a log line carrying the exception type so a player report can say whether this happened.

### (d), (e)

* Encoder, embedding, decoder: CPU in every GPU plan (code above; the harness log shows "language model on WebGpu" only). The q4f16 encoder and decoder replace the old fp32 ones on the CPU path; across my renders lines 1 to 10 are as intelligible as fp32 (WER 0.021 to 0.033 against 0.019 to 0.022) and the spike measured the same speaker likeness, so they are not the cause.
* KV cache and IoBinding: every `Run` starts with empty past (`past = new OrtValue?[...]`), previous outputs are disposed at the end. Fourteen lines run back to back in one process produced the same tokens as fresh processes, and streamed equals whole. No leakage between lines or sentences.
* Adapter: the harness prints the first adapter (0x13c0, the iGPU) as the "active" device, but `--power high` runs on the 9070 XT (card1 at 100 %, card0 at 0 %), as the spike found. The status text is misleading for dual-GPU machines (`status: active WebGpu on AMD GPU (device 0x13c0)`); cosmetic.

## Recommended fix

### 1. Windowed repetition penalty plus identical-run cap (implementer, ~1 hour, high value)

`src/AIVoiceActing/Infrastructure/Onnx/TurboDecodeLoop.cs` (line numbers of the current working tree):

* `:40` next to `RepetitionPenalty` add

  ```csharp
  /// <summary>The penalty looks at this many of the latest tokens only. Over the whole history (the card's recipe) the
  /// q4f16 model drifts into pause runs and never stops; 32 matches fp32 (docs/plans/2026-10-gpu-audio-artifacts.md).</summary>
  public const int RepetitionWindow = 32;

  /// <summary>The same token is never picked more than this many times in a row (12 tokens = 0.5 s).</summary>
  public const int MaxIdenticalRun = 12;
  ```

* `:97` delete `var seen = new HashSet<long> { StartSpeechToken };` and `:168` `seen.Add(next);`.
* `:157` replace `ApplyRepetitionPenalty(logits, seen);` with `ApplyRepetitionPenalty(logits, RecentDistinct(generated, RepetitionWindow));` (`generated` still starts with the START token, so the window includes it for the first 31 steps, as before).
* After the existing STOP mask at `:161-164` and before `Argmax` at `:166` add `MaskIdenticalRun(logits, generated, MaxIdenticalRun);`.
* Helpers beside `ApplyRepetitionPenalty` (`:293`), both `internal static` for tests:

  ```csharp
  internal static IEnumerable<long> RecentDistinct(List<long> generated, int window) =>
      generated.Skip(Math.Max(0, generated.Count - window)).Distinct();

  internal static void MaskIdenticalRun(float[] logits, List<long> generated, int maxRun)
  {
      if (generated.Count < maxRun) return;
      var last = generated[^1];
      for (var i = 2; i <= maxRun; i++) if (generated[^i] != last) return;
      if (last >= 0 && last < logits.Length) logits[(int)last] = float.NegativeInfinity;
  }
  ```

  Note the mask must not hit STOP (it cannot: STOP ends the loop when picked).

My prototype of exactly this (env-var driven, in `snap2/.../TurboDecodeLoop.cs`, method `ExpPick`) produced the tables above with `AIVA_PENWIN=32 AIVA_RUNCAP=12`. Do not copy the prototype (it re-sorts the vocabulary for sampling).

Tests (`tests/AIVoiceActing.Tests/TurboDecodeLoopTests.cs`): `RecentDistinct` returns only the last N and de-duplicates; the penalty leaves a token alone once it is older than the window; `MaskIdenticalRun` masks after exactly 12 equal tokens, not after 11 or after 12 mixed ones; STOP is never masked.

Cache: `RenderVariant` (`ChatterboxSynthesizer.cs:124-132`) feeds the render-cache key; add a decode-recipe suffix (for example `"/d2"`) so a previously cached derailed render is not replayed after the update.

Verify in game or with SmokeSynth after the change: `lines_exp.txt`-style set (the Chuchuto line, the ten chunked lines, a repeated-word line) must show no line above 2.4 tokens/char, no pause above 1 s, WER at or below 0.03.

### 2. Stop safeguard (implementer, optional, small; not exercised by my tests because no run needed it after fix 1)

In the loop, if `MaskIdenticalRun` fired three times in one line, treat the next step as STOP: the model is struggling and everything after is likely silence. Cheap, cannot hurt a normal line (no normal line in 56 renders reached 12 identical tokens twice after fix 1). Also consider lowering `TokenBudget`'s slope from 16 to 12 tokens per text token: normal lines used 5 to 7 speech tokens per text token; 12 still leaves headroom for slow voices and shortens the worst case from 25 s to 18 s. Not measured beyond that.

### 3. Streaming cushion and gap fades (engineer, in progress by the other engineer)

* Pre-roll sized from the measured language model rate: on a game-shared GPU the model produced about 20 tokens/s, below the 25/s that real time needs, so the correct pre-roll for a 100-token line is most of the line. Use `TokensPerChar` 1.6 (2.2 for lines when the voice is pitched below 1.0).
* Fade out the last 6 ms before a gap and fade in after it (`PolishedPcmSource`, `NAudioSink.cs:286-300`), and count the gap, so an unavoidable underrun is a soft pause and not a click.
* Do not merge sentences past what the budget and the pre-roll can carry (300 chars is about 400 tokens, 16 s at 20 tokens/s generation).

### 4. Smaller items

* Text normalisation: upstream `punc_norm` (tts_turbo.py) maps `…` to `, `, `:` to `,`, em/en dashes to `-`, curly quotes to straight, capitalises the first letter and appends `.`; the plugin only appends `.` (`ChatterboxSynthesizer.BuildPromptText`). FFXIV text is full of `…` and `—`. I did not measure its effect; worth a check against the lines with ellipses, because these are the punctuation marks most likely to provoke the pause tokens.
* Log the decode recipe and per-line stall counts (identical-run maxima, pause tokens) in `Turbo streaming:`/`Turbo timing:` lines and in the diagnostics report, so the next player report says whether it derailed.
* Consider fp16 as the GPU default instead of q4f16 only if fix 1 turns out to be insufficient on a player's machine; with fix 1 fp16 showed no extra benefit in WER or runaways and costs 1.0 GB more disk and a slower model.

## Limits of this work

* Nobody listened to the samples; all quality numbers are whisper WER, silence detection and token statistics. Window 32 gives no mid-line pause of 0.4 s or more in the 16-line set (the fp32 reference has 4.2 s of them: natural sentence pauses), so it may speak slightly more briskly (5.2 against 5.8 s per 100 characters). On the 30 short lines window 32 alone keeps natural pauses (12.2 s of them against 8.9 s for fp32), so the zero in the 16-line set is partly chance. A listening pass on `out/x-win32`, `out/L-win32` against `out/x-cpu32-greedy` is the one check I could not do.
* Eight to sixteen lines per configuration; the differences between the windowed and the full-history recipe are large (runaways 0 against 1 to 3 per 8 on real clips) but the ranking among window 16, 32 and 64 is within noise. 32 is the tested value, not a tuned optimum.
* Game-side behaviour (NAudio under Wine, device open cost, the real in-game GPU contention) was not run; the underrun numbers come from emit timestamps of the harness replayed against a perfect real-time consumer.
* Scratchpad hygiene: while setting up I overwrote a `lines.txt` at the top of the shared scratchpad directory (it now lives in `gpuart/`); if the coordinator relied on a file with that name there, it needs recreating.
