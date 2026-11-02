using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Nautilus.Handlers;

namespace SN1DynamicMusic
{
    // =========================================================
    //  Dynamic Music 2.0
    //  Author: Infinite. 1.x written with Claude Sonnet; 2.0 rebuilt with Claude Opus
    //  from the game's own code and data (see MODLOG.md).
    // =========================================================
    [BepInPlugin(Guid, "SN1 Dynamic Music", "10.0.0")]
    [BepInDependency("com.snmodding.nautilus")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.infinite.sn1dynamicmusic";

        internal static ManualLogSource Log;
        internal static Plugin Instance;
        internal static DynamicMusicConfig Cfg;

        internal MusicLibrary Library;
        internal Director Director;

        public static string ModFolder => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        public static string MusicFolder => Path.Combine(ModFolder, "Music");

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            Log.LogInfo("=== Dynamic Music 2.0 (plugin 10.0.0) ===");

            Cfg = new DynamicMusicConfig();
            Cfg.Load();
            Cfg.Migrate();

            Library = new MusicLibrary(MusicFolder);
            if (!Directory.Exists(MusicFolder))
            {
                int n = Library.EnsureFolders(AreaCatalog.FoldersForPreset(0));
                Log.LogInfo($"First launch: created {n} folders (Minimal preset). More presets: Options → Create folders.");
            }
            Library.EnsureFolders(ZoneFolders());
            Library.Scan();
            Director = new Director(Library, Cfg);

            new Harmony(Guid).PatchAll(Assembly.GetExecutingAssembly());
            OptionsPanelHandler.RegisterModOptions(new MusicModOptions());

            ConsoleCommandsHandler.RegisterConsoleCommand<Action>("dm_next", () => Director.Skip());
            ConsoleCommandsHandler.RegisterConsoleCommand<Action>("dm_rescan", Rescan);
            ConsoleCommandsHandler.RegisterConsoleCommand<Action>("dm_hud", () => { Cfg.ShowDebugHud = !Cfg.ShowDebugHud; Cfg.Save(); });
            ConsoleCommandsHandler.RegisterConsoleCommand<Action>("dm_status", () =>
            {
                string s = Director.StatusLine();
                Log.LogInfo(s);
                Notify(s);
            });

            Log.LogInfo("Music folder: " + MusicFolder);
        }

        private bool _warnedUnsupported;

        private void Update()
        {
            try { Director?.Update(); }
            catch (Exception e) { Log.LogError("Director error: " + e); }
            try { if (Mixer.Ready) DialogueDucking.Update(); }
            catch (Exception e) { Log.LogError("Ducking error: " + e.Message); }
            if (!_warnedUnsupported && Player.main != null && Library.Unsupported.Count > 0)
            {
                _warnedUnsupported = true;
                Notify($"{Library.Unsupported.Count} music file(s) skipped: unsupported format (e.g. {Library.Unsupported[0]}). Convert to .ogg or .mp3");
            }
        }

        private void OnGUI()
        {
            try { Director?.DrawHud(); } catch { }
        }

        private void OnDestroy() => Director?.StopAll();

        // ---------------------------------------------------------
        internal static void Notify(string msg)
        {
            try { ErrorMessage.AddMessage("[Dynamic Music] " + msg); } catch { }
        }

        internal static IEnumerable<string> ZoneFolders()
        {
            foreach (var z in DynamicMusicAPI.Zones)
            {
                yield return z.ZoneID;
                if (z.SupportsLayered) yield return z.ZoneID + AreaCatalog.NightSuffix;
            }
        }

        internal void OnZoneRegistered(CustomZoneDescriptor zone)
        {
            Library.EnsureFolders(new[] { zone.ZoneID }.Concat(zone.SupportsLayered ? new[] { zone.ZoneID + AreaCatalog.NightSuffix } : new string[0]));
            Rescan();
        }

