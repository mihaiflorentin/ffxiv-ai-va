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

## What's read
| Property | Control |
|---|---|
| `ReadFromQuestTalkAddon`, `CancelSpeechOnTextAdvance`, `SkipVoicedQuestText` | General > Quest dialogue |
| `ReadFromBattleTalkAddon`, `SkipVoicedBattleText` | General > Battle dialogue |
| `ReadCutsceneSubtitles` | General > Cutscenes |
| `SkipMessagesFromYou`, `OnlyMessagesFromYou` | General > Chat |
| `EnableNameWithSay`, `NameNpcWithSay`, `SayPlayerWorldName`, `DisallowMultipleSay`, `SayPartialName`, `OnlySayFirstOrLastName` | General > Name lines with "say" |
| `UsePlayerRateLimiter`, `MessagesPerSecond` | General > Rate limiting |
| `EnabledChatTypesPresets`, `CurrentPresetId`, preset `Name`, `EnableAllChatTypes`, `UseKeybind`, `ModifierKey`, `MajorKey`, `EnabledChatTypes` | Channel Settings |
| `Triggers`, `Exclusions` | Triggers/Exclusions |

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
`DefaultExaggeration`, `Lexicons`, `AdHocStyleTagsEnabled`, `StyleTag` (the Styles window / config file),
`SelectedAudioDeviceIndex`, `Voices.*`, `Device.Mode/AdapterId/Backend/Api/GpuImpact/FallbackToCpu`,
`Engine.PerEngine`, migration and onboarding fields. Later tickets (#15-#18) surface the ones that matter.

## Behaviour notes
- Engine cards now come from `IEngineCatalog` (non-retired engines that own a download group), so Chatterbox Turbo is offered as a selectable engine; before, only Kokoro was visible.
- The Fork, New and Import (preset and assignments) dialogs run through `ModalHost`.
- Status messages go to the footer through `StatusCenter`; errors stay until dismissed.
