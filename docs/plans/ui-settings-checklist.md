# Settings reachability checklist (four-tab shell, ticket #7; Settings tab redone in #18)

Every `Configuration` property the old six-tab window exposed, and where it lives now.
Tabs: Home, Voices, What's read, Settings. Settings is Voice quality, Processor, Downloads and Keybinds cards, then a collapsed Advanced section.

## Home
| Property | Control |
|---|---|
| `Enabled` | "Enabled" checkbox |
| `GlobalVolume` | "Global volume %" slider |
| (derived) | engine status strip, first-run "Get started" guide, download progress |

## Voices (ticket #16)
Library on top, "Who sounds like who" below. The old segmented control (Casting defaults / NPC voices / Player voices) is gone; `VoiceSetEditor` and the id-prefix language/sex filters are retired from the UI.

| Property / data | Control |
|---|---|
| `IVoiceLibrary.Voices` | Voice library: search (name, accent, description, id), accent/sex/age filters, "N of M shown", Clear filters, preview, "Use for..." chooser (race and gender row, beast tribe, named NPC, named player) |
| `IVoiceLibrary.Packs` | Voice packs strip: licence, credit, problems list |
| `Voices.DisabledPacks` | Voice packs strip: per-pack switch (saves, rescans, rebuilds the voice map); built-in voices cannot be switched off |
| (pack folder) | Voice packs strip: "Open packs folder", "Rescan" |
| `Voices.ExtraPackDirs` | Still config-file only |
| `UseRaceVoicePresets` | Who sounds like who: "Choose voices automatically by race and gender" |
| `AgeAwareCasting` | Who sounds like who: "Make children and elders sound their age" |
| casting presets (`ICastingPresetStore`) | Settings > Advanced > Casting presets (`CastingPresetsCard`, rules in `CastingDraftGuard`): Use this casting, Duplicate, New, Delete (asks first), Export, Import (asks before replacing a casting with the same name). Voices links to it ("Manage casting presets in Settings > Advanced", anchor `casting-presets`) |
| unsaved casting edits | Voices: Save / Discard bar. Saving on the built-in casting keeps the edits as a new casting ("My casting") and switches to it |
| race/gender rows (17 + Anyone else) | Race and gender: fingerprints and names per row, "Edit" opens the editor (add, change, remove, preview) |
| per-slot pitch, speed, volume, expressiveness (`VoiceSlotDto`) | Editor: "Tune" popover; chips appear only when a knob is not at its default |
| beast tribes, voice lists (any gender, male, female) | Beast tribes: one row per tribe, "Edit" |
| model-id bindings, unassigned creature models | Beast tribes: inbox of creatures heard this session (Assign to..., Ignore); "Creature models" list in the tribe editor |
| per-character overrides (`IProfileStore`) | Characters with their own voice: NPCs / Players, sortable named columns, search, "Add by name", voice picker, Tune popover (commits on release), delete with Undo, Export, Import, Clear all (asks first) |
| voice not provided by any enabled pack | "Missing" row at the top of the library; "(missing)" next to the voice wherever it is used |

## What's read (ticket #17)
Four cards, then a collapsed "More options". The styles command (`/aivastyles`) opens this tab with More options open, scrolled to Style tags (`NavModel.OpenStyleTags`, anchor `ReadingAnchors.Styles`). The separate Styles window is gone.

| Property | Control |
|---|---|
| `ReadFromQuestTalkAddon` | Story & dialogue > "Read story dialogue" |
| `CancelSpeechOnTextAdvance` | Story & dialogue > "Stop the voice when I advance the text" |
| `SkipVoicedQuestText` | Story & dialogue > "Leave out lines the game already voices" |
| `ReadCutsceneSubtitles` | Story & dialogue > "Read cutscene subtitles" |
| `EnableNameWithSay` | Story & dialogue > "Say the speaker's name before each line" |
| `ReadFromBattleTalkAddon` | Battle > "Read battle talk" |
| `SkipVoicedBattleText` | Battle > "Leave out lines the game already voices" |
| `SkipMessagesFromYou`, `OnlyMessagesFromYou` | Chat > "Skip my own messages", "Only read my own messages" |
| `EnabledChatTypesPresets`, `CurrentPresetId` | Chat > Preset picker, "New preset", "Delete preset" |
| preset `EnableAllChatTypes` | Chat > "Read every channel" |
| preset `EnabledChatTypes` | Chat > Channels: search, per-category "Select all" / "Clear all", "N of M on" |
| `Exclusions` | "Skip lines matching..." card (empty state "Everything is read."); pattern checkbox needs `ShowAdvanced` |
| `NameNpcWithSay`, `SayPlayerWorldName`, `DisallowMultipleSay`, `SayPartialName`, `OnlySayFirstOrLastName` | More options > Speaker names (greyed out while the name option is off) |
| `UsePlayerRateLimiter`, `MessagesPerSecond` | More options > Pace |
| preset `Name`, `UseKeybind`, `ModifierKey`, `MajorKey` | More options > Chat preset |
| `Triggers` | More options > "Only read lines matching..." (pattern checkbox needs `ShowAdvanced`) |
| `AdHocStyleTagsEnabled`, `StyleTag` | More options > Style tags: "Allow style tags in text", "Marker", line tester ("Copy with markers") |
| `StyleRegex` (derived, read-only) | More options > Style tags, shown as "Match pattern" when `ShowAdvanced` is on |
| `ShowAdvanced` | More options > Technical details > "Show advanced details" |

