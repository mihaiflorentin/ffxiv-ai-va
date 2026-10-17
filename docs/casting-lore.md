# Built-in casting and its lore

The Default casting preset is derived from `src/AIVoiceActing/Domain/voices.json` on every start; it is never stored. A user's own presets and per-character overrides are separate and are not touched when this casting changes.

## Rules

1. No American-accent voice anywhere in the built-in casting. That means no stock `af_*` or `am_*` voice, and nothing whose library accent is `en-US`. Accent is judged from the voice library, never from the phonemizer: the Isles pack speaks through the en-us phonemizer but is Irish, Welsh, Scottish and so on, and is allowed. `CastingLoreTests` enforces this.
2. English only. No stock voice from a non-English bank (Kokoro prefixes e, f, h, i, j, p, z). The foreign-accented voices come from the `aiva-accented` pack, which pronounces English through an English phonemizer and passed its intelligibility gate (see that pack's README).
3. Gender-appropriate. Female rows get female voices and male rows get male voices. There are no exceptions. Where a lore gap forces a stand-in (no Irish woman exists) the nearest accent of the right sex is used and noted below.
4. Spread. Every race row has at least three voices, and picks are random per character, so one race does not sound identical across its NPCs.

Voices available: stock British `bf_alice`, `bf_emma`, `bf_isabella`, `bf_lily`, `bm_daniel`, `bm_fable`, `bm_george`, `bm_lewis`; the `aiva-isles` pack (Irish x2 male, Scottish, Welsh, Northern, Midlands, Southern, each male and female except Irish); the `aiva-accented` pack (Spanish, French (female only), Hindi, Italian, Japanese, Portuguese, Chinese; male and female each).

Granularity: the casting grid has one row per race and sex, so clans are expressed by mixing voices inside the race row (a Highlander-leaning voice and a Midlander-leaning one both appear in the Hyur row). A character's clan does not choose its voice. Per-clan rows would need a change to the casting grid and the Voices page and are left as a follow-up.

Pitch below 1 = deeper or bigger, above 1 = lighter or smaller. Abbreviations: RP = stock British, Isles voices are `isles.*`, accented voices are `accented.*`.

## Playable races

| Race (clan) | Sex | Voices | Why |
|---|---|---|---|
| Hyur (Midlander, Highlander, Doman, Hingan) | M | Midlands, Southern, Northern (0.95), Scottish (0.95), RP `bm_lewis`, `bm_daniel`, `bm_fable`, Japanese-accented man | Midlanders are the everyday Eorzean mix, so plain English regional voices and RP. Highlanders are Ala Mhigan: Northern and Scottish, a little lower. Doman and Hingan Hyur are the Japanese-accented voice (one in eight). |
| Hyur | F | Midlands, Southern, Northern, Scottish, RP `bf_lily`, `bf_alice`, `bf_emma`, Japanese-accented woman | Same split as the men. |
| Elezen (Wildwood, Duskwight) | M | RP `bm_daniel`, `bm_george`, `bm_lewis`, `bm_fable`, pitch 0.95 to 0.97 | Ishgardian and Gridanian Elezen are formal, aristocratic and tall: stock RP only, slightly lowered. |
| Elezen | F | RP `bf_isabella`, `bf_emma`, `bf_alice`, `bf_lily`, pitch 0.96 to 0.98 | Same. |
| Lalafell (Plainsfolk, Dunesfolk) | M | RP `bm_fable`, `bm_lewis`, Southern, Midlands, pitch 1.12, speed 1.05 | Small, light and quick. Plainsfolk are cheery (`bm_fable`, Southern); Dunesfolk are sharp Ul'dah merchants (`bm_lewis`, Midlands). |
| Lalafell | F | RP `bf_lily`, `bf_isabella`, Southern, Midlands, pitch 1.12, speed 1.05 | Same. |
| Miqo'te (Seeker of the Sun, Keeper of the Moon) | M | Southern, Midlands, RP `bm_fable` (Seekers); Welsh, Irish x2 (Keepers) | Seekers are open, nomadic and lively. Keepers are forest-dwelling and lyrical, so Welsh and Irish lilts. |
| Miqo'te | F | Southern, Midlands, RP `bf_lily`, `bf_alice` (Seekers); Welsh (Keepers) | Same. There is no Irish woman, so the Keeper side leans on Welsh. |
| Roegadyn (Sea Wolf, Hellsguard) | M | Northern (0.92), Scottish (0.90) for Sea Wolves; RP `bm_george` (0.88), `bm_lewis` (0.86), Midlands (0.90) for Hellsguard | Big people. The Sea Wolves of Limsa are the Nordic flavour, and no Nordic voice exists, so the Northern voices stand in. Hellsguard are gruffer, so lower RP and Midlands. |
| Roegadyn | F | Northern, Scottish, RP `bf_emma`, `bf_alice`, Midlands, pitch 0.92 to 0.94 | Same split, less deep. |
| Au Ra (Raen, Xaela) | M | Japanese-, Chinese- and Hindi-accented men | Raen come from Othard with a Far Eastern feel (Japanese, Chinese). Xaela are the varied steppe tribes (Chinese, Hindi, plus the Japanese-leaning side). |
| Au Ra | F | Japanese-, Chinese- and Hindi-accented women | Same. |
| Hrothgar (The Lost, Helions) | M | Scottish (0.84), Northern (0.84), RP `bm_george` (0.80), `bm_lewis` (0.82), Midlands (0.85), speed 0.94 | The deepest, slowest cast: big leonine warriors. Highland and Northern voices fit the tribal feel. |
| Hrothgar | F | RP `bf_emma`, Northern, Scottish, Midlands, pitch 0.88 to 0.90, speed 0.96 | The game reads Hrothgar women as ungendered for the voice group. They are women, so the container routes them to this female cast instead of the mixed Unknown row. |
| Viera (Rava, Veena) | M | Spanish-accented man, Welsh, RP `bm_daniel`, Southern | Viera men are rare and apart from the world: one foreign accent, lyrical Welsh, formal RP. |
| Viera | F | French-accented woman (Rava), Welsh, RP `bf_isabella`, `bf_alice`, Southern (Veena) | Rava of Golmore are reclusive and elegant: French accent and Welsh lilt. Veena are worldly and lighter: RP and Southern. |
| Unknown / Ungendered row | any | RP `bm_lewis`, `bf_emma`, `bf_alice`, `bm_fable`; Midlands and Southern, male and female | A neutral British mix for speakers whose identity cannot be read. This is the one row with both sexes by design. |

## Beast tribes and allied societies

Each tribe has a pool (used when the sex is unknown), plus a male and a female list. The pool is the male and female lists together. All lists are of the right sex.

| Tribe | Voices (male / female) | Why |
|---|---|---|
| Amalj'aa | Northern (0.88), Scottish (0.90), RP `bm_george` (0.88) / Northern, RP `bf_emma` (0.90) | Lizard warriors of the Thanalan desert: deep, gruff and slow. |
| Ananta | Hindi-accented man (1.04) / Hindi-accented woman (1.07), theatrical (bias 1.0) | Snake-people with a South Asian flavour, dramatic delivery. |
| Arkasodara | Hindi-accented man (0.92) / Hindi-accented woman (0.95) | Tall, mighty Tural bear folk with the same South Asian flavour, calmer. |
| Dragons | RP `bm_george` (0.82), `bm_daniel` (0.83) / RP `bf_emma`, `bf_isabella` (0.86) | Ancient, regal and enormous: lowest RP. |
| Dwarves | Scottish, Welsh (0.90) / Scottish, Welsh (0.95) | The classic fantasy dwarf: Scottish and Welsh mining-folk. |
| Garleans | Italian-accented man (0.95), RP `bm_daniel` / Italian-accented woman (0.97), RP `bf_isabella` | The Empire is Rome-flavoured: Italian accent for the legions, formal RP for officers. |
| Goblins | RP `bm_fable` (1.10), Southern, Midlands (faster) / Southern, RP `bf_lily` | Quick, nasal Ul'dah hagglers: bright Southern and fast speech. |
| Ixal | RP `bm_lewis`, Midlands (1.10, speed 1.12) / Midlands, RP `bf_alice` | Chirpy, fast bird-folk of the Black Brush. |
| Kobolds | Welsh (0.95), Northern (0.95) / Welsh | Rowdy mining-folk: Welsh and Northern mining accents. |
| Kojin | Japanese-accented man (0.90, bias 0.5) / Japanese-accented woman (0.93) | Onokoro turtle folk, Hingan flavour, stoic and slow. |
| Loporrits | RP `bm_fable` (1.18), Southern (1.15) / RP `bf_lily` (1.18), Southern (1.15) | Small, bubbly moon rabbits. |
| Mamool Ja | Hindi-accented man (0.93) / Hindi-accented woman | Lizard soldiers with a South Asian flavour, the same family as the Ananta and Arkasodara but gruffer. |
| Moogles | RP `bm_fable` (1.22), Midlands (1.20) / RP `bf_lily` (1.25), Southern (1.22) | Tiny and cute: highest pitch, brisk speech. |
| Namazu | Japanese-accented man (1.10, bias 0.9) / Japanese-accented woman (1.12) | Playful catfish traders of Hingashi: chatty and light. |
| Nu Mou | Welsh, RP `bm_daniel` (about 0.95, speed 0.90) / Welsh, RP `bf_isabella` (speed 0.90) | Slow, scholarly sages. |
| Omicrons | RP `bm_lewis`, `bm_daniel` / RP `bf_emma`, `bf_isabella` (0.95, speed 0.96) | Calm, measured machine voices. |
| Pelupelu | Italian-accented man (1.05) / Italian-accented woman (1.07) | Jovial, plump Tural traders: warm, gesturing Italian. |
| Pixies | Irish x2 (1.12) / Welsh, Scottish (1.10) | Lilting fey folk: Irish men. There is no Irish woman, so Welsh and Scottish stand in. |
| Qitari | Spanish-accented man (1.05) / Spanish-accented woman (1.07), bias 0.75 | Tural folk with a Mesoamerican flavour. |
| Sahagin | Northern, Irish `ie_m_02` (0.94) / Northern, RP `bf_emma` (0.94) | Sea raiders: the same Northern sea-going flavour as the Sea Wolves. |
| Sylphs | RP `bm_fable`, Southern (1.15) / RP `bf_lily`, `bf_alice`, Southern | Airy, flighty Gridanian spirits. |
| Vanu Vanu | Portuguese-accented man (0.90) / Portuguese-accented woman (0.95) | Tropical Sea of Clouds tribe with a rhythmic, singing cadence; Portuguese stands in. |
| Vath | Chinese-accented man (1.05) / Chinese-accented woman (1.05), bias 0.5 | Clipped, disciplined insect hive. |
| Yok Huy | Spanish-accented man (0.84), RP `bm_george` (0.84) / Spanish-accented woman (0.92), RP `bf_emma` (0.92) | Big, gruff Tural bruisers. |

## Voice sets added in #10

`irishMale`, `scottishMale`, `scottishFemale`, `welshMale`, `welshFemale`, `northernMale`, `northernFemale`, `midlandsMale`, `midlandsFemale`, `southernMale` and `southernFemale` stay as active single-voice building blocks. The race rows above are the multi-voice casts composed from the same voices.

## Unique generated voices (VoiceBlend, #20)

New generated per-character voices are blended from sources that must be:

- English with an English phonemizer, of one phonemizer family and one sex,
- not American: the accent `en-US` or a stock `a[fm]_` id disqualifies a source (the accent is read from the voice library, not the phonemizer, so the Isles pack passes),
- not themselves generated voices (no blends of blends).

When the cast set has fewer than two usable voices of the chosen phonemizer and sex, the pool is widened with the other British stock and regional pack voices that have Kokoro tensors, excluding foreign-accented ones so a Midlands speaker is not blended with a Japanese-accented voice. If the widened pool still has fewer than two, no voice is generated and the character keeps the ordinary pick from the casting. Voices already generated and stored in `voices-generated/` are untouched and keep their ids.

## Upgrading

The Default casting is derived live from `voices.json`, so every user who has not made their own preset gets the new casting at once. Presets users saved, and per-character overrides (hand-picked voices), are unchanged and no config version bump is needed.

One resolution rule changed for existing assignments. A character whose voice was assigned automatically and is a stock voice that the casting no longer uses (for example one of the old Hindi or Chinese bank voices) is recast from the new casting on its next line. A voice the user chose by hand keeps that voice, even when the casting no longer lists it. Voices from enabled packs and already-generated voices stay as before.

## Known limits

- The engine fallback voice for a missing or unknown voice id is still the stock `af_heart` (`KokoroSynthesizer`, `RemoteSynthesizer`). It is not part of the casting. It matters if a pack is disabled: a row whose voices all come from a disabled pack falls back to the engine default. Only rows with stock RP voices in the mix always keep a stock voice.
- Pack voices are experimental; the accent strength can only be judged by ear.
