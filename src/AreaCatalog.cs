using System;
using System.Collections.Generic;
using System.Linq;

namespace SN1DynamicMusic
{
    /// <summary>
    /// One music folder and the biome strings that select it.
    /// Biome strings come from Player.GetBiomeString(), which is either a terrain biome
    /// (StreamingAssets/SNUnmanagedData/Build18/biomes.csv), an AtmosphereVolume.overrideBiome
    /// from a prefab, or one of the hard-coded strings in Player.CalculateBiome().
    /// The full list was extracted from the game files (see MODLOG.md).
    /// </summary>
    public sealed class AreaDef
    {
        public string Folder;
        /// <summary>Case-insensitive prefixes, like the game's own FMODGameParams.onlyInBiome.
        /// A leading '*' means "contains" and only wins when no prefix matches.</summary>
        public string[] Patterns;
        /// <summary>Folders tried, in order, when this folder (and its night variant) is empty.</summary>
        public string[] Fallbacks = new string[0];
        /// <summary>Old folder names still read if this folder is empty.</summary>
        public string[] Legacy = new string[0];
        /// <summary>When empty, fall through to whatever area the biome string would match without this one.</summary>
        public bool InheritBiome;
        public bool CreateNight;
        public bool CreateFirstVisit;
        /// <summary>Shown in the per-biome layered list in the options menu.</summary>
        public bool Layerable;
        /// <summary>Created by the "Minimal" folder preset.</summary>
        public bool Minimal;
        public string Category;
    }

    public static class AreaCatalog
    {
        public const string Fallback = "Fallback";
        public const string MainMenu = "MainMenu";
        public const string Base = "Base";
        public const string Cyclops = "Cyclops";
        public const string Seamoth = "Seamoth";
        public const string PrawnSuit = "PrawnSuit";
        public const string Lifepod = "Lifepod";
        public const string ScannerRoom = "ScannerRoom";
        public const string Observatory = "Observatory";
        public const string BaseMoonPool = "Base - MoonPool";
        public const string BaseRoom = "Base - MultipurposeRoom";
        public const string BaseLargeRoom = "Base - LargeRoom";
        public const string BaseCorridor = "Base - Corridor";

        public const string NightSuffix = " - night";
        public const string FirstVisitSuffix = " - first visit";

        public static readonly List<AreaDef> Areas = new List<AreaDef>();
        public static readonly Dictionary<string, AreaDef> ByFolder =
            new Dictionary<string, AreaDef>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Biome strings where the current music simply keeps going.</summary>
        public static readonly HashSet<string> Passthrough =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PrecursorGun" };

        /// <summary>Folders that are created on disk but never selected by a biome string.</summary>
        public static readonly string[] ExtraFolders =
        {
            Fallback, MainMenu, Base, Cyclops, Seamoth, PrawnSuit,
            BaseMoonPool, BaseRoom, BaseLargeRoom, BaseCorridor,
            "QEP - fallback", "PCF - fallback", "LostRiver - fallback", "ILZ - fallback",
            "ALZ - fallback", "BonesField - fallback", "BloodKelp - fallback", "BloodKelp - fallback - Cave",
        };

        static AreaDef A(string category, string folder, params string[] patterns)
        {
            var a = new AreaDef { Folder = folder, Patterns = patterns, Category = category };
            Areas.Add(a);
            ByFolder[folder] = a;
            return a;
        }

        static AreaDef Fb(this AreaDef a, params string[] f) { a.Fallbacks = f; return a; }
        static AreaDef Night(this AreaDef a) { a.CreateNight = true; a.Layerable = true; return a; }
        static AreaDef FV(this AreaDef a) { a.CreateFirstVisit = true; return a; }
        static AreaDef Inherit(this AreaDef a) { a.InheritBiome = true; return a; }
        static AreaDef Old(this AreaDef a, params string[] l) { a.Legacy = l; return a; }
        static AreaDef Min(this AreaDef a) { a.Minimal = true; return a; }

