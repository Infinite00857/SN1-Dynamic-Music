# MODLOG — Dynamic Music 2.0

## Setup (2026-11-01)
- Game: Subnautica (Windows build via notproton on macOS), `~/Library/Application Support/Steam/steamapps/common/Subnautica`.
  Unity Mono, Addressables bundles in `StreamingAssets/aa/StandaloneWindows64`. Build stamp 10/03/2025.
- Loader: BepInEx Subnautica pack (Tobey) + Nautilus 1.0.0-pre.56.
- **Do not launch the game without the user's OK.** User tests.
- Old mod (1.x / 9.3.0) is read-only in `../sub1musicmod - old`; used as the feature spec.
- Tools: `~/.dotnet` SDK 10 + ilspycmd; UnityPy in a scratch venv. Decompiled code and dumps are kept out of the repo.

## How the game plays music (from decompiled Assembly-CSharp + scene data)
- `Player.LateUpdate` → `CalculateBiome()` each frame: `observatory` / `generatorRoom` / `crashedShip` from
  special trigger components; otherwise `AtmosphereDirector.GetBiomeOverride()` (highest-priority
  `AtmosphereVolume.overrideBiome`, or a batch's atmosphere prefab); `PrecursorGun` when out of water in
  precursor areas with no override; else the terrain biome map (`biomes.csv` + `biomeMap.bin`).
- Biome music = `FMODGameParams` components under GameObject `music` in `main.unity`. Every 0.5 s each one
  plays its looping FMOD event if `GetBiomeString()` **starts with** `onlyInBiome` (case-insensitive) and stops it
  otherwise. Several can match at once (e.g. `LostRiver_GhostTree_*` matches `lostRiver` and `LostRiver_GhostTree`).
  It feeds FMOD parameters `depth` (player y), `playerDamage` (health fraction), `oxygenLeft`, `time` (hour 0–24).
- Ambience beds = same component under GameObject `background` (left untouched by the mod).
- All `music` players use InteriorState=Always (they keep playing inside bases/subs).
- Other music outside that system: `MainMenuMusic` (title theme + background), `IntroLifepodDirector.music`,
  `Player.deathMusic`, `SubFire.fireMusic`, `LaunchRocket.endMusic`, `EndCreditsManager.endMusic`,
  `Incubator.hatchingMusic`.
- Day/night: `DayNightCycle.IsDay()` = dayScalar in (0.125, 0.875); a day is 1200 s; `GetDay()` = days elapsed.

### Vanilla music table (`music` group, prefix → FMOD event)
| prefix | event |
|---|---|
| safeShallows | event:/env/music/reef_background_music |
| kelpForest | kelp_background_music |
| grassyPlateaus | grasy_background_music |
| mushroomForest | mushroom_background_music |
| floatingIsland | floating_islands_background_music |
| mountains | mountain_underwater_music |
| dunes | dunes_background_music |
| SparseReef | sparse_reef_background_music |
| UnderwaterIslands | underislands_background_music |
| seaTreaderPath | tredpath_music |
| crashZone | crash_zone_music |
| crashedShip | ship_ambience_music |
| generatorRoom | generator_room_music |
| wreck | wreak_ambience_big_music |
| kooshZone | koosh_background_music |
| grandReef, deepGrandReef | grandreef_background_music |
| jellyshroomCaves | jellyshroom_caves_music |
| BloodKelp | blood_kelp_background_music |
| lostRiver | lost_river_background_music |
| LostRiver_GhostTree | lost_river_ghost_tree |
| LostRiver_TreeCove | lostriver_tree |
| ILZ, lava, lavaZone, alz | lava_background_music |
| Precursor, Prison | prec_base_background_music |
| PrecursorCave | prec_cave_music |
| Prison_Antechamber | antechamber_music |
| Prison_Aquarium | prec_aquarium_music |
| observatory | zen_music |
| maproom | (asset in another bundle) |
| arctic | arctic_background_music (unused biome) |

Not covered by any vanilla prefix (silent in vanilla): CragField, void, Lifepod, `*_WreckInterior` style strings
(they start with the biome name, so they get that biome's music), Rocket.

## Every biome string the game can report
Terrain: safeShallows kelpForest grassyPlateaus underwaterIslands mushroomForest kooshZone grandReef arctic
inactiveLavaZone unassigned crashZone void sparseReef dunes bloodKelp mountains seaTreaderPath bloodKelpTwo CragField.
Plus ~150 `AtmosphereVolume.overrideBiome` strings scanned from all 3,749 prefab bundles (parse raw
MonoBehaviour bytes; typetrees are stripped; overrideBiome is the last string before two padded bools).
`T.cs`-style offline check maps every one of them; see AreaCatalog.cs.

## Route
BepInEx plugin + Harmony + Nautilus options. FMOD Core API directly (not Nautilus `TryPlaySound`, which starts
channels unpaused → the 1.x "resume pop").
- Mute vanilla: prefix `FMODGameParams.UpdateParams`, only when `transform.parent.name == "music"`.
- Menu theme: prefix `MainMenuMusic.Play`, start only `evt2` (background) when MainMenu folder has tracks.
- Own `ChannelGroup` under bus `bus:/master/SFX_for_pause/nofilter/music` (game Music slider still applies).
- Voices: `createSound(CREATESTREAM|ACCURATETIME|NONBLOCKING)`, `playSound(paused:true)`, seek, `setDelay` on the
  group DSP clock, `addFadePoint` curves, `setLoopPoints` + `setLoopCount(-1)` for loops.

## 1.x bugs found
1. Muted any `FMOD_CustomEmitter` whose asset path contained "music"/"theme" → also muted story music
   (rocket `endMusic`, credits, Cyclops `fireMusic`) and blocked `Start` of emitters.
2. Keyword lists: many never matched (`ILZ_Corridor`, `bulbZone`, `crater_edge`, `InactiveLavaRt`,
   `LostRiver_BonesField_SkeletonCave`...), substring "crash" caught `crashedShip` and `CrashZone`,
   `Mountains_Island*` went to MountainRange, inactiveLavaZone (open ILZ) matched nothing.
3. `SkipLeadingSilence` used `Sound.lock` on streams, which always fails → never did anything.
4. Intro→loop gap up to one poll (50 ms) + MP3 padding; layered stems started one after another (not synced).
5. Multi-select file picker marshalled `lpstrFile` as string → only the directory came back.

## Not verified in game yet (needs user test)
- Mixer attaches under the music bus; volume slider + game Music slider.
- Vanilla biome music muted / restored ("original" fallback).
- MP3 gapless auto-detect: whether FMOD already trims LAME padding (logged per file: "MP3 gapless: ...").
- `IntroLifepodDirector.IsActive` / `LaunchRocket.isLaunching` used as "story music" windows.

## Test pass 1 (2026-11-02, user)
- Works: menu, biomes, night, first visit, resume, Base/Cyclops/PrawnSuit, Observatory (biome string), ALZ, GhostTree.
- MP3: FMOD does NOT trim LAME padding; it also outputs the info frame (trim = 1152 + delay + 529, e.g. 2257).
- Seamoth folder held only a .m4a → not scanned (now warned). FMOD under notproton can't be relied on for AAC.
- `maproom` biome string never appears (the vanilla 'maproom' music gate is dead). Scanner room now detected from the
  base cell grid: `Base.GetCell(Base.WorldToGrid(playerPos))` → CellType.MapRoom/MapRoomRotated.
- ILZ visit was ILZCorridor → ILZ - Corridor > ILZ - fallback > Fallback; InactiveLavaZone is now the region fallback.
- "Failed to call function Stop of class FMOD_CustomLoopingEmitter" comes from the game's VFXController.BroadcastMessage("Stop"), not the mod.

## Dialogue ducking
Not in code: Master Bank.strings has `snapshot:/duck when voice` (strings are a prefix trie; the snapshot names follow
`snapshot:/`: LOWPASS, aurora warnings, bloodkelp_verb, ..., cyclops interior, duck when voice, filter down/up).
Option off → stop every instance of that snapshot each frame.

## Loop maker
FMOD OPENONLY + readData (main thread, 2 MB/frame) → LoopFinder on a worker thread: 32 log bands/45 ms, cosine
self-similarity diagonals with a 12 s window, loudness penalty, bias to long loops, then sample-accurate cross-correlation
and a 100 ms baked tail crossfade. Offline on the user's tracks: 0.5–3 s per song; exact rips correlate 1.00; a
non-repeating title theme is rejected (best 56%).

## Next
- User test: Seamoth (after converting), scanner room, ILZ rooms, ducking toggle, loop maker in game.
