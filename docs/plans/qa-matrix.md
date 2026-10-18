# Manual QA matrix

Checklist for the owner before a release is tagged. Automated tests cover the logic; this list covers what only a real game window, GPU and Wine prefix can show. Tick each box, and note the build version and date at the top of your run.

Build under test: ______  Date: ______  Platform: ______

## 1. Interface scale and window size

Open the settings window on every tab (Home, Voices, What's read, Settings, plus Settings > Advanced) and the Now Speaking window at each scale. Dalamud: Settings > Look & Feel > Global font scale.

| Check | 100% | 150% | 200% |
|---|---|---|---|
| Nothing is clipped or overlaps (buttons, chips, fingerprints, tables) | [ ] | [ ] | [ ] |
| Text columns and tables keep their proportions | [ ] | [ ] | [ ] |
| Voice picker, Tune popover and every modal fit on screen | [ ] | [ ] | [ ] |
| Now Speaking: fingerprint, voice name, mood and the four icon buttons all visible | [ ] | [ ] | [ ] |
| Footer status bar readable, actions clickable | [ ] | [ ] | [ ] |

Narrow window (drag the settings window to its minimum width, at 100% and 200%):

- [ ] Tab bar still shows all four tabs (or scrolls cleanly)
- [ ] Voice rows wrap or ellipsize instead of overflowing
- [ ] Filter chips wrap onto a new line
- [ ] Home status line and setup card do not clip
- [ ] No horizontal scrollbar appears on any tab except where a table needs one

Light and dark Dalamud themes (if you can switch): [ ] secondary text still readable, [ ] accent colour has enough contrast.

## 2. Quality tiers

For each tier, set it from Home ("Set up recommended" or Customize) and again from Settings > Voice quality. Play one NPC line in a cutscene and one calm quest line.

| Tier | Starts | Calm line speaks | Emotional line speaks | Status line is correct | Switching away and back keeps downloads |
|---|---|---|---|---|---|
| Fast (Kokoro) | [ ] | [ ] | n/a | [ ] | [ ] |
| Balanced (Kokoro + Turbo on emotional lines) | [ ] | [ ] | [ ] routed to Turbo | [ ] | [ ] |
| Most expressive (Turbo for all) | [ ] | [ ] | [ ] | [ ] | [ ] |

- [ ] "Most expressive" is blocked with a plain reason when no usable graphics card exists
- [ ] The advisor's download size and free-disk check match what actually downloads
- [ ] Settings > Advanced shows the raw engine and still agrees with the chosen tier

## 3. GPU, fallback and retry

- [ ] GPU present: Processor shows the detected card and "Ready, running on your graphics card"
- [ ] Processor set to CPU: Turbo runs on the CPU and says so
- [ ] Force a GPU failure (unplug/disable the device, or use an unsupported card): the plugin drops to the CPU with a plain reason, speech keeps working
- [ ] After a crash during GPU work, the crash guard keeps the GPU off on the next start and the UI explains why
- [ ] "Retry" on the graphics card re-probes and re-enables it when it works
- [ ] Warm-up progress is shown, and the first line is not silently dropped
- [ ] Time to first audio feels acceptable on GPU (note: ______ s)

## 4. Downloads

Test on a throwaway models folder or by deleting the group in Settings > Downloads.

| Case | Expected | Pass |
|---|---|---|
| Cancel mid-download | Stops promptly; partial files kept; status says cancelled; no error banner | [ ] |
| Fail (turn the network off mid-download) | Plain error with a Retry action; no half-installed engine is used | [ ] |
| Resume after cancel or fail | Continues from the partial file, not from zero; progress starts at the right fraction | [ ] |
| Resume after closing the game mid-download | Same as above on next start | [ ] |
| Low disk space | Blocked before starting, with the amount needed | [ ] |
| Switching tier while downloading | Queue updates; no duplicate downloads | [ ] |
| Indeterminate progress with Reduce motion on | Static bar, no animation | [ ] |

## 5. Linux / Wine (XIVLauncher.Core)

Do not use a second Wine build on the prefix. Run on the installed dev plugin only.

- [ ] Plugin loads under Wine with no errors in `dalamud.log`
- [ ] Kokoro (Fast) speaks
- [ ] Turbo on the CPU speaks
- [ ] Turbo on the GPU (Vulkan) speaks, or falls back cleanly with a reason
- [ ] Audio plays through the system output with no crackle on long lines
- [ ] Fonts and icons render (FontAwesome buttons are not `?`)
- [ ] Settings window and Now Speaking behave at the scale you play at
- [ ] Models and packs open from "Open packs folder" (or show a path if the file manager is unavailable)

## 6. Characters, races and beast tribes

Hear at least three different NPCs per row, and confirm the voice is the right sex and never American. Use the Voices tab to check what each row contains, then the game to confirm.

| Group | Female NPC | Male NPC | Notes |
|---|---|---|---|
| Hyur (Midlander, Highlander) | [ ] | [ ] | |
| Elezen | [ ] | [ ] | |
| Lalafell | [ ] | [ ] | pitch up, quicker |
| Miqo'te | [ ] | [ ] | |
| Roegadyn | [ ] | [ ] | pitch down |
| Au Ra | [ ] | [ ] | |
| Hrothgar | n/a | [ ] | |
| Viera | [ ] | n/a (or [ ]) | |
| Anyone else / unknown sex | [ ] | [ ] | |

Beast tribes: [ ] Amalj'aa [ ] Sylph [ ] Kobold [ ] Sahagin [ ] Ixal [ ] Vanu Vanu [ ] Vath [ ] Moogle [ ] Kojin [ ] Ananta [ ] Qitari [ ] Namazu [ ] Loporrit [ ] Pixie [ ] Mamool Ja [ ] Gnath

- [ ] A creature with no tribe binding shows up in the Beast tribes inbox and can be assigned or ignored
- [ ] Children and elders sound their age when "Make children and elders sound their age" is on
- [ ] A named NPC override (Use for... > Named NPC) wins over the race row
- [ ] Player characters keep a stable voice across sessions
- [ ] A removed or disabled voice pack falls back to a British voice of the same sex (check the log line), never an American one

## 7. Now Speaking and shortcuts

- [ ] Shows speaker name, fingerprint, the voice's friendly name (not its id) and the mood
- [ ] Fingerprint pulses while speaking; with Reduce motion on it stays still
- [ ] Replay repeats the last line; Skip ends the current line; Stop clears the queue; each is disabled with a reason when it cannot act
- [ ] Gear opens the settings window on the Voices tab
- [ ] Closing the overlay sticks across restarts; the setting turns it back on
- [ ] Every icon-only button in the plugin shows a tooltip on hover
- [ ] Esc closes the open modal or popup (voice picker, Tune, confirm dialogs, import/export) and nothing else
- [ ] Reduce motion: no pulse, no sliding progress bar, no other animation

## 8. Config migration from v1

Keep a copy of the old `AIVoiceActing.json` from a v1 install before testing.

- [ ] First start on the upgraded plugin shows the migration notice once, and dismissing it persists
- [ ] Enabled, volume, chat channels, keybinds and triggers carry over
- [ ] Saved per-character voices still resolve; ones that used American ids (`af_*`, `am_*`) still play (via the stand-in) and show their old voice
- [ ] Custom casting presets are still listed and selectable
- [ ] The old engine choice maps to the right quality tier on Home
- [ ] No "missing voice" entries remain for voices the new library still provides
- [ ] Downgrading and re-upgrading does not lose settings
- [ ] A fresh install (no config) lands on the Home "Get started" guide

## Sign-off

- [ ] All sections pass, or each failure is filed as an issue
- [ ] No release tag until this list is complete
