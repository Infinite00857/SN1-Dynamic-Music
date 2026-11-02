using System;
using System.Linq;
using Nautilus.Options;

namespace SN1DynamicMusic
{
    public class MusicModOptions : ModOptions
    {
        private static readonly string[] NoTrackLabels =
        {
            "Fallback folder, else original OST",
            "Fallback folder, else silence",
            "Keep playing current track",
            "Silence",
            "Original soundtrack",
        };

        public MusicModOptions() : base("Dynamic Music 2")
        {
            var cfg = Plugin.Cfg;
            Action refresh = () => Plugin.Instance?.Director?.ForceRefresh();

            Button("about", "Dynamic Music 2.0 — help (opens Nexus page)", Plugin.OpenNexusPage,
                "Drop MP3/OGG/WAV/FLAC files into the biome folders (Open Music Folder below), then Rescan.\n\n" +
                "Night: put tracks in '<Folder> - night'. Any folder can have a night version.\n" +
                "Intro+loop: name files 'x-intro' and 'x-loop'; the loop joins the intro with no gap and repeats forever.\n" +
                "First visit: '<Folder> - first visit' plays for the first in-game days after you first arrive (per save).\n" +
                "Empty folders fall back to related folders (cave → biome, room → '... - fallback'), then the Fallback folder.\n" +
                "Base / Cyclops / Seamoth / PrawnSuit folders override biome music while you are inside them.\n" +
                "MainMenu replaces the title-screen theme.\n" +
                "folder.json in a folder can set volume, shuffle, layered, layerGroup, crossfade and mode (silence/original).");

            Toggle("enabled", "Mod enabled", cfg.ModEnabled,
                "Off = the original soundtrack plays everywhere.", v => { cfg.ModEnabled = v; MenuMusic.Sync(); refresh(); });

            var vol = ModSliderOption.Create("dm2_volume", "Music volume", 0f, 1f, cfg.MusicVolume, 0.13f, "{0:P0}", 0.01f,
                "Volume of your music. The game's own Music slider also applies on top.");
            vol.OnChanged += (s, e) => { cfg.MusicVolume = e.Value; cfg.Save(); Mixer.ApplyVolume(); };
            AddItem(vol);

            Slider("xfade", "Crossfade length", 0f, 8f, cfg.CrossfadeSeconds, 2f, "{0:F1} s", 0.5f,
                "How long music takes to fade between areas.", v => cfg.CrossfadeSeconds = v);
            Slider("delay", "Biome change delay", 0f, 6f, cfg.BiomeChangeDelay, 1.5f, "{0:F1} s", 0.5f,
                "You must stay in a new area this long before the music changes. Stops flip-flopping at biome borders.",
                v => cfg.BiomeChangeDelay = v);

            var noTrack = ModChoiceOption<string>.Create("dm2_notrack", "When a folder has no music", NoTrackLabels,
                Math.Max(0, Math.Min(NoTrackLabels.Length - 1, cfg.NoTrack)),
                "What plays in an area whose folders (and fallbacks) are all empty.");
            noTrack.OnChanged += (s, e) => { cfg.NoTrack = e.Index; cfg.Save(); refresh(); };
            AddItem(noTrack);

            Toggle("resume", "Resume on re-entry", cfg.ResumeOnReEntry,
                "Returning to one of your last 6 areas continues its track where it left off (fades in, no pop).",
                v => cfg.ResumeOnReEntry = v);
            Toggle("sametrack", "Keep same song across areas", cfg.KeepSameTrackAcrossAreas,
                "If the new area's folder contains a file with the same name as the song playing, it keeps playing.",
                v => cfg.KeepSameTrackAcrossAreas = v);

            Toggle("firstvisit", "First-visit music", cfg.FirstVisitEnabled,
                "'<Folder> - first visit' plays when you first reach an area in a save. Not used for layered areas.",
                v => { cfg.FirstVisitEnabled = v; refresh(); });
            Slider("fvdays", "First-visit music lasts", 0.5f, 10f, cfg.FirstVisitDays, 2f, "{0:F1} days", 0.5f,
                "In-game days after the first visit during which first-visit music is used.", v => { cfg.FirstVisitDays = v; refresh(); });
            Button("resetfv", "Reset first-visit data", () => { Plugin.ResetFirstVisits(); },
                "Forget first visits for all saves.");

            Toggle("silence", "Silence between tracks", cfg.SilenceBetweenTracks,
                "After a song ends, wait a random time before the next one.", v => cfg.SilenceBetweenTracks = v);
            Slider("silmin", "Silence: shortest", 0f, 600f, cfg.SilenceMinSeconds, 30f, "{0:F0} s", 10f, null, v => cfg.SilenceMinSeconds = v);
            Slider("silmax", "Silence: longest", 0f, 1200f, cfg.SilenceMaxSeconds, 600f, "{0:F0} s", 10f, null, v => cfg.SilenceMaxSeconds = v);
            Toggle("silsurvive", "Silence continues in new areas", cfg.SilenceSurvivesBiomeChange,
                "On: entering a new area during a silence gap waits for the gap to end. Off: new areas start music right away.",
                v => cfg.SilenceSurvivesBiomeChange = v);

            Toggle("layered", "Layered day/night (all biomes)", cfg.LayeredDayNight,
                "Day and night tracks play in sync and crossfade when time changes (like stems). " +
                "Uses the first file by name in '<Folder>' and '<Folder> - night'; make them the same length. Takes effect immediately.",
                v => { cfg.LayeredDayNight = v; refresh(); });

            string[] modes = { "Normal", "Layered" };
            foreach (var area in AreaCatalog.Areas.Where(a => a.Layerable).OrderBy(a => a.Folder, StringComparer.OrdinalIgnoreCase))
            {
                string folder = area.Folder;
                string tip = folder == "LavaPool"
                    ? "Layered: LavaPool and ActiveLavaZone play as synced stems; moving between them crossfades without restarting."
                    : "Layered: day and night tracks play in sync and crossfade at dusk/dawn. Normal: separate day and night playlists.";
                var c = ModChoiceOption<string>.Create("dm2_layer_" + Id(folder), folder + " mode", modes,
                    cfg.GetBiomeLayeredSetting(folder) ? 1 : 0, tip);
                c.OnChanged += (s, e) => { cfg.SetBiomeLayered(folder, e.Index == 1); refresh(); };
                AddItem(c);
            }

            Toggle("story", "Let story music play", cfg.YieldToStoryMusic,
                "Fade your music out during the crash-landing intro and the rocket launch so the game's own score plays.",
                v => { cfg.YieldToStoryMusic = v; refresh(); });
            Toggle("duck", "Lower music during dialogue", cfg.DuckDuringDialogue,
                "On (game default): music gets quieter while the PDA, AI or Cyclops is talking. " +
                "Off: music stays at full volume (this also affects the original soundtrack).",
                v => cfg.DuckDuringDialogue = v);
                        Toggle("mp3trim", "Gapless MP3 (encoder padding trim)", cfg.Mp3GaplessTrim,
                "Reads the LAME header of MP3s and trims encoder silence so intros and loops join without a gap. " +
                "OGG files don't need this.", v => { cfg.Mp3GaplessTrim = v; Plugin.Rescan(); });
            Toggle("hud", "Debug overlay", cfg.ShowDebugHud,
                "Shows the biome name, chosen folder, fallback chain and track on screen. Handy when building a music pack.",
                v => cfg.ShowDebugHud = v);

            Button("skip", "Skip current track", () => Plugin.Instance?.Director?.Skip(), "Also: console command dm_next");
            Button("rescan", "Rescan music folders", Plugin.Rescan, "Pick up added/removed files without restarting.");
            Button("importbiome", "Import music for current area", Plugin.ImportForCurrentArea,
                "Pick audio files to copy into the folder of the area you're in (night folder at night).");
            Button("open", "Open music folder", Plugin.OpenMusicFolder, Plugin.MusicFolder);
            Button("importpack", "Import music pack (.zip)", Plugin.ImportPack,
                "Also applies the pack's layered settings when it has a pack.json.");
            Button("exportpack", "Export music pack (.zip)", Plugin.ExportPack,
                "Zips every folder (with folder.json files) plus your layered settings.");
            Button("loopnow", "Make intro+loop from current song", LoopMaker.MakeForCurrent,
                "Finds where the song playing now repeats and writes '<song>-intro.wav' + '<song>-loop.wav' into its folder, " +
                "so it loops forever with no gap. The folder then plays only that intro+loop. " +
                "Delete the two .wav files to undo. Takes a few seconds; works best on game/video-game music that repeats.");
            Button("loopall", "Make intro+loop for all single-song folders", LoopMaker.MakeForSingleTrackFolders,
                "Same as above for every folder that has exactly one song.");
                        var preset = ModChoiceOption<string>.Create("dm2_preset", "Folder preset", AreaCatalog.PresetNames,
                Math.Max(0, Math.Min(AreaCatalog.PresetNames.Length - 1, cfg.FolderPreset)),
                "Minimal: one folder per main biome/region (caves, rooms and wrecks fall back to these).\n" +
                "Minimal + day/night: adds '- night' folders for outdoor biomes.\n" +
                "Standard: every cave, room, facility, base room and vehicle folder, with night folders.\n" +
                "Everything: Standard plus first-visit folders.\nPick one, then press Create folders.");
            preset.OnChanged += (s, e) => { cfg.FolderPreset = e.Index; cfg.Save(); };
            AddItem(preset);
            Button("rebuild", "Create folders for preset", () => Plugin.CreateFolders(Plugin.Cfg.FolderPreset),
                "Adds any missing folders for the chosen preset. Never deletes or moves anything; empty folders are harmless.");
        }

        private static string Id(string s) => s.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");

        private void Toggle(string id, string label, bool value, string tip, Action<bool> set)
        {
            var t = ModToggleOption.Create("dm2_" + id, label, value, tip);
            t.OnChanged += (s, e) => { set(e.Value); Plugin.Cfg.Save(); };
            AddItem(t);
        }

        private void Slider(string id, string label, float min, float max, float value, float def, string fmt, float step, string tip, Action<float> set)
        {
            var o = ModSliderOption.Create("dm2_" + id, label, min, max, value, def, fmt, step, tip);
            o.OnChanged += (s, e) => { set(e.Value); Plugin.Cfg.Save(); };
            AddItem(o);
        }

        private void Button(string id, string label, Action onClick, string tip)
        {
            AddItem(ModButtonOption.Create("dm2_" + id, label, _ => onClick(), tip));
        }
    }
}