        static AreaCatalog()
        {
            // ---- Open water, day + night ----
            A("Surface", "SafeShallows", "safeShallows").Night().Min().FV();
            A("Surface", "SafeShallowsCaves", "safeShallows_Cave", "safeShallows_CaveEntrance").Fb("SafeShallows");
            A("Surface", "KelpForest", "kelpForest").Night().Min();
            A("Surface", "KelpForestCaves", "KelpForest_Cave", "KelpForest_CaveEntrance", "KelpForest_Dense").Fb("KelpForest");
            A("Surface", "GrassyPlateaus", "grassyPlateaus").Night().Min();
            A("Surface", "GrassyPlateauCaves", "grassyPlateaus_Cave").Fb("GrassyPlateaus");
            A("Surface", "MushroomForest", "mushroomForest").Night().Min();
            A("Surface", "MushroomForestCaves", "mushroomForest_cave").Fb("MushroomForest");
            A("Surface", "FloatingIslands", "FloatingIsland").Night().Min().FV();
            A("Surface", "MountainRange", "mountains").Night().Min().FV();
            A("Surface", "MountainCaves", "Mountains_Cave", "Mountains_CaveEntrance").Fb("MountainRange");
            A("Surface", "MountainIsland", "Mountains_Island", "Mountains_IslandSurface").Fb("MountainRange");
            A("Surface", "Dunes", "dunes").Night().Min();
            A("Surface", "DunesCaves", "Dunes_Cave").Fb("Dunes");
            A("Surface", "SparseReef", "sparseReef").Night().Min();
            A("Surface", "SparseReefCaves", "SparseReef_Cave").Fb("SparseReef");
            A("Surface", "UnderwaterIslands", "underwaterIslands").Night().Min();
            A("Surface", "UnderwaterIslandsCaves", "underwaterIslands_Cave", "underwaterIslands_IslandCave").Fb("UnderwaterIslands");
            A("Surface", "CragField", "CragField").Night().Min();
            A("Surface", "SeaTreaderPath", "seaTreaderPath").Night().Min();
            A("Surface", "SeaTreaderPathCaves", "seaTreaderPath_Cave").Fb("SeaTreaderPath");
            A("Surface", "Crash", "crashZone").Night().Min().FV();
            A("Surface", "BulbZone", "kooshZone").Night().Min();
            A("Surface", "BulbZoneCaves", "kooshZone_cave").Fb("BulbZone");
            A("Surface", "GrandReef", "grandReef").Night().Min();
            A("Surface", "GrandReefCaves", "GrandReef_Cave").Fb("GrandReef");
            A("Surface", "DeepGrandReef", "deepGrandReef").Fb("GrandReef");
            A("Surface", "Void", "void").Min();

            // ---- Deep ----
            A("Deep", "Jellyshroom", "JellyshroomCaves").Min();
            A("Deep", "BloodKelp", "BloodKelp").Min().Fb("BloodKelp - fallback");
            A("Deep", "BloodKelp - Trench", "BloodKelp_Trench", "BloodKelp_DeepTrench").Fb("BloodKelp", "BloodKelp - fallback");
            A("Deep", "BloodKelpTwo", "BloodKelpTwo").Fb("BloodKelp - fallback", "BloodKelp");
            A("Deep", "BloodKelp - Cave", "BloodKelp_Cave").Fb("BloodKelp - fallback - Cave", "BloodKelp", "BloodKelp - fallback");
            A("Deep", "BloodKelpTwo - Cave", "BloodKelpTwo_Cave").Fb("BloodKelp - fallback - Cave", "BloodKelpTwo", "BloodKelp - fallback", "BloodKelp");

            // ---- Lost River ----
            A("LostRiver", "LostRiver - fallback", "LostRiver").Min();
            A("LostRiver", "LostRiver - Junction", "LostRiver_Junction").Fb("LostRiver - fallback");
            A("LostRiver", "LostRiver - Canyon", "LostRiver_Canyon").Fb("LostRiver - fallback");
            A("LostRiver", "LostRiver - Corridor", "LostRiver_Corridor").Fb("LostRiver - fallback");
            A("LostRiver", "GhostTree", "LostRiver_GhostTree").Fb("LostRiver - fallback");
            A("LostRiver", "LostRiver - TreeCove", "LostRiver_TreeCove").Fb("GhostTree", "LostRiver - fallback");
            A("LostRiver", "BonesField", "LostRiver_BonesField").Fb("BonesField - fallback", "LostRiver - fallback");
            A("LostRiver", "BonesField - Corridor", "LostRiver_BonesField_Corridor").Fb("BonesField - fallback", "LostRiver - fallback");
            A("LostRiver", "LostRiver - SkeletonCave", "LostRiver_SkeletonCave").Fb("LostRiver - fallback");
            A("LostRiver", "LostRiverBase", "Precursor_LostRiverBase");

            // ---- Inactive Lava Zone ----
            A("ILZ", "InactiveLavaZone", "inactiveLavaZone", "ILZ").Min().Fb("ILZ - fallback").Old("InactiveLavaRt");
            A("ILZ", "ILZ - Corridor", "ILZCorridor").Fb("ILZ - fallback", "InactiveLavaZone");
            A("ILZ", "ILZ - Chamber", "ILZChamber").Fb("ILZ - fallback", "InactiveLavaZone");
            A("ILZ", "ILZ - Dragon", "ILZChamber_Dragon").Fb("ILZ - Chamber", "ILZ - fallback", "InactiveLavaZone");
            A("ILZ", "ILZ - CastleTunnel", "ILZCastleTunnel").Fb("ILZ - fallback", "InactiveLavaZone");
            A("ILZ", "ILZ - CastleChamber", "ILZCastleChamber", "ilzLava").Fb("ILZ - fallback", "InactiveLavaZone");
            A("ILZ", "ILZ - ThermalPlant", "Precursor_LavaCastleBase", "PrecursorThermalRoom").Fb("ILZ - fallback", "InactiveLavaZone").FV();

            // ---- Active Lava Zone ----
            A("ALZ", "ActiveLavaZone", "LavaLakes", "ALZChamber", "alz", "lavaZone").Min().Fb("ALZ - fallback");
            A("ALZ", "ALZ cave enterance", "LavaPit").Fb("ALZ - fallback", "ActiveLavaZone");
            A("ALZ", "ALZ lavafalls enterance", "LavaFalls").Fb("ALZ - fallback", "ActiveLavaZone");
            A("ALZ", "LavaPool", "LavaLakes_LavaPool").Fb("ActiveLavaZone", "ALZ - fallback").Layerable = true;

            // ---- Places ----
            A("Places", Lifepod, "Lifepod").Min().FV();
            A("Places", "Aurora - Interior", "crashedShip", "CrashedShip_Interior").Min().Fb("Crash");
            A("Places", "Aurora - GeneratorRoom", "generatorRoom").Fb("Aurora - Interior", "Crash");
            A("Places", "Wrecks",
                "wreck", "WreckInterior", "SafeShallows_WreckInterior", "kelpForest_WreckInterior",
                "GrassyPlateaus_WreckInterior", "mountains_wreckinterior", "grandReef_wreck", "bloodKelp_wreck",
                "Dunes_wreck", "kooshZone_wreck", "mushroomForest_wreck", "seaTreaderPath_wreck",
                "sparseReef_Wreck", "underwaterIslands_wreck", "*wreck").Inherit().Min();
            A("Places", Observatory, "observatory");
            A("Places", ScannerRoom, "maproom");
            A("Places", "Rocket", "Rocket");
            A("Places", "PrecursorCaves", "PrecursorCave", "PrecursorCache");
            A("Places", "QEP - ControlRoom", "Precursor_Gun_ControlRoom").Fb("QEP - fallback");
            A("Places", "QEP - MoonPool", "Precursor_Gun_MoonPoolWater").Fb("QEP - fallback");
            A("Places", "QEP - Rooms", "Precursor_Gun_OuterRooms", "Precursor_Gun_InnerRooms").Fb("QEP - fallback");
            A("Places", "QEP - fallback", "Precursor_Gun").Min();
            A("Places", "PCF - Aquarium", "Prison_Aquarium").Fb("PCF - fallback");
            A("Places", "PCF - MoonPool", "Prison_Moonpool").Fb("PCF - fallback");
            A("Places", "PCF - Antechamber", "Prison_Antechamber").Fb("PCF - fallback");
            A("Places", "PCF - Enterance and UpperRoom", "Prison_UpperRoom").Fb("PCF - fallback");
            A("Places", "PCF - fallback", "Prison").Min();
        }

