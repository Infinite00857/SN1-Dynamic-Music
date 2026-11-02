# SN1 Dynamic Music 2.0

Replaces Subnautica's biome music with your own files, per biome, cave, facility, base and vehicle.
A ground-up rewrite of 1.x (9.3.0), built from the game's real code and data instead of guesses.

Requires BepInEx (Subnautica pack) and Nautilus.

## Install
Put `DynamicMusic.dll` in `BepInEx/plugins/DynamicMusic/`. The first launch creates `Music/` next to it.
To keep 1.x music, copy your old `Music` folder (and `config.json`) into the same folder.

## Adding music
Drop `.mp3 .ogg .wav .flac .aiff` files into a folder, then **Options → Mods → Dynamic Music 2 → Rescan**.
Turn on **Debug overlay** (or console `dm_hud`) to see the biome name, the chosen folder and the fallback chain live.

| Name pattern | What it does |
|---|---|
| `<Folder> - night` | Plays at night (any folder). |
| `<Folder> - first visit`, `<Folder> - night - first visit` | First in-game days after you first arrive, per save. |
| `x-intro.*` + `x-loop.*` in one folder | Intro once, then the loop repeats forever, joined with no gap. |
| `folder.json` | Per-folder settings (below). |

### Folder presets
First launch creates the **Minimal** preset: one folder per main biome or region; caves, rooms and wrecks fall back to these.
Options → *Folder preset* + *Create folders for preset* adds more: Minimal + day/night, Standard (every area + night), Everything (+ first visit).
Creating folders never deletes or moves anything.

### Auto intro+loop
*Make intro+loop from current song* finds where the playing song repeats and writes `<song>-intro.wav` and `<song>-loop.wav`
next to it; the folder then loops that song forever with no gap. *…for all single-song folders* does every folder with one song.
Delete the two .wav files to undo. Works best on game music that repeats; songs with no repeat are reported, not butchered.

### Fallbacks
Each area tries its own folder, then related ones, then `Fallback`, then the option "When a folder has no music"
(default: original soundtrack). For example: `BloodKelp - Cave` → `BloodKelp - fallback - Cave` → `BloodKelp` → `BloodKelp - fallback`;
`KelpForestCaves` → `KelpForest`; any wreck → `Wrecks` → the biome the wreck is in.

`Base`, `Cyclops`, `Seamoth`, `PrawnSuit` override biome music while you're inside them (only if they have tracks).
Base rooms outrank `Base`: `ScannerRoom`, `Observatory`, `Base - MoonPool`, `Base - MultipurposeRoom`, `Base - LargeRoom`, `Base - Corridor`.
Region folders: every Inactive Lava Zone room falls back to `ILZ - fallback` then `InactiveLavaZone`; ALZ rooms to `ALZ - fallback` then
`ActiveLavaZone`; Lost River areas to `LostRiver - fallback`; QEP and PCF rooms to `QEP - fallback` / `PCF - fallback`.
`MainMenu` replaces the title-screen theme.

### folder.json
```json
{
  "volume": 0.8,          // per-folder loudness
  "shuffle": true,        // false = play in file-name order
  "layered": false,       // day/night stems for this folder
  "layerGroup": "lava",   // folders with the same group play as synced stems
  "crossfade": 3.0,       // seconds when entering this folder (-1 = global)
  "mode": "music",        // "silence" or "original" (game's own soundtrack here)
  "loopStartMs": 0, "loopEndMs": 0  // optional loop region for x-loop files
}
```
Exported packs include every `folder.json` and a `pack.json` with layered settings, so a pack imports exactly as made.

### Layered (stems)
With layering on, `<Folder>` and `<Folder> - night` start on the same audio sample and crossfade at dusk/dawn.
Uses the first file by name in each; make stems the same length. `layerGroup` does the same across areas
(e.g. walk from a cave into its biome and the stem crossfades without restarting).

Supported formats: mp3, ogg, wav, flac, aiff. `.m4a`/`.aac`/`.wma`/`.opus` are skipped with a warning; convert them.

## Console
`dm_next` skip track · `dm_status` what's playing and why · `dm_rescan` · `dm_hud` toggle overlay.

## What changed from 1.x
- Biome detection uses the game's exact biome names (extracted from the game files) with longest-prefix matching, like the game itself. Many 1.x keywords never matched anything.
- Vanilla music is muted at its source (the `music` group of `FMODGameParams`), so ambience, story music (intro, rocket, credits, Cyclops fire, death) and sound effects are no longer muted by accident; "original soundtrack" is now a fallback option.
- Audio is scheduled on FMOD's DSP clock: intro→loop joins are sample-accurate, layered stems start on the same sample, fades are smooth and frame-rate independent, and resumes no longer pop (channels start paused and silent before seeking).
- MP3 encoder padding is detected from the LAME header and trimmed (option).
- Settings take effect immediately; no more "leave and re-enter the biome".
- A short biome-change delay stops music flip-flopping at borders.
- First-visit records are per save. Silence-gap length and behaviour are configurable.
- Fixed: the multi-file import picker only returned the folder when several files were selected.
- New folders: caves for SafeShallows/Dunes/SeaTreader/UnderwaterIslands/GrandReef, `MountainIsland`, `BloodKelp - Trench`,
  `LostRiver - Corridor`, `LostRiver - TreeCove`, `InactiveLavaZone`, `Aurora - GeneratorRoom`, `PrecursorCaves`,
  `Observatory`, `ScannerRoom`, `Rocket`, `Base`, `Cyclops`, `Seamoth`, `PrawnSuit`, `MainMenu`.
- Changed: `Aurora - Interior` now covers the whole Aurora (generator room has its own folder that falls back to it);
  `BloodKelp_Trench` uses `BloodKelp - Trench` (was `BloodKelpTwo`); `CragFieldCaves` removed (no such biome);
  `InactiveLavaRt` is read as a legacy name for `InactiveLavaZone`.

## Building
Needs the .NET SDK and your own Subnautica install with BepInEx + Nautilus (game DLLs are referenced from the
install, never shipped here). `dotnet build -c Release`; add `-p:Deploy=true` to copy into the game and
`-p:GameDir=...` if Subnautica lives elsewhere. `MODLOG.md` explains how the game's music system works.

## License
MIT, see [LICENSE](LICENSE). No game files or music are included.
