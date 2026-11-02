using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;

namespace SN1DynamicMusic
{
    /// <summary>Win32 file/folder pickers (also work under Wine/Proton).</summary>
    internal static class NativeDialogs
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BROWSEINFO
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public IntPtr pszDisplayName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszTitle;
            public uint ulFlags;
            public IntPtr lpfn;
            public IntPtr lParam;
            public int iImage;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFO lpbi);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAME
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile; // raw buffer: multi-select results are \0-separated
            public int nMaxFile;
            public IntPtr lpstrFileTitle;
            public int nMaxFileTitle;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpstrInitialDir;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

        public static string PickFolder(string title)
        {
            try
            {
                var bi = new BROWSEINFO { lpszTitle = title, ulFlags = 0x0001 | 0x0010 | 0x0040 };
                IntPtr pidl = SHBrowseForFolderW(ref bi);
                if (pidl == IntPtr.Zero) return null;
                var sb = new StringBuilder(1024);
                bool ok = SHGetPathFromIDListW(pidl, sb);
                CoTaskMemFree(pidl);
                return ok ? sb.ToString() : null;
            }
            catch (Exception e) { Plugin.Log.LogError("Folder picker failed: " + e.Message); return null; }
        }

        public static List<string> PickFiles(string title, string filter, string initialDir, bool multi)
        {
            const int OFN_ALLOWMULTISELECT = 0x200, OFN_EXPLORER = 0x80000, OFN_FILEMUSTEXIST = 0x1000,
                OFN_PATHMUSTEXIST = 0x800, OFN_NOCHANGEDIR = 0x8;
            const int chars = 65536;
            var result = new List<string>();
            IntPtr buf = Marshal.AllocHGlobal(chars * 2);
            try
            {
                for (int i = 0; i < chars * 2; i++) Marshal.WriteByte(buf, i, 0);
                var ofn = new OPENFILENAME
                {
                    lStructSize = Marshal.SizeOf(typeof(OPENFILENAME)),
                    lpstrFilter = filter,
                    nFilterIndex = 1,
                    lpstrFile = buf,
                    nMaxFile = chars,
                    lpstrTitle = title,
                    lpstrInitialDir = initialDir,
                    Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | (multi ? OFN_ALLOWMULTISELECT : 0),
                };
                if (!GetOpenFileNameW(ref ofn)) return result;

                var parts = new List<string>();
                int offset = 0;
                while (offset < chars * 2)
                {
                    string s = Marshal.PtrToStringUni(buf + offset);
                    if (string.IsNullOrEmpty(s)) break;
                    parts.Add(s);
                    offset += (s.Length + 1) * 2;
                }
                if (parts.Count == 1) result.Add(parts[0]);
                else for (int i = 1; i < parts.Count; i++) result.Add(Path.Combine(parts[0], parts[i]));
            }
            catch (Exception e) { Plugin.Log.LogError("File picker failed: " + e.Message); }
            finally { Marshal.FreeHGlobal(buf); }
            return result;
        }
    }

    /// <summary>pack.json inside an exported zip: carries settings that are not per-folder.</summary>
    public class PackManifest
    {
        public string name;
        public string version = "2";
        public string biomeLayered;
        public bool? layeredDayNight;
    }

    internal static class MusicPacks
    {
        public static void Export(MusicLibrary lib, DynamicMusicConfig cfg, string destDir)
        {
            string zipPath = Path.Combine(destDir, "SN1MusicPack_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (string dir in Directory.GetDirectories(lib.Root))
                {
                    string folder = Path.GetFileName(dir);
                    foreach (string file in Directory.GetFiles(dir))
                    {
                        string ext = Path.GetExtension(file);
                        bool keep = MusicLibrary.AudioExtensions.Contains(ext) ||
                                    Path.GetFileName(file).Equals("folder.json", StringComparison.OrdinalIgnoreCase);
                        if (!keep) continue;
                        // audio is already compressed; storing it is much faster
                        var level = ext.Equals(".json", StringComparison.OrdinalIgnoreCase) || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
                            ? CompressionLevel.Optimal : CompressionLevel.NoCompression;
                        zip.CreateEntryFromFile(file, "Music/" + folder + "/" + Path.GetFileName(file), level);
                    }
                }
                var manifest = new PackManifest
                {
                    name = Path.GetFileNameWithoutExtension(zipPath),
                    biomeLayered = cfg.BiomeLayeredRaw,
                    layeredDayNight = cfg.LayeredDayNight,
                };
                var entry = zip.CreateEntry("pack.json");
                using (var w = new StreamWriter(entry.Open()))
                    w.Write(JsonConvert.SerializeObject(manifest, Formatting.Indented));
            }
            Plugin.Log.LogInfo("Exported music pack: " + zipPath);
            Plugin.Notify("Music pack exported: " + Path.GetFileName(zipPath));
        }

        public static void Import(MusicLibrary lib, DynamicMusicConfig cfg, string zipPath)
        {
            int files = 0;
            string rootFull = Path.GetFullPath(lib.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in zip.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    if (name.Equals("pack.json", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var r = new StreamReader(e.Open()))
                            ApplyManifest(cfg, r.ReadToEnd());
                        continue;
                    }
                    if (name.EndsWith("_ModConfig.json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("Music/", StringComparison.OrdinalIgnoreCase)) name = name.Substring(6);
                    if (name.Length == 0) continue;

                    string dest = Path.GetFullPath(Path.Combine(lib.Root, name));
                    if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    {
                        Plugin.Log.LogWarning("Skipping unsafe zip path: " + e.FullName);
                        continue;
                    }
                    if (e.Name.Length == 0) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                    files++;
                }
            }
            Plugin.Log.LogInfo($"Imported {files} file(s) from {Path.GetFileName(zipPath)}.");
            Plugin.Notify($"Imported {files} file(s) from {Path.GetFileName(zipPath)}");
        }

        private static void ApplyManifest(DynamicMusicConfig cfg, string json)
        {
            try
            {
                var m = JsonConvert.DeserializeObject<PackManifest>(json);
                if (m == null) return;
                if (!string.IsNullOrEmpty(m.biomeLayered))
                {
                    foreach (var part in m.biomeLayered.Split(','))
                    {
                        var kv = part.Trim().Split('=');
                        if (kv.Length == 2 && kv[0].Length > 0)
                            cfg.SetBiomeLayered(kv[0], kv[1].Equals("true", StringComparison.OrdinalIgnoreCase));
                    }
                }
                if (m.layeredDayNight == true) cfg.LayeredDayNight = true;
                cfg.Save();
                Plugin.Log.LogInfo("Applied pack.json settings.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("pack.json unreadable: " + e.Message); }
        }
    }

    // =========================================================
    //  Interop API (same names as 1.x so other mods keep working)
    // =========================================================
    public enum CustomZoneType { Biome, POI }

    public class CustomZoneDescriptor
    {
        public string ZoneID;
        public string DisplayName;
        public CustomZoneType ZoneType;
        public Func<bool> IsActive;
        public float FadeTimeSec = 0f;
        public int Priority = 0;
        public bool SupportsLayered = false;
    }

    public static class DynamicMusicAPI
    {
        private static readonly List<CustomZoneDescriptor> _zones = new List<CustomZoneDescriptor>();
        internal static IReadOnlyList<CustomZoneDescriptor> Zones => _zones;

        public static void RegisterZone(CustomZoneDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (string.IsNullOrWhiteSpace(descriptor.ZoneID)) throw new ArgumentException("ZoneID must not be empty.", nameof(descriptor));
            if (descriptor.IsActive == null) throw new ArgumentException("IsActive must not be null.", nameof(descriptor));
            if (_zones.Any(z => z.ZoneID.Equals(descriptor.ZoneID, StringComparison.OrdinalIgnoreCase)))
            {
                Plugin.Log?.LogWarning($"[DynamicMusicAPI] Zone '{descriptor.ZoneID}' already registered.");
                return;
            }
            _zones.Add(descriptor);
            Plugin.Log?.LogInfo($"[DynamicMusicAPI] Registered zone '{descriptor.ZoneID}' ({descriptor.ZoneType}).");
            Plugin.Instance?.OnZoneRegistered(descriptor);
        }

        /// <summary>The folder currently playing (or null). Handy for other mods' HUDs.</summary>
        public static string CurrentFolder => Plugin.Instance?.Director?.ActiveFolder;

        /// <summary>The track file name currently playing (or null).</summary>
        public static string CurrentTrack => Plugin.Instance?.Director?.ActiveTrack;
    }
}
