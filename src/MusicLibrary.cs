using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace SN1DynamicMusic
{
    /// <summary>Optional folder.json inside any music folder. Travels with exported packs.</summary>
    public class FolderSettings
    {
        /// <summary>Volume multiplier for this folder (1 = unchanged).</summary>
        public float volume = 1f;
        /// <summary>false = play files in name order.</summary>
        public bool shuffle = true;
        /// <summary>Force layered day/night for this folder (same as the options toggle).</summary>
        public bool layered = false;
        /// <summary>Folders with the same layerGroup play as synced stems; moving between them only crossfades.</summary>
        public string layerGroup = null;
        /// <summary>"music" (default), "silence" or "original" (let the game's own soundtrack play here).</summary>
        public string mode = null;
        /// <summary>Crossfade length when entering this folder, in seconds. Negative = use global setting.</summary>
        public float crossfade = -1f;
        /// <summary>Intro/loop: optional manual loop region in milliseconds (0 = whole file).</summary>
        public uint loopStartMs = 0;
        public uint loopEndMs = 0;
    }

    public sealed class FolderInfo
    {
        public string Name;
        public string Path;
        public List<string> Tracks = new List<string>();
        public string Intro;
        public string Loop;
        public FolderSettings Settings = new FolderSettings();
        public bool HasTracks => Tracks.Count > 0;
        public bool IsIntroLoop => Intro != null && Loop != null;
        /// <summary>Files that play as normal playlist entries (intro/loop pair excluded).</summary>
        public List<string> PlaylistTracks =>
            IsIntroLoop ? Tracks.Where(t => t != Intro && t != Loop).ToList() : Tracks;
        public string FirstTrackByName =>
            Tracks.OrderBy(t => System.IO.Path.GetFileName(t), StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    public sealed class MusicLibrary
    {
        public static readonly HashSet<string> AudioExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".ogg", ".wav", ".flac", ".aiff", ".aif" };

        /// <summary>Formats the game's FMOD can't decode here; reported so files aren't silently ignored.</summary>
        public static readonly HashSet<string> UnsupportedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".m4a", ".aac", ".wma", ".opus", ".webm", ".mp4", ".alac", ".ape" };

        public List<string> Unsupported { get; } = new List<string>();

        private readonly Dictionary<string, FolderInfo> _folders =
            new Dictionary<string, FolderInfo>(StringComparer.OrdinalIgnoreCase);

        public string Root { get; }
        public int Version { get; private set; }

        public MusicLibrary(string root) { Root = root; }

        public IEnumerable<FolderInfo> All => _folders.Values;

        public FolderInfo Get(string name) =>
            name != null && _folders.TryGetValue(name, out var f) ? f : null;

        public bool Has(string name) => Get(name)?.HasTracks == true;

        /// <summary>Creates missing folders. Never deletes or moves anything. Returns how many were new.</summary>
        public int EnsureFolders(IEnumerable<string> folders)
        {
            Directory.CreateDirectory(Root);
            int made = 0;
            foreach (var f in folders)
            {
                try
                {
                    string d = System.IO.Path.Combine(Root, f);
                    if (Directory.Exists(d)) continue;
                    Directory.CreateDirectory(d);
                    made++;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Could not create folder '{f}': {e.Message}"); }
            }
            return made;
        }

        public void Scan()
        {
            _folders.Clear();
            Unsupported.Clear();
            Version++;
            if (!Directory.Exists(Root)) { Plugin.Log.LogWarning("Music folder missing: " + Root); return; }

            int total = 0;
            foreach (string dir in Directory.GetDirectories(Root))
            {
                var info = new FolderInfo { Name = System.IO.Path.GetFileName(dir), Path = dir };
                info.Tracks = Directory.GetFiles(dir)
                    .Where(f => AudioExtensions.Contains(System.IO.Path.GetExtension(f)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var f in Directory.GetFiles(dir))
                    if (UnsupportedExtensions.Contains(System.IO.Path.GetExtension(f)))
                        Unsupported.Add(info.Name + "/" + System.IO.Path.GetFileName(f));
                info.Intro = info.Tracks.FirstOrDefault(f => NameEnds(f, "-intro"));
                info.Loop = info.Tracks.FirstOrDefault(f => NameEnds(f, "-loop"));
                if (info.Intro == null || info.Loop == null) info.Intro = info.Loop = null;

                string json = System.IO.Path.Combine(dir, "folder.json");
                if (File.Exists(json))
                {
                    try { info.Settings = JsonConvert.DeserializeObject<FolderSettings>(File.ReadAllText(json)) ?? new FolderSettings(); }
                    catch (Exception e) { Plugin.Log.LogWarning($"[{info.Name}] folder.json unreadable: {e.Message}"); }
                }

                _folders[info.Name] = info;
                if (info.HasTracks)
                {
                    total += info.Tracks.Count;
                    Plugin.Log.LogInfo($"[{info.Name}] {info.Tracks.Count} track(s)" +
                        (info.IsIntroLoop ? $" — intro+loop: {System.IO.Path.GetFileName(info.Intro)} → {System.IO.Path.GetFileName(info.Loop)}" : ""));
                }
            }
            foreach (var u in Unsupported)
                Plugin.Log.LogWarning($"Unsupported format, skipped: {u} — convert it to .ogg or .mp3");
            Plugin.Log.LogInfo(total == 0
                ? "No tracks found. Drop MP3/OGG/WAV/FLAC files into the folders in: " + Root
                : $"Scan complete: {total} track(s).");
        }

        private static bool NameEnds(string path, string suffix) =>
            System.IO.Path.GetFileNameWithoutExtension(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>Folders that declare this layer group in folder.json.</summary>
        public IEnumerable<FolderInfo> InLayerGroup(string group) =>
            _folders.Values.Where(f => f.HasTracks &&
                string.Equals(f.Settings.layerGroup, group, StringComparison.OrdinalIgnoreCase));

        public void WriteSettings(string folder, FolderSettings s)
        {
            string dir = System.IO.Path.Combine(Root, folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(System.IO.Path.Combine(dir, "folder.json"), JsonConvert.SerializeObject(s, Formatting.Indented));
        }
    }
}
