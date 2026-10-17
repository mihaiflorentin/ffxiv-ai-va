# UI/UX redesign: friendly, scalable, testable

Status: plan. Date: 2026-10-17. Author: architect.
Companion plan (written concurrently): `docs/plans/2026-10-gpu-engines-and-voices.md`. Section 9 states what this plan assumes from it.

Inputs: AGENTS.md, `src/AIVoiceActing/UI/**`, `Configuration.cs`, `AIVoiceActingPlugin.cs`, the `ui-ux-pro-max` and `frontend-design` guidance (translated to ImGui, see section 4 for what was rejected).

## 0. Simplification pass (owner directive: "simplify everything, improve ease of use") — OVERRIDES sections 3, 5 and 8 where they conflict

The owner asked for the redesign to be primarily a simplification. Seven sidebar pages is still a settings app. The target is: a new player installs, clicks one button, hears voices; everything else is optional.

**Four tabs, not seven pages.** A plain tab bar fits four items at 200% scale, so drop the sidebar.

| Tab | Holds | Folds in (from section 3) |
|---|---|---|
| **Home** | Master on/off, volume, one status line ("Ready, running on your graphics card"), "Try it" box, setup button when not ready, migration banner | Overview |
| **Voices** | Voice library (search, accent/sex filter chips, preview) on top; below it "Who sounds like who": auto-casting on/off plus a list of characters you've overridden | Voices + Casting (Defaults/NPCs/Players become filter chips on one list, not three sub-pages) |
| **What's read** | Checkboxes for dialogue, battle talk, cutscenes, chat channels; "Skip lines matching…" list | Reading (style tags under "More options") |
| **Settings** | Quality (one 3-way choice: *Fast / Balanced / Most expressive*, which maps to engine+device), Processor (Auto/GPU/CPU with detected device and fallback reason), Downloads, Keybinds, then a collapsed "Advanced" (raw engine pick, thread budget, presets, diagnostics, reset) | Engine + Delivery + Advanced |

**Rules that cut most of the complexity:**
1. **One quality choice replaces engine selection for 95% of users.** "Fast" = Kokoro, "Balanced" = Kokoro + expressive engine for emotional lines, "Most expressive" = best GPU engine. Raw engine names appear only under Advanced.
2. **Setup is one button, not a 4-step wizard.** "Set up recommended" detects the GPU, picks the quality tier, downloads, plays a sample. It shows one progress row and a Cancel. A "Customize" link expands the same choices inline. No separate window.
3. **Casting presets leave the main path.** Auto-casting by race/gender just works; the preset bar (Fork/New/Import/Export) moves under Settings > Advanced. Per-race rows become "Voices for Hyur Midlander, male: [fp][fp][fp] Edit".
4. **No sliders by default.** Per-voice pitch/speed/volume/expressiveness live in one Tune popover per row, summarised as chips only when non-default.
5. **Delivery knobs collapse into presets.** Emotion detection, expressiveness, loudness levelling and stale-line drop become on/off toggles under Settings > Quality with sensible defaults. Numeric values go under Advanced.
6. **Jargon list (must not appear outside Advanced):** execution provider, ONNX, model id, bias, exaggeration, pool, RTF, DirectML, regex. Use: "graphics card", "voice", "expressiveness", "speed".
7. **Signature stays, and stays small:** the voice fingerprint appears in the Voices list, the voice picker and Now Speaking only.

**Phase impact:** P3's five page redesigns become four tabs (Home, Voices, What's read, Settings). P6's wizard becomes the inline "Set up recommended" flow on Home. Sidebar and NavModel narrow-mode work is dropped; `NavModel` keeps `OpenAt(tab, anchor)` for deep links. Everything else in sections 4, 6, 7 stands.

## 1. Audit of current pain points

File refs are `src/AIVoiceActing/UI/...`. "CW" = `ConfigurationWindow.cs`.

### Structure and maintainability
1. CW is a 2330-line class with a 24-parameter constructor (CW:161-185), 40+ fields and six tabs. Download queue (CW:851-919), warm-up (708-744), probes (746-793), world lookup (133-159), preview/test plumbing (1277-1353) and all drawing live in one type. The download queue and warm-up state machines have no unit tests because they cannot be constructed without a window.
2. The UI breaks its own port-only rule: CW:316-317 references `Infrastructure.Onnx.ModelCatalog`, and engine/asset group keys are duplicated as string literals (CW:304-314, 547-548, 800, 828). Engine knowledge belongs behind a port.
3. Persistence model is inconsistent. Toggles and sliders auto-save (`Controls.cs`), casting edits need an explicit Save with a hidden dirty buffer (CW:1388-1422, `CastingTabModel.Dirty`), characters commit on slider release (CW:2303-2320). Nothing shows "unsaved"; switching preset silently drops edits (CW:1376-1381).

### Bugs found while reading
4. Four modals very likely never open. `OpenPopup` and `BeginPopupModal` use different IDs: `"##fork-preset"` (CW:1428) vs `"Fork preset"` (CW:1790); `"##new-preset"` (1435) vs `"New preset from scratch"` (1834); `"##import-preset"` (1474) vs `"Import preset"` (1878); `"Import assignments###import{prefix}"` (2001) vs `"Import voice assignments"` (2005). ImGui hashes the whole string, or the part after `###`, so the IDs differ. Fork, New, Import preset and Import assignments are probably dead buttons. Verify in-game first; either way the redesign replaces them with one `ModalHost`.
5. Emoji used as icons: `"🗑"` (CW:1612, 1736, 2119) and `"▶"` (CW:1603, 1633, 2186). Dalamud's default atlas has no U+1F5D1 glyph, so the trash button probably renders as `?`. Use FontAwesome via `UiBuilder.IconFont`.
6. The status line (CW:275-287, 10 s fade) is drawn after the tab bar, inside the scrolling content. On any long tab it is scrolled out of view, so errors such as "Download failed" are easy to miss. Success and error use raw red and green (CW:285) with no icon.

