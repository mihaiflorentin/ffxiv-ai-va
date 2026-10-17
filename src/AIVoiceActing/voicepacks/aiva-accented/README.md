# aiva-accented: Accented English

Kokoro's stock non-English voices (Japanese, Mandarin Chinese, French, Spanish, Italian,
Brazilian Portuguese, Hindi) registered with an English phonemizer, so they speak English
with their native speaker's cadence and vowel colour. Apache-2.0, tensors by hexgrad
(Kokoro-82M); see `LICENSE` and `NOTICE`. All voices are marked experimental.

## Intelligibility gate

English words must still be pronounced properly, so every voice and phonemizer combination
was rendered and checked by machine (`tools/voicepack/accented_audition.py`):

- 8 test lines: FFXIV names (Ul'dah, Y'shtola, Gridania, Alphinaud, Alisaie, Thancred,
  Garlemald, Minfilia, Ishgard), long sentences, and hard English sounds (th, w/v, r/l).
- Each render is transcribed with faster-whisper `small.en` (CPU, int8) and compared with the
  text. WER is the word error rate over all 8 lines, case and punctuation ignored.
- Baseline: the stock English voices af_heart, am_michael and bf_emma on the same lines
  (mean WER 5.6 %, no mangled content words).
- A combination is kept only if its WER is at most the baseline mean plus 5 points (10.6 %)
  AND no content word was heard as another word. Invented names are exempt from the second
  rule (the ASR spells them differently for every voice, baselines included), and so are
  "the/a/and/of".
- The pack ships at most one woman and one man per accent, the lowest WER of those that pass,
  using the better of en-us / en-gb. The em_santa and pm_santa voices are novelty characters
  and are never shipped.

The ASR check proves the words are clear; it cannot judge how strong the accent sounds.
Listen to the previews.

## Audition results

| Stock voice | Phonemizer | WER % | Mangled words | Verdict |
|---|---|---|---|---|
| af_heart (baseline) | stock | 4.9 | 0 | reference |
| am_michael (baseline) | stock | 7.0 | 0 | reference |
| bf_emma (baseline) | stock | 4.9 | 0 | reference |
| ef_dora | en-gb | 6.3 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| ef_dora | en-us | 4.9 | 0 | shipped |
| em_alex | en-gb | 6.3 | 0 | shipped |
| em_alex | en-us | 6.3 | 1 | dropped |
| em_santa | en-gb | 4.2 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| em_santa | en-us | 4.9 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| ff_siwis | en-gb | 7.7 | 1 | dropped |
| ff_siwis | en-us | 7.7 | 0 | shipped |
| hf_alpha | en-gb | 7.0 | 1 | dropped |
| hf_alpha | en-us | 8.5 | 0 | shipped |
| hf_beta | en-gb | 7.0 | 1 | dropped |
| hf_beta | en-us | 9.2 | 1 | dropped |
| hm_omega | en-gb | 5.6 | 0 | shipped |
| hm_omega | en-us | 8.5 | 1 | dropped |
| hm_psi | en-gb | 5.6 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| hm_psi | en-us | 7.0 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| if_sara | en-gb | 4.2 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| if_sara | en-us | 3.5 | 0 | shipped |
| im_nicola | en-gb | 4.2 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| im_nicola | en-us | 4.2 | 0 | shipped |
| jf_alpha | en-gb | 7.7 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| jf_alpha | en-us | 7.7 | 1 | dropped |
| jf_gongitsune | en-gb | 12.0 | 4 | dropped |
| jf_gongitsune | en-us | 6.3 | 0 | shipped |
| jf_nezumi | en-gb | 7.7 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| jf_nezumi | en-us | 10.6 | 4 | dropped |
| jf_tebukuro | en-gb | 8.5 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| jf_tebukuro | en-us | 11.3 | 4 | dropped |
| jm_kumo | en-gb | 5.6 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| jm_kumo | en-us | 5.6 | 0 | shipped |
| pf_dora | en-gb | 6.3 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| pf_dora | en-us | 4.2 | 0 | shipped |
| pm_alex | en-gb | 5.6 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| pm_alex | en-us | 5.6 | 0 | shipped |
| pm_santa | en-gb | 4.9 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| pm_santa | en-us | 4.2 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| zf_xiaobei | en-gb | 8.5 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| zf_xiaobei | en-us | 7.0 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| zf_xiaoni | en-gb | 7.7 | 1 | dropped |
| zf_xiaoni | en-us | 6.3 | 0 | shipped |
| zf_xiaoxiao | en-gb | 7.0 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| zf_xiaoxiao | en-us | 7.7 | 1 | dropped |
| zf_xiaoyi | en-gb | 9.2 | 1 | dropped |
| zf_xiaoyi | en-us | 7.0 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| zm_yunjian | en-gb | 5.6 | 0 | shipped |
| zm_yunjian | en-us | 6.3 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| zm_yunxi | en-gb | 7.0 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| zm_yunxi | en-us | 6.3 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| zm_yunxia | en-gb | 8.5 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| zm_yunxia | en-us | 7.7 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |
| zm_yunyang | en-gb | 7.0 | 1 | dropped |
| zm_yunyang | en-us | 7.7 | 0 | passes, not shipped (a lower-WER option of this accent and sex is shipped) |

## Pending: Russian and German (and a second Japanese/Chinese/Hindi flavour)

Kokoro has no Russian or German voice, so these accents cannot come from stock tensors.
The accent codes `en-x-ru` and `en-x-de` are already in `Domain/Accents`; no voice uses them yet.

Searched for a permissively licensed (CC0 / CC-BY, not NC) accented-English corpus to
generate them with KVoiceWalk:

- Common Voice English on the Hugging Face Hub (`mozilla-foundation/common_voice_*`): CC0, but
  gated; the ungated mirror `WillHeld/accented_common_voice` only carries regional accent
  groups (US, England, Scottish, Australian, Irish, Canadian, Filipino, India and South Asia),
  no Russian or German.
- `westbrook/English_Accent_DataSet`: has German, French, Spanish and Italian second-language
  English, built from VoxPopuli (CC0), VCTK (CC-BY 4.0) and EDACC (CC-BY-SA 4.0); no Russian,
  Japanese or Hindi. Usable for German. Two German targets (one voice of each sex, VoxPopuli
  only) were prepared with `tools/voicepack/make_accent_target.py`, but the KVoiceWalk runs
  did not finish in the time available on a loaded CPU, so no German voice ships.
- Not usable: Speech Accent Archive derivatives (CC-BY-NC-SA), L2-ARCTIC (gated, no Russian
  or German or Japanese), AIxBlock sets (CC-BY-NC), `ThBel/Accented-Speech-Fragments` (NC),
  Nexdata accented English samples (no licence stated).
- Note that a walk only transfers timbre and cadence: with an English phonemizer the words
  stay in standard English pronunciation, and the same intelligibility gate would apply.

To add them later: build targets with `make_accent_target.py`, run KVoiceWalk, then list the
resulting voices in `extra_voices.json` for `tools/voicepack/build_accented_pack.py` and re-run
`accented_audition.py` on them.

## Rebuilding

```
accented_audition.py render   # needs a built tools/SmokeSynth, env vars in the script header
accented_audition.py score    # KVoiceWalk venv with faster-whisper
build_accented_pack.py <audition-dir> <kokorosharp voices dir> <this directory>
```
