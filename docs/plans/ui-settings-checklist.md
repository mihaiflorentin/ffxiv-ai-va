# Settings reachability checklist (four-tab shell, ticket #7)

Every `Configuration` property the old six-tab window exposed, and where it lives now.
Tabs: Home, Voices, What's read, Settings. Settings sections are collapsing headers.

## Home
| Property | Control |
|---|---|
| `Enabled` | "Enabled" checkbox |
| `GlobalVolume` | "Global volume %" slider |
| (derived) | engine status strip, first-run "Get started" guide, download progress |

## Voices (segmented control: Casting defaults / NPC voices / Player voices)
| Property / data | Control |
|---|---|
| `UseRaceVoicePresets` | Casting defaults: "Use race/gender voice presets for unlisted speakers" |
| `AgeAwareCasting` | Casting defaults: "Age-aware voices" |
| casting presets (`ICastingPresetStore`) | Casting defaults: preset bar (Activate, Save, Fork, New, Delete, Export, Import) |
| race/gender rows, beast tribes, model-id bindings | Casting defaults: row sections, "Beast tribes" |
| per-character overrides (`IProfileStore`) | NPC voices, Player voices: table, add form, Clear all, Export, Import |

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
`SelectedAudioDeviceIndex`, `Voices.*`, `Device.Mode/AdapterId/Backend/Api/GpuImpact/FallbackToCpu`,
`Engine.PerEngine`, migration and onboarding fields. Later tickets (#15-#18) surface the ones that matter.

## Behaviour notes
- Engine cards now come from `IEngineCatalog` (non-retired engines that own a download group), so Chatterbox Turbo is offered as a selectable engine; before, only Kokoro was visible.
- The Fork, New and Import (preset and assignments) dialogs run through `ModalHost`.
- Status messages go to the footer through `StatusCenter`; errors stay until dismissed.