### Information architecture
7. The master switch "Enabled" and "Global volume" sit inside Chat > General (CW:950-955). The only always-visible hint is "(TTS Enabled)" in the title bar (CW:225). The Chat tab mixes capture sources, name-with-say, rate limiting, channel presets, keybinds and regex triggers in one scroll (CW:932-1038).
8. Playback and quality settings (Now Speaking panel, loudness levelling, stale-line drop, CPU impact) are hidden inside the active engine card, which is collapsible (CW:684-703). Collapse the card and they vanish.
9. Engine selection is effectively hidden: only Kokoro is visible (CW:419) and the other engines survive only via JSON edits. The new engine plan reverses that, so the screen needs a real engine and device model.
10. Test is a separate tab (CW:1192-1273) from the places where a user actually chooses a voice. Per-row `▶` buttons exist but always say the same fixed sentence (`TestBenchModel.VoiceTestLine`).
11. Styles is a separate window (`StylesWindow.cs`) for what is a Reading setting.

### Voices and characters
12. Voices are cryptic ids (`af_heart`) in an ImGui combo. Language and sex are parsed from the id prefix (`Domain/VoiceCatalog.cs`, `VoiceSetEditor.cs:15-37`); there is no description, age, or accent label. `VoiceSetEditor.DrawVoiceCombo` appends "(missing)" entries to the list (VoiceSetEditor.cs:108-117).
13. Each voice slot row is a 7-column table with four always-visible sliders: pitch, speed, vol, bias (CW:1590-1646). 17 race/gender rows x several slots is a wall of sliders. Jargon: "Bias", "Exaggeration", "Pool (unknown gender)", "Execution provider", "Model-id bindings".
14. The NPC/Player table has 9 columns and no header row: no `TableSetupColumn`/`TableHeadersRow` at CW:2106-2112. Columns are unlabelled, unsortable, and every row draws a combo and four sliders with no clipper.
15. The search box hint says matching rows are "highlighted" (VoiceSetEditor.cs:74) but the table filters them (CW:2095-2102). Filters and search are three tiny controls in a row with fixed widths (VoiceSetEditor.cs:49,58,67).
16. Destructive actions use an "arm then click again" button (CW:1439-1461, 1964-1988). The armed state never times out and survives switching tabs. There is no undo.