        /// <summary>Best area for a biome string: longest matching prefix, then longest "contains" pattern.</summary>
        public static AreaDef Match(string biome, AreaDef exclude = null)
        {
            if (string.IsNullOrEmpty(biome)) return null;
            AreaDef best = null;
            int bestScore = -1;
            foreach (var a in Areas)
            {
                if (a == exclude) continue;
                foreach (var p in a.Patterns)
                {
                    int score;
                    if (p[0] == '*')
                    {
                        string needle = p.Substring(1);
                        if (biome.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        score = needle.Length; // contains-matches rank below any prefix match
                    }
                    else
                    {
                        if (!biome.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
                        score = 1000 + p.Length;
                    }
                    if (score > bestScore) { bestScore = score; best = a; }
                }
            }
            return best;
        }

        /// <summary>The ordered list of folders to try for a biome string (without night / first-visit variants).</summary>
        public static List<string> ChainFor(string biome)
        {
            var chain = new List<string>();
            AreaDef exclude = null;
            for (int guard = 0; guard < 4; guard++)
            {
                var a = Match(biome, exclude);
                if (a == null) break;
                AddArea(chain, a);
                if (!a.InheritBiome) break;
                exclude = a;
            }
            return chain;
        }

        static void AddArea(List<string> chain, AreaDef a)
        {
            void Add(string f) { if (!chain.Contains(f, StringComparer.OrdinalIgnoreCase)) chain.Add(f); }
            Add(a.Folder);
            foreach (var l in a.Legacy) Add(l);
            foreach (var f in a.Fallbacks)
            {
                Add(f);
                if (ByFolder.TryGetValue(f, out var fa)) foreach (var l in fa.Legacy) Add(l);
            }
        }

        public static readonly string[] PresetNames =
        {
            "Minimal (main biomes)",
            "Minimal + day/night",
            "Standard (all areas + night)",
            "Everything (+ first visit)",
        };

        private static readonly string[] MinimalExtras = { Fallback, MainMenu, Base };

        /// <summary>Folders for a preset: 0 minimal, 1 minimal + night, 2 standard, 3 everything.</summary>
        public static IEnumerable<string> FoldersForPreset(int preset)
        {
            bool minimal = preset <= 1;
            bool night = preset >= 1;
            bool firstVisit = preset >= 3;
            foreach (var a in Areas)
            {
                if (minimal && !a.Minimal) continue;
                yield return a.Folder;
                if (night && a.CreateNight) yield return a.Folder + NightSuffix;
                if (firstVisit && a.CreateFirstVisit)
                {
                    yield return a.Folder + FirstVisitSuffix;
                    if (a.CreateNight) yield return a.Folder + NightSuffix + FirstVisitSuffix;
                }
            }
            foreach (var f in minimal ? MinimalExtras : ExtraFolders) yield return f;
        }

        public static bool IsNightFolder(string f) =>
            f != null && f.EndsWith(NightSuffix, StringComparison.OrdinalIgnoreCase);

        public static string DayName(string f) =>
            IsNightFolder(f) ? f.Substring(0, f.Length - NightSuffix.Length) : f;
    }
}
