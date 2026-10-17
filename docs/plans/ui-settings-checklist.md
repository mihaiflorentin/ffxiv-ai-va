# Settings reachability checklist (four-tab shell, ticket #7)

Every `Configuration` property the old six-tab window exposed, and where it lives now.
Tabs: Home, Voices, What's read, Settings. Settings sections are collapsing headers.

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
| casting presets (`ICastingPresetStore`) | Settings > Casting presets (`CastingPresetPanel`; ticket #18 folds it into Advanced): Use this casting, Duplicate, New, Delete (asks first), Export, Import. Voices links to it ("Manage casting presets in Settings") |
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

## Settings
| Property | Control |
|---|---|
| `Engine.Primary` | Engine > engine card "Set active" (retired engines are not offered) |
| `Engine.Expressive.Enabled` | Engine > "Expressive voices" card |
| `DirectorEnabled` | Engine > "Emotion detection" card |
| `Device` provider (`ToExecutionProvider` / `ApplyExecutionProvider`) | Engine > active card, "Execution provider" (GPU-capable engines) |
| `Device.CpuImpact` | Engine > active card, "CPU impact" |
| `StaleLineSeconds` | Engine > active card, "Drop lines older than" |
| `KeepTempoWhenPitched` | Engine > active card, "Keep speaking pace on pitched voices" |
| `LevelVoiceLoudness` | Engine > active card, "Even out voice loudness" |
| `StreamSentences` | Engine > active card, "Start long lines sooner" |
| `ShowNowSpeaking` | Engine > active card, "Show the now-speaking panel" |
| model assets (download, remove, open folders, load engine) | Engine > buttons and asset tables |
| test bench (`TestBenchModel`) | Test |
| `UseKeybind`, `ModifierKey`, `MajorKey` | Keybinds |
| `RemoveStutter` | Experimental |

## Not exposed in the old window either (unchanged)
`DefaultExaggeration`, `Lexicons`,
`SelectedAudioDeviceIndex`, `Voices.ExtraPackDirs`, `Device.Mode/AdapterId/Backend/Api/GpuImpact/FallbackToCpu`,
`Engine.PerEngine`, migration and onboarding fields. Later tickets (#15-#18) surface the ones that matter.

## Behaviour notes
- Engine cards now come from `IEngineCatalog` (non-retired engines that own a download group), so Chatterbox Turbo is offered as a selectable engine; before, only Kokoro was visible.
- The Fork, New and Import (preset and assignments) dialogs run through `ModalHost`.
- Status messages go to the footer through `StatusCenter`; errors stay until dismissed.