        internal static void Rescan()
        {
            var p = Instance;
            if (p == null) return;
            p.Library.Scan();
            p.Director.OnLibraryRescanned(clearResume: !Cfg.ResumeOnReEntry);
            MenuMusic.Sync();
            var bad = p.Library.Unsupported;
            Notify(bad.Count == 0 ? "Rescanned music folders"
                : $"Rescanned. {bad.Count} file(s) skipped (unsupported format, e.g. {bad[0]}). Convert to .ogg or .mp3");
        }

        internal static void CreateFolders(int preset)
        {
            var p = Instance;
            if (p == null) return;
            int n = p.Library.EnsureFolders(AreaCatalog.FoldersForPreset(preset).Concat(ZoneFolders()));
            Rescan();
            Notify(n == 0 ? "All folders for that preset already exist" : $"Created {n} folder(s) ({AreaCatalog.PresetNames[preset]})");
        }

        internal static void ResetFirstVisits()
        {
            Cfg.FirstVisits.Clear();
            Cfg.Save();
            Instance?.Director.ForceRefresh();
            Notify("First-visit data reset");
        }

        internal static void OpenNexusPage()
        {
            try { System.Diagnostics.Process.Start("https://www.nexusmods.com/subnautica/mods/3426"); }
            catch (Exception e) { Log.LogWarning("Could not open browser: " + e.Message); }
        }

        internal static void OpenMusicFolder()
        {
            try
            {
                Directory.CreateDirectory(MusicFolder);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + MusicFolder + "\"");
            }
            catch (Exception e) { Log.LogError("Open folder failed: " + e.Message); }
        }

        internal static void ImportForCurrentArea()
        {
            var ctx = MusicContext.Sense();
            if (ctx.Kind != ContextKind.Game) { Notify("Load a save first"); return; }
            string folder = ctx.Room ?? ctx.Interior ?? (ctx.InLifepod ? AreaCatalog.Lifepod : AreaCatalog.ChainFor(ctx.Biome).FirstOrDefault());
            if (folder == null) { Notify($"No folder for biome '{ctx.Biome}'"); return; }
            if (ctx.Night && (AreaCatalog.ByFolder.TryGetValue(folder, out var a) && a.CreateNight ||
                              Directory.Exists(Path.Combine(MusicFolder, folder + AreaCatalog.NightSuffix))))
                folder += AreaCatalog.NightSuffix;

            string dir = Path.Combine(MusicFolder, folder);
            Directory.CreateDirectory(dir);
            var files = NativeDialogs.PickFiles($"Add music to [{folder}]",
                "Audio\0*.mp3;*.ogg;*.wav;*.flac;*.aif;*.aiff\0All files\0*.*\0", dir, multi: true);
            int n = 0;
            foreach (var f in files)
            {
                if (!MusicLibrary.AudioExtensions.Contains(Path.GetExtension(f))) continue;
                try { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true); n++; }
                catch (Exception e) { Log.LogError($"Copy failed for {f}: {e.Message}"); }
            }
            if (n > 0) { Notify($"Added {n} file(s) to [{folder}]"); Rescan(); }
        }

        internal static void ImportPack()
        {
            var zip = NativeDialogs.PickFiles("Import music pack", "ZIP archives\0*.zip\0All files\0*.*\0", ModFolder, multi: false).FirstOrDefault();
            if (zip == null) return;
            try { MusicPacks.Import(Instance.Library, Cfg, zip); }
            catch (Exception e) { Log.LogError("Import failed: " + e); Notify("Import failed: " + e.Message); }
            Rescan();
        }

        internal static void ExportPack()
        {
            string dir = NativeDialogs.PickFolder("Choose where to save the music pack");
            if (string.IsNullOrEmpty(dir)) return;
            try { MusicPacks.Export(Instance.Library, Cfg, dir); }
            catch (Exception e) { Log.LogError("Export failed: " + e); Notify("Export failed: " + e.Message); }
        }
    }
}