### Feedback, accessibility, scaling
17. Hard-coded pixel sizes: window 620x520 (CW:215), combo 220 (1373), popup inputs 300 and 460x120 (1796, 1884), table height 240 (1594), fixed columns 24/90/80/36 (1597-1603, 503-505), add-form widths 180/140/80 (2220, 2235, 2254), trigger input 320 (`TriggerList.cs:44`), styles delimiter 60 (`StylesWindow.cs:35`), progress bar `-130` (CW:388). At GlobalScale 150-200% these misalign or clip.
18. `ImGui.TextDisabled` is the main secondary-text style. Default disabled grey (about #808080) on a typical dark Dalamud window is 4.4:1, just under the 4.5:1 AA threshold for informative text (computed, see section 4).
19. `Controls.Button` shows a disabled reason on hover only (`Controls.cs:150-165`). Many other disabled controls give no reason (e.g. `ImGui.BeginDisabled` in `NowSpeakingWindow.cs:69-92`). Icon-only buttons have no tooltip.
20. Error recovery is "message only": download failure (CW:910-916) and engine-start failure (CW:734-738) offer no Retry, no "Open log", no "Use CPU". Onboarding is three static lines shown only while the engine is missing (CW:798-824); there is no step flow and nothing for returning users after an upgrade.
21. Loading states are a text `loading…` (CW:336) and a progress bar with a long caption (CW:379-389). There is no indeterminate state when the total size is unknown (`OverallProgress` returns 0).

What is good and stays: cached probes never run per frame (CW:23-24, 746-793), persist-on-release for sliders (`Controls.PersistOnRelease`), the pure `State/` models that are already unit-tested, fire-and-forget speech wrapper counting in-flight requests (CW:1333-1353), disabled-with-reason buttons.

## 2. Design brief (from the two skills)

Subject: a settings and "casting desk" for people playing FFXIV who want NPCs voiced. Audience: mostly non-technical players, some tinkerers. Single job of the main window: get from install to hearing a good voice in under two minutes, then let people direct who sounds like what.

Direction, in one line: calm, game-native chrome with one distinctive element, the **voice fingerprint**. ImGui in Dalamud inherits the user's theme and the game's fonts, so we do not fight them. We add semantic colour, icons, and spacing discipline, and spend the "boldness" in one place.

Signature: every voice is drawn with a small fingerprint, a row of 12 vertical bars whose heights are derived from a hash of the voice id (drawn with `ImDrawList.AddRectFilled`, accent coloured). Static when idle, bars gently pulse while that voice is previewing. It makes a library of 50+ similar-sounding names scannable, gives the Voices page its character, and costs about 40 lines.

Rejected defaults from the tool's suggestion (`--design-system` returned a neon/cyberpunk Orbitron + indigo/orange palette): wrong for a game plugin that must sit inside Dalamud's window chrome and FFXIV's calmer tone, and web fonts are not available. We keep only its transferable rules: AA contrast, no emoji icons, visible state, empty-state actions, progress for multi-step flows, clear error recovery, skippable onboarding, limited nav items, 150-300 ms motion with reduced-motion off switch.

## 3. Information architecture

One main window, one onboarding window, one overlay. `StylesWindow` is folded into Reading.

```
Main window: "AI Voice Acting"   (###AIVAConfig, replaces "Settings")
 +- Sidebar (icons + labels, badges)         +- Page (scrolling child)
 |  Overview     (Home)                      |  header: title + one-line purpose
 |  Engine       (Microchip)  [!] if not rdy |  banners (migration, not-ready)
 |  Voices       (Microphone)                |  cards
 |  Casting      (Users)      [*] unsaved    |
 |  Reading      (BookOpen)                  +- Footer (always visible, not scrolled)
 |  Delivery     (Sliders)                      status pill | queue | last message | Stop all
 |  Advanced     (Cog)
Onboarding window: "Welcome to AI Voice Acting" (4 steps, skippable)
Overlay:          "Now speaking" (kept, polished)
```

Why a sidebar rather than a tab bar: seven items with icons, badges and a narrow-mode (icon-only below about 44 em wide) do not fit a tab bar at 150% scale; a sidebar also gives each page a title and purpose line. Seven items is within the usual 5-9 limit.

| Page | Contains | Replaces |
|---|---|---|
| Overview | Master switch, volume, status headline, setup checklist, quick "Try it" line, migration banner, link to Now Speaking | CW Chat>General top, Engine status strip, `DrawGetStarted` |
| Engine | Engine cards (choose), Device card (detected GPU, preference, fallback status), Models and downloads table, folders | Engine tab, ExecutionProviders combo |
| Voices | Voice library: browse, filter by accent/sex/age, preview, pack install | `VoiceSetEditor`, per-row combos |
| Casting | Segmented: **Defaults** (preset bar, race/gender rows, beast tribes, unbound models) / **NPCs** / **Players** | General, NPC, Player Voices tabs |
| Reading | What is read: sources (quest, battle, cutscene), chat channels, name-with-say, rate limit, triggers/exclusions, ad-hoc style tags | Chat tab + `StylesWindow` |
| Delivery | Emotion detection, expressive voices, default expressiveness, pitch tempo, loudness levelling, stale-line drop, audition panel (old Test tab) | Engine knobs, Test tab |
| Advanced | Keybinds, CPU impact, folders, diagnostics (copy report), reset, re-run setup, "show advanced" toggle, experimental | Keybinds, Experimental |

Progressive disclosure rules:
- Every page opens with the 3-5 settings 90% of people touch. Rare ones live in a collapsed "More options" `CollapsingHeader` at the bottom of the page.
- Per-voice tuning (pitch, speed, volume, expressiveness) is hidden behind a Tune popup per row, summarised as chips ("pitch +10%") so differences stay visible.
- Raw ids, regex, model ids and execution providers appear only under Advanced or when "Show advanced details" (config `ShowAdvanced`) is on.

Navigation state: `NavModel.Selected` plus an optional `OpenAt(page, anchor)` so `/aiva styles`, the Now Speaking panel's gear icon, and notification clicks can deep-link. Selected page is not persisted across game restarts except via `config.LastPage` (cheap).

## 4. Visual style tokens

All values are applied with `ImRaii.PushColor/PushStyle` scopes inside `Theme`; nothing global is overwritten, so Dalamud's user theme (window bg, frames, fonts) still rules. Draw code never contains pixel literals.

### Colour (semantic, dark-theme tuned)
Contrast computed against a worst-case dark window #1A1A1E and card #26262C.

| Token | Hex | On window / card | Use |
|---|---|---|---|
| `Accent` (Aether teal) | #4CC9C0 | 8.6 / 7.5 | primary button fill, active nav marker, progress, fingerprint bars |
| `OnAccent` | #0E1A1A | 8.8 on accent | text on primary button |
| `AccentHover` / `AccentActive` | #6FD8D0 / #3DB0A8 | n/a | button states |
| `Ok` | #6CCB7E | 8.7 / 7.5 | ready, downloaded (always with check icon) |
| `Warn` (Gil gold) | #E8B84B | 9.4 / 8.2 | attention, fell back to CPU (always with triangle icon) |
| `Error` | #F0625F | 5.5 / 4.7 | failures (always with x-circle icon); danger button fill #B3403E with white text 5.7:1 |
| `Info` | #7FB2FF | 8.0 / 7.0 | informational banners (info icon) |
| `Muted` | #A3ADB5 | 7.6 / 6.6 | secondary text, replaces `TextDisabled` for anything informative |
| `Card` | window bg lightened about 6% (derive from `ImGuiCol.ChildBg`) | n/a | card background, border = `ImGuiCol.Border` |

Rules: colour is never the only signal (status = icon + word + colour); `TextDisabled` is reserved for truly inert text. If the user's theme is light, `Theme` flips by luminance of `ImGuiCol.WindowBg` and picks darker variants of the same hues (add a unit test on the token resolver in `UI/State/ThemeTokens` that is pure math).

### Typography (ImGui reality)
Dalamud gives one body font, a mono font, a FontAwesome icon font and the game fonts. Do not shrink text below body.

| Role | Font | Notes |
|---|---|---|
| Page title | Game font Axis 18 (`UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis18))`), push with the handle's `Push()` scope | the one place we echo the game's own UI face |
| Section label | body font, `Accent` colour, followed by a thin separator line drawn manually (`SeparatorText` is absent in these bindings, `Controls.cs:14`) | |
| Body | Dalamud default | |
| Caption / helper | body font, `Muted` colour | never `SetWindowFontScale(<1)` |
| Ids, regex, paths | `UiBuilder.MonoFontHandle` | |
| Icons | `UiBuilder.IconFont` (`FontAwesomeIcon`) | icon-only buttons always get a tooltip |

Verify handle names against the installed Dalamud assembly (`.dalamud/dev/Dalamud.dll` contains `NewGameFontHandle`, `IconFontHandle`, `ImRaii`, `ImGuiComponents.ToggleButton/HelpMarker/IconButton`, `INotificationManager`).

### Spacing and size (all derived from `em = ImGui.GetFontSize()`, which already includes GlobalScale)
`Ui.Em(k)` = `ImGui.GetFontSize() * k`; `Ui.Scaled(px)` = `px * ImGuiHelpers.GlobalScale` (only for icon bitmaps and one-pixel rules).

| Token | Value |
|---|---|
| `Space1/2/3/4/5` | 0.25 / 0.5 / 1.0 / 1.5 / 2.0 em |
| Card padding | `Space3`; gap between cards `Space3` |
| Sidebar width | 11 em (3 em, icon-only, when the window is narrower than 44 em) |
| Label column in forms | 14 em, control column fills the rest, helper text on the line below |
| Control min width | 8 em (combos 12 em, search 16 em, name inputs 14 em) |
| Min hit target | one frame height (`GetFrameHeight()`); never `SmallButton` for a primary action |
| Row height in tables | `GetFrameHeight() + Space1` |
| Main window default / min | 54 x 36 em / 44 x 26 em set once in the constructor via `Size`/`SizeConstraints` (Dalamud multiplies these by GlobalScale; verify in the installed `Window` implementation) |

### Motion
Only two animations: indeterminate progress (`ProgressBar(-1 * time)`) and the voice-fingerprint pulse while previewing (about 200 ms per bar, sine). `config.ReduceMotion` (default off) freezes both to static. No fades or slides.

### Components (UI/Components, drawing only)
`Theme`, `Ui` (em/scaled helpers), `Icons`, `IconButton`, `PrimaryButton`/`DangerButton`/`GhostButton`, `Card`, `Chip`, `StatusPill`, `Banner` (info/warn/error + actions), `EmptyState`, `FormRow`, `SectionHeader`, `SearchBox` (clear button), `SegmentedControl`, `ModalHost` (one registered popup per modal id, fixes bug 4), `ConfirmDialog`, `ProgressRow`, `VoiceFingerprint`, `VoiceCard`/`VoiceRow`, `VoicePicker`, `Sidebar`. Existing `Controls` stays as the config-bound wrapper layer and is re-skinned, not removed.

## 5. Main screens (ASCII)

Widths are in em. `[x]` checkbox, `(o)` radio/segment, `[ btn ]` button, `>` collapsed.

### 5.1 Frame and Overview
```
+------------------------------------------------------------------------------+
| AI Voice Acting                                                         _ x  |
+-------------+----------------------------------------------------------------+
| (H) Overview| Overview                                                       |
| (E) Engine !| Voices for the game, running on your PC.                       |
| (V) Voices  |                                                                |
| (U) Casting*| [i] Your settings were updated for v1.0.  [ See what changed ] [x]|
| (R) Reading |                                                                |
| (D) Delivery| +- Status ---------------------------------------------------+  |
| (A) Advanced| | (ok) Ready: Kokoro on NVIDIA RTX 4070 (GPU)                |  |
|             | | Speech is [ON]   Volume  |=========o------| 100%          |  |
|             | +------------------------------------------------------------+  |
|             | +- Get set up ------------------------------ 2 of 4 done ---+  |
|             | | (ok) Install the voice engine                              |  |
|             | | (ok) Engine loads                                          |  |
|             | | ( )  Hear a sample voice           [ Play sample ]         |  |
|             | | ( )  Choose how characters are cast [ Open Casting ]       |  |
|             | +------------------------------------------------------------+  |
|             | +- Try it -----------------------------------------------+     |
|             | | [ "Well met, traveler. The crystal calls us onward."  ] |     |
|             | | Voice: Auto (my character)   Mood: Auto   [ > Speak ]   |     |
|             | +----------------------------------------------------------+    |
+-------------+----------------------------------------------------------------+
| (ok) Ready  |  Queue 0  |  Spoke test line in 1.2 s                [Stop all] |
+------------------------------------------------------------------------------+
```
Footer is outside the scrolling child (fixes pain point 6). Checklist is derived by `OverviewChecklistModel`; it collapses to a single "All set" line once complete.

### 5.2 Engine
```
 Engine                       Pick how voices are made. You can change any time.
 +- Engine ------------------------------------------------------------------+
 | (o) Kokoro        Fast narration  [Fast] [Recommended]    310 MB   (ok) Ready|
 |     50+ voices, near-instant, works on any PC.                              |
 | ( ) Chatterbox Turbo   Expressive acting  [Slower] [GPU recommended] 3.2 GB |
 |     [ Download 3.2 GB ]                                                     |
 | ( ) F5-TTS        Voice cloning quality  [Slow] [Experimental]  1.4 GB      |
 +-----------------------------------------------------------------------------+
 +- Processor ---------------------------------------------------------------+
 | Detected:  NVIDIA RTX 4070 (12 GB)   AMD iGPU (not supported by Kokoro)     |
 | Run on:    (o) Automatic   ( ) GPU: [RTX 4070 v]   ( ) CPU                  |
 | (warn) Running on CPU: GPU init failed (DirectML under Wine).               |
 |        [ Retry on GPU ]  [ Keep using CPU ]  [ Copy details ]               |
 +-----------------------------------------------------------------------------+
 +- Downloads ------------------------------------------------------ 340 MB --+
 | Kokoro model        310 MB   (ok) Installed          [ Remove ]             |
 | Emotion reader      480 MB   Not installed           [ Download ]           |
 | Downloading Turbo (file 2 of 5)  |=========------| 412 / 1 840 MB  [Cancel] |
 | Models folder: ...\models (3.6 GB)                      [ Open folder ]     |
 +-----------------------------------------------------------------------------+
 > More options (processor details, thread budget, warm-up)
```

### 5.3 Voices (library)
```
 Voices                         Browse and preview every voice you have installed.
 [ Search voices, accents, descriptions...   x ]  Accent [All v]  Sex [All v]
 Age [All v]  [x] Installed only      Sort [Name v]        24 of 54 shown
 Preview line: [ This is the voice I will use.                    ] [ Reset ]
 +----------------------------------------------------------------------------+
 | ||||.|.|||..  Heart            British English . Female . Adult   [ > ]    |
 |                Warm, steady narrator.                         [ + Cast ]    |
 | |.||||..|||.  Michael          American English . Male . Adult    [ > ]    |
 |                Clear and brisk.                                [ + Cast ]   |
 | ...                                                                          |
 +----------------------------------------------------------------------------+
 Packs:  Starter (installed)   Accents: Scottish, Irish (120 MB) [ Download ]
 Empty:  No voices match "scot". [ Clear filters ]   (or: No voice packs yet. [ Download Starter ])
```
The glyph row on the left is the voice fingerprint. `> ` previews, `+ Cast` opens the VoicePicker target chooser ("Add to: Casting row...").

### 5.4 Casting > NPCs (Players is identical with a World column)
```
 Casting   (o) Defaults  ( ) NPCs  ( ) Players
 [ Search name or voice...  x ]  [ Add character... ]       37 characters
 +--------------------------------------------------------------------------+
 | Name v          Voice                    Tune                     Actions |
 | Alisaie L.     (fp) Heart (UK, F)       pitch +5%   speed 1.0   > ... t   |
 | Thancred W.    (fp) Marcus (US, M)      defaults                > ... t   |
 +--------------------------------------------------------------------------+
 Empty:  No NPCs cast yet. They appear here as you meet them.  [ Add manually ]
 Row `...` = Tune popup (pitch, speed, volume, expressiveness) ; `t` = trash with 5 s Undo toast.
```
Defaults sub-page: preset bar `Preset [My casting v] [Activate] [Save] [...menu: Fork, New, Import, Export, Delete]`, a sticky bar `You have unsaved changes  [Save] [Discard]` when dirty, and a tree of race/gender rows each summarised as "Hyur Midlander, Male: 3 voices [fp][fp][fp]".

### 5.5 Setup wizard
```
+- Welcome to AI Voice Acting ---------------------------- Step 2 of 4 -----+
|  (1 Engine) -> [2 Processor] -> (3 Download) -> (4 Hear it)                 |
|                                                                             |
|  We found: NVIDIA RTX 4070 (12 GB)                                          |
|  (o) Use my graphics card (recommended)    ( ) Use the CPU only             |
|  GPU speeds up voices about 3x here. You can change this in Engine.         |
|                                                                             |
|  [ Back ]                                  [ Skip setup ]   [ Continue > ]  |
+-----------------------------------------------------------------------------+
Step 3: progress row, size and free disk, Cancel / Retry, "Keep playing, I will finish later".
Step 4: three accent samples with fingerprints and play buttons, [ Finish ].
```

### 5.6 Migration notice
Non-blocking banner at the top of Overview (and of the footer pill as a dot) until acknowledged: "Your settings were updated for v1.0. Engine: Chatterbox (legacy) -> Kokoro. 12 voice names were renamed. [ See what changed ] [ Got it ]". "See what changed" opens a modal listing grouped changes from the migration report; `Got it` stores `config.MigrationNoticeAckedVersion`. No blocking modal at login.

### 5.7 Now speaking (polished)
```
+- Now speaking --------------------------+
| (fp) Alisaie Leveilleur        [gear]   |
|      Heart . sad                         |
| "We cannot lose another friend..."       |
| [ Replay ] [ Skip ] [ Stop all ]  3 wait |
+------------------------------------------+
```
Voice shown by display name, emotion as a chip, icon+label buttons with tooltips and disabled reasons, `Lock` option (NoMove | NoTitleBar when pinned) kept in Advanced.

## 6. Interaction patterns

**Voice preview.** One `PreviewController` (pure, `UI/State`) owns states `Idle -> Synthesizing -> Playing -> Idle|Error`. Starting a preview cancels the previous one (queue `CancelCurrent`, then enqueue). The button shows a spinner glyph while synthesizing and a stop glyph while playing; the voice's fingerprint pulses. Preview text defaults to the Voices page "Preview line" (3 canned lines: neutral, excited, sad; custom allowed). Disabled with reason when engine is not ready ("Download the engine first" with a link-button to Engine). Previews go through the existing render cache so repeat plays are instant.

**Search and filter.** One `SearchBox` (case-insensitive substring over display name, accent, description, id) plus multi-select chips for accent, sex, age, installed-only, in a popup when more than 3 values. Always show "N of M shown" and a `Clear filters` button; an empty result shows a specific message with that button. Filters are session state only and never change stored selections (fixes pain point 15: no more "highlight" wording). Table clipper (`ImGuiListClipper`) for lists above about 40 rows.

**Applying changes.** Rule: toggles, combos and sliders apply immediately (sliders persist on release, as now). The only exception is the Casting Defaults editor, which is a draft: sticky unsaved bar, nav dot, `Discard` and `Save`, and a confirm dialog when switching preset or leaving with unsaved edits. Footer pill states "Saved" briefly after a persist.

**Destructive actions.** `ConfirmDialog` modal naming the object and count, default focus on Cancel, destructive button in `Error`-dark fill. Single-row deletions skip the modal and show an Undo toast for 8 s (profile store `SetOverride` re-adds). Bulk and preset deletes always confirm. The "arm then click" pattern is removed.

**Errors and recovery.** `StatusCenter` (pure) holds typed messages `{Severity, Title, Detail, Actions[]}`. Transient success goes to a toast (Dalamud `INotificationManager` through a UI-local `IToastSink` implemented in the plugin root) and the footer. Errors are sticky until dismissed or resolved and carry actions:
- download failed -> [Retry] [Open log] ; offline/disk errors name the cause
- engine failed on GPU -> [Retry on GPU] [Use CPU] [Copy details]
- engine not installed -> [Download N MB] ; the same inline banner is on every page while the engine is not ready
- preset import invalid -> inline message under the paste box, the modal stays open

**Loading.** Engine loading shows an indeterminate bar plus "Loading Kokoro on GPU..." in the Overview status card and footer. Downloads show "file k of n", bytes, speed, and Cancel; an unknown total uses the indeterminate bar. Page content never blocks on loading; controls that need the engine disable with a reason.

**Empty states.** Every list has a one-sentence explanation plus a primary action (see mockups): no characters, no voice packs, no unassigned models ("Nothing to assign. Creatures you meet that have no tribe will appear here."), no triggers ("Everything is read. Add a trigger to read only matching lines.").

**Onboarding.** First run opens the wizard (`config.OnboardingCompletedVersion < current`). It is skippable at every step, resumable (step index stored in config), and reachable from Advanced and `/aiva setup`. Skipping leaves the Overview checklist as the guide. Recommended choices come from `WizardModel.Recommend(DeviceReport, freeDisk)`.

**Wording.** Name things by what people control. Mapping lives in a pure `Labels` class so it is testable: "Execution provider" -> "Processor"; "CPU impact low/medium/high" -> "Background load: Light / Balanced / Fast (2/4/8 threads)"; "Exaggeration" and "Bias" -> "Expressiveness"; "Pool (unknown gender)" -> "Any gender"; "Model-id bindings" -> "Creature models"; "Fork" -> "Duplicate". Actions keep one verb through the flow ("Download" button -> "Downloaded Kokoro" toast).

**Accessibility (ImGui equivalents).** AA contrast tokens; status never colour-only; every icon-only button has a tooltip; hit targets at least one frame height; tab order follows layout with `SetItemDefaultFocus` on the safe action in modals; everything sized in em so 100-200% GlobalScale works; reduced-motion switch; `Esc` closes modals; no hover-only information (disabled reasons also appear as muted text next to the control when the reason is important, e.g. engine not ready).

## 7. Architecture of the UI code

```
UI/
  ConfigurationWindow.cs     shell only (~200 lines): sidebar, page host, footer, modals
  UiContext.cs               record bundling ports/delegates/config/save/StatusCenter (replaces 24 ctor args)
  Pages/        IPage.cs, OverviewPage, EnginePage, VoicesPage, CastingPage(+Defaults/Npcs/Players views),
                ReadingPage, DeliveryPage, AdvancedPage
  Wizard/       SetupWizardWindow.cs
  Components/   (see section 4) + existing Controls, TriggerList (re-skinned)
  State/        pure, Dalamud-free, compiled into the test project (already globbed in the csproj)
```

New or changed `UI/State` models (all unit tested; ImGui-free):
`NavModel`, `StatusCenter`, `DownloadQueueModel` (extracted from CW:851-919, takes `IModelProvisioner` + clock), `EngineSessionModel` (warm-up state from CW:708-744), `CachedProbes` (downloaded cache, process memory, dir size; injectable clock and IO), `WorldDirectory` (CW:133-159, 2282), `EngineViewModel`/`DeviceViewModel` (headline, severity, primary action), `OverviewChecklistModel`, `VoiceLibraryModel`, `PreviewController`, `CharacterTableModel` (replaces the `characterPending` tuple dictionary, CW:92), `CastingDraftGuard` (wraps `CastingTabModel`), `WizardModel`, `MigrationNoticeModel`, `Labels`, `ThemeTokens`.

Rules for implementers:
- Page classes receive `UiContext`; no page takes more than it needs from it. Pages never construct synthesizers or touch `Infrastructure.*` (removes CW:316-317). Group keys come from the engine catalog port (section 9).
- No pixel literals in `Pages/`, `Components/` (except `Theme`, `Ui`), or `Wizard/`. Enforced by `UiHygieneTests`, a source-text scan of `src/AIVoiceActing/UI/**/*.cs` that fails on `SetNextItemWidth(<digit>`, `new Vector2(<digit>` and `TableSetupColumn(... , <digit>f)` outside the allowlist, and on emoji code points in string literals.
- The draw layer stays thin: `Draw()` reads a model, calls components, forwards events to the model/ports.
- Keep persist-on-release and the cached-probe rule (no file/process probing per frame).

## 8. Phased implementation plan

Order matters: P0 and P1 are small and independent; P2 is the structural pivot; P3 pages can run in parallel after P2; P4-P6 depend on the companion plan's contract (section 9) but can start against adapters; P7 closes.

### P0. Fix the dead modals, icons, status visibility (implementer, about 0.5 day)
- Files: `UI/ConfigurationWindow.cs`, `UI/Components/Controls.cs`.
- Do: make `OpenPopup` and `BeginPopupModal` use the same literal id for Fork, New, Import preset and Import assignments (the latter is declared twice per tab prefix; give each a stable `###` id); replace `🗑`/`▶` with FontAwesome via `ImRaii.PushFont(UiBuilder.IconFont)` + `FontAwesomeIcon.Trash/Play.ToIconString()` and tooltips; move `DrawStatusLine` out of the scrolling area (draw in a `BeginChild` with remaining height reserved for a one-line footer).
- Tests: none for ImGui. Add a manual QA note in the PR: open each modal, delete a row, see an error status while scrolled.
- Acceptance: all four modals open and complete their action in game; no `?` glyphs; status line visible on every tab.

### P1. Foundation: theme, scale helpers, components, hygiene test (engineer)
- Create: `UI/Components/{Theme,Ui,Icons,IconButton,Card,Chip,StatusPill,Banner,EmptyState,FormRow,SectionHeader,SearchBox,SegmentedControl,ModalHost,ConfirmDialog,ProgressRow,VoiceFingerprint}.cs`, `UI/State/{ThemeTokens,StatusCenter,Labels}.cs`, `UI/IToastSink.cs`; plugin entry wires a `DalamudToastSink` over `INotificationManager`.
- Re-skin `Controls.cs` to use tokens (Section uses `SectionHeader`; `HelpMarker` uses icon; `Button` shows the disabled reason as muted text option).
- Add `UiHygieneTests.cs` (source scan, allowlist = `Theme.cs`, `Ui.cs`; initial baseline excludes legacy `ConfigurationWindow.cs`, shrinking as pages move).
- Tests: `ThemeTokensTests` (token resolver flips by luminance, contrast >= 4.5 against both bg values), `StatusCenterTests` (severity, sticky errors, expiry with fake clock, undo action, de-duplication), `LabelsTests`.
- Acceptance: tests green; a throwaway `Components` demo is not needed; `dotnet build` passes; no behaviour change in tabs.

### P2. Shell split, `UiContext`, state extraction (engineer; the risky step)
- Create `UI/UiContext.cs`, `UI/Pages/IPage.cs`; reduce `ConfigurationWindow` to sidebar + page host + footer + `ModalHost` (target under 250 lines). Move each existing tab verbatim into a page class so behaviour is preserved: `EnginePage` (old Engine), `CastingDefaultsView`, `CharactersView` (NPC/Player share one class), `ReadingPage` (old Chat), `DeliveryPage` (old Test + emotion/expressive cards), `AdvancedPage` (keybinds, experimental, folders). Add a stub `OverviewPage` and `VoicesPage` that forward to the old content until P3/P5.
- Extract state: `DownloadQueueModel`, `EngineSessionModel`, `CachedProbes`, `WorldDirectory`, `PreviewController` (from `SpeakFireAndForget`/`SpeakVoiceTest`).
- Remove CW:316-317 Infrastructure reference by introducing a UI-local `IEngineGroups` seam supplied by the plugin root (temporary until the engine catalog port exists).
- Tests: `DownloadQueueModelTests` (sequential order, cancel mid-file, failure stops queue and surfaces to `StatusCenter`, progress reset, fake `IModelProvisioner`), `EngineSessionModelTests`, `CachedProbesTests` (refresh interval with fake clock), `PreviewControllerTests` (single flight, cancel-previous, error state), `NavModelTests`.
- Acceptance: no user-visible functional change except nav (sidebar) and footer; `ConfigurationWindow.cs` < 250 lines; no file in `UI/` over 500 lines; tests green; AGENTS.md "UI consumes ports only" holds (grep for `Infrastructure` in `UI/` returns nothing).

### P3. Page redesigns (parallel, one implementer per page, after P2)
- **P3a Overview + footer** (implementer): `OverviewPage`, `OverviewChecklistModel`, `StatusPill`, master switch and volume moved here; migration banner slot. Tests: `OverviewChecklistModelTests` (steps and next action from facts). Acceptance: fresh install shows the checklist, finished install shows "All set".
- **P3b Reading** (implementer): consolidate Chat sources into cards; channel presets with search and "select category" actions; triggers with empty-state; fold `StylesWindow` in and make `/aivastyles` open Reading at the style card (`NavModel.OpenAt`); delete `StylesWindow.cs` and its wiring in `AIVoiceActingPlugin.cs`. Tests: `ChannelNamesTests` extended for search; `NavModelTests` for deep link. Acceptance: no pixel literals in the page; all former settings still reachable (checklist of config properties in the PR).
- **P3c Characters** (implementer): `CharacterTableModel` + table with named, sortable columns, search with "N of M", row Tune popup, trash with Undo toast, add-character form (inline validation), import/export via `ModalHost`, empty states. Tests: `CharacterTableModelTests` (sort, filter, pending commit once, bulk delete count, undo restores). Acceptance: header row present, 1000 rows scroll smoothly with `ImGuiListClipper`.
- **P3d Casting Defaults** (implementer): preset bar with overflow menu, `CastingDraftGuard` + unsaved bar + confirm on discard, race/gender tree with summaries, unbound-models inbox with empty state; all destructive paths through `ConfirmDialog`. Tests: `CastingDraftGuardTests` (switch with dirty requires confirm, discard resets, save clears), keep `CastingTabModelTests` passing. Acceptance: dirty state visible in three places (bar, nav dot, buttons).
- **P3e Delivery + Advanced** (implementer): rename jargon via `Labels`, regroup knobs, audition card replaces Test tab, `ShowAdvanced` and `ReduceMotion` config fields (default false, additive). Tests: `LabelsTests` extended; `TestBenchModelTests` unchanged. Acceptance: no setting lives inside a collapsible engine card.

### P4. Engine and Device page against the new contract (engineer, after companion plan lands the engine/device ports)
- `EnginePage`, `EngineViewModel`, `DeviceViewModel`, engine cards with badges (Fast, Slower, GPU recommended, Experimental), device card with detected adapters and preference, fallback banner with actions, downloads table using `DownloadQueueModel` and sizes from the catalog.
- Tests: `EngineViewModelTests` table-driven: not installed, loading, ready on GPU, ready on CPU by choice, fell back with reason, failed, no adapter detected, Wine/Linux note; `DeviceViewModelTests` for preference resolution.
- Acceptance: every state in the table renders a headline, severity icon, and at least one action; switching engine mid-line stays safe (existing behaviour); text for fallback reason comes from the port, not invented in the UI.

### P5. Voice library, picker, fingerprint (engineer, after the voice metadata port exists)
- `VoicesPage`, `VoiceLibraryModel` (search over name/accent/description, filters, sort, installed-only, pack install state), `VoiceCard`/`VoiceRow`, `VoicePicker` popup used by Casting and Characters, retire `VoiceSetEditor.cs` and the prefix-based `VoiceLanguage/VoiceSex` filters in the UI (domain code stays until the companion plan removes it).
- Tests: `VoiceLibraryModelTests` (search by accent word, filter combos, empty result descriptor, "N of M" counts, missing-voice placeholder row), `VoiceFingerprintTests` (deterministic bar heights per id, range bounds).
- Acceptance: browsing 200 voices stays under about 1 ms of model work per frame (filter result cached by inputs, not recomputed per frame); preview works from library, picker and Casting.

### P6. Onboarding wizard and migration notice (engineer)
- `UI/Wizard/SetupWizardWindow.cs`, `WizardModel`, `MigrationNoticeModel`, `/aiva setup` in `CommandRouter` (+ test), config fields `OnboardingCompletedVersion`, `OnboardingStep`, `MigrationNoticeAckedVersion` (additive; the companion plan's versioned schema owns the migration of old files).
- Tests: `WizardModelTests` (recommendation from device report and free disk, step gating, skip, resume, download failure keeps position), `MigrationNoticeModelTests` (groups report lines, ack persists, no notice when nothing changed), `CommandRouterTests` for `/aiva setup`.
- Acceptance: first run on a clean profile reaches "Hear it" in 4 steps without opening Settings; Skip at any step leaves a usable plugin; upgraded profile shows the banner once.

### P7. Polish and QA (implementer)
- `NowSpeakingWindow` as in 5.7, `Sidebar` narrow mode, `Esc` and focus checks, reduced motion, tooltip audit (every icon-only button), copy review with `Labels`, remove legacy `Controls` overloads that are unused, flip `UiHygieneTests` to cover the whole UI tree.
- QA matrix to run manually and record in the PR: GlobalScale 100/150/200%; narrow window (44 em) and default; engine states (missing, loading, ready, failed, fell back); download cancel/fail/resume; Linux/Wine session; light and dark Dalamud themes.
- Acceptance: hygiene test green on the full tree; zero known emoji or pixel literals; QA matrix filled.

Estimated order of subagent hand-offs: P0 (implementer) -> P1 (engineer) -> P2 (engineer) -> P3a..e (5 implementers in parallel, each owns distinct files; `ConfigurationWindow.cs` and `UiContext.cs` are frozen during P3) -> P4/P5 (2 engineers in parallel) -> P6 (engineer) -> P7 (implementer).

## 9. Contract with the GPU/voices plan (assumed; reconcile when that file exists)

`docs/plans/2026-10-gpu-engines-and-voices.md` did not exist when this plan was written, so its "Contract for the UI plan" section could not be read. The UI needs the following. If the other plan names things differently, only the thin adapter layer in `UI/State` changes.

| Need | Assumed shape (Ports, Domain-pure DTOs) | Used by |
|---|---|---|
| Engine catalog | `IEngineCatalog.List()` -> `EngineInfo { Key, DisplayName, Blurb, Speed (Fast/Slower/Slow), Experimental, SupportsGpu, Assets[], TotalBytes }`; engine keys and asset group keys come from here, so the UI never embeds literals | P4, Overview |
| Device probe | `IDeviceProbe.Detect()` -> `DeviceReport { Adapters[ { Vendor (Nvidia/Amd/Intel/Other), Name, VramBytes, Backends[] } ], Platform (Windows/Linux-Wine/Other), Notes }` | Engine, wizard |
| Device preference | config: `DevicePreference { Mode (Auto/Gpu/Cpu), AdapterId? }` | Engine |
| Runtime engine status | `ISpeechSynthesizer.Status` -> `EngineStatus { State (NotInstalled/Loading/Ready/FellBack/Failed), RequestedDevice, ActiveDevice, Reason, CanRetryGpu }`; plus `Retry(device)` | Overview, footer, Engine |
| Downloads | existing `IModelProvisioner` plus `FreeDiskBytes(path)` and per-asset size; resume flag if supported | Engine, wizard |
| Voice library | `IVoiceLibrary.List()` -> `VoiceInfo { Id, DisplayName, Accent, Sex, AgeBand, Description, Attribution, License, PackId }`; `IVoiceLibrary.Packs()` -> `VoicePackInfo { Id, Name, Bytes, Installed }`; install via the provisioner | Voices, picker, Casting, Characters |
| Preview | existing `ISpeechSynthesizer` + `ISpeechQueue` path with the render cache; no new port | `PreviewController` |
| Expressiveness | `Configuration.DefaultExaggeration` stays; UI labels it "Expressiveness"; per-voice `ExaggerationBias` shown as "Expressiveness boost" | Delivery, Tune popup |
| Config schema | versioned config (`Version`) with automatic migration; `IConfigMigrationReport { FromVersion, ToVersion, Changes[ { Area, Before, After, Message } ] }` readable after load | Migration notice |
| Additive UI fields | `OnboardingCompletedVersion`, `OnboardingStep`, `MigrationNoticeAckedVersion`, `ShowAdvanced`, `ReduceMotion`, `LastPage`, `LastPreviewLine?` | Wizard, shell |
| Voice renames | migrated `VoiceSlotDto.Id`/`VoiceProfile.ReferenceVoiceId` values must resolve in `IVoiceLibrary`; unresolved ids show as a "Missing voice" row with a Replace action (replaces the "(missing)" combo hack) | Casting, Characters |

Until those ports exist, P2-P3 use only current ports. P4-P6 may start behind adapters (`UI/State/Adapters/*`) that synthesize `EngineInfo`, `DeviceReport` and `VoiceInfo` from today's `ModelCatalog` groups, `VoiceCatalog.Describe` and `Synth.IsReady/NotReadyReason`, so the swap to the real ports is mechanical.

Alignment checklist for the moment the companion plan appears: (1) confirm port and DTO names, (2) confirm who writes the migration report and when it is readable, (3) confirm free-disk and resume capabilities, (4) confirm whether voice previews need a per-voice sample line, (5) confirm that `Configuration` additions above do not collide with its schema bump.

## 10. Risks and open questions
- Dead-modal diagnosis (bug 4) comes from reading ID strings, not from running the game; P0 verifies it before changing anything.
- Game font handles and window-size scaling behaviour depend on the installed Dalamud build; P1 includes a short spike to confirm `Window.Size` scaling and `NewGameFontHandle` usage and records the result in `Ui.cs` comments.
- Sidebar adds a small amount of per-frame layout work; keep page models cached by input, and draw only the selected page.
- The unit-test host cannot load Dalamud, so Components and Pages are verified manually; that is why logic is pushed into `UI/State`.
- If the companion plan changes engine keys, the saved `config.LastPage` and wizard fields are unaffected (they do not reference engines).