## Settings (ticket #18)
Order: Voice quality, Processor, Downloads, Keybinds, Advanced (collapsed). Nav anchors (`SettingsAnchors`): `quality`, `processor`, `downloads`, `keybinds` scroll to their card; `advanced`, `engine`, `test`, `casting-presets`, `diagnostics`, `experimental` open Advanced and the part they name.

| Property / data | Control |
|---|---|
| `Engine.Primary`, `Engine.Expressive.Enabled`, `Engine.Expressive.EngineId` (as a tier) | Voice quality: Fast / Balanced / Most expressive (`QualityTierModel`). "Most expressive" is greyed out with the reason shown when there is no usable graphics card. A tier whose files are missing waits (`QualityChoiceModel`): "Download N GB and switch", the voices switch when the download ends. A setup that matches no tier reads "Custom setup" |
| engine state (`ISpeechSynthesizer.Status`) | Voice quality: state pill (icon + word) and one detail line (`EngineViewModel`) |
| `DirectorEnabled` | Voice quality > "Emotion detection" (greyed out until the emotion reader is downloaded) |
| `LevelVoiceLoudness` | Voice quality > "Even out loudness" |
| `StaleLineSeconds` | Voice quality > "Skip lines that arrive late" (on = default 20 s, off = 0); the seconds are under Advanced > Delivery details |
| `ShowNowSpeaking` | Voice quality > "Show the now-speaking panel" |
| `Device.Mode` | Processor > Automatic / Graphics card / CPU (`DeviceViewModel`); the graphics card choice is greyed out with the probe's reason when no adapter is usable |
| `Device.GpuPreference` | Processor > "Graphics card to prefer": Performance / Low power (only with more than one adapter) |
| `Device.CpuImpact` | Processor > "Background load": Light / Balanced / Fast (2 / 4 / 8 threads) |
| `IDeviceProbe.LastReport.Adapters` | Processor > "Graphics cards reported by the system" (display only; Wine/Proton note), "Check again" |
| `Device.GpuDisabledByCrashGuard`, `EngineStatus` FellBack / Failed | Processor banner: "Graphics card was turned off after a crash" / "Voices are running on your processor" / "The voices could not start", with the port's reason verbatim and Retry on GPU (`RetryAsync(Gpu)`), Use CPU (`Device.Mode = cpu`), Copy details |
| model assets (download, resume, cancel, remove) | Downloads: one row per group (`DownloadsModel`) with size, state, Download/Resume, Remove (optional groups, or groups of an engine not in use), progress with Cancel, failure banner with Retry, free disk, Open models folder, Open voices folder |
| `UseKeybind`, `ModifierKey`, `MajorKey` | Keybinds |
| `Engine.PerEngine[id].Precision` | Advanced > Engines > Precision (engines with more than one precision, sizes shown; config only until the engine reads it) |
| `Engine.PerEngine["remote"].Extra` (`url`, `allowNonLoopback`, `timeoutSeconds`, `model`) | Advanced > Engines > Remote server (`RemoteEndpointModel`: address check, warning for servers on another computer) |
| `Engine.Primary` (raw), `Engine.Expressive.Enabled` (raw) | Advanced > Engines: "Use as main engine", "Send emotional lines to the expressive engine" |
| `KeepTempoWhenPitched`, `StreamSentences`, `RemoveStutter` | Advanced > Delivery details |
| `Device.Backend`, `Device.Api` | Advanced > Delivery details > Graphics runtime |
| casting presets | Advanced > Casting presets |
| test bench (`TestBenchModel`) | Advanced > Test a line |
| diagnostics report | Advanced > Diagnostics > Copy report (`DiagnosticsReport`; no paths, names or line text); also Processor banner > Copy details |
| `ShowAdvanced` | Advanced > This window and setup > "Show advanced details" |
| `ReduceMotion` | Advanced > This window and setup > "Reduce motion" |
| `OnboardingCompletedVersion`, `OnboardingStep` | Advanced > This window and setup > "Run setup again" (resets both, goes to Home, restarts the setup card) |
| reset | Advanced > This window and setup > "Reset settings to defaults" (asks first; `SettingsReset`) |

## Not exposed (unchanged)
`DefaultExaggeration`, `Lexicons`, `SelectedAudioDeviceIndex`, `Voices.ExtraPackDirs`, `Device.GpuImpact`, `Device.FallbackToCpu` (no engine reads these two yet),
`Engine.PerEngine[id].Device` (the device policy does not read it), `LastPage`, `MigrationNoticeAckedVersion` and the migration report (Home banner).

## Behaviour notes
- Engine cards (Advanced > Engines) come from `IEngineCatalog` (non-retired engines, including Remote server); Downloads rows come from the engine catalog's download groups plus the emotion reader.
- Wording: "Execution provider" is "Processor", "CPU impact" is "Background load", "Fork" is "Duplicate" (`Labels`). Engine names, precisions, runtime and API names appear only under Advanced.
- The Duplicate, New and Import (preset and assignments) dialogs run through `ModalHost`.
- Status messages go to the footer through `StatusCenter`; errors stay until dismissed.
