using System;
using System.Collections.Generic;
using System.Linq;

namespace SN1DynamicMusic
{
    public enum ContextKind { Menu, Loading, Cinematic, Game }

    public struct MusicContext
    {
        public ContextKind Kind;
        public string Biome;
        public bool Night;
        public float DayScalar;
        public double Day;
        public string Slot;
        public string Interior;   // Base / Cyclops / Seamoth / PrawnSuit or null
        public string Room;       // base room folder (ScannerRoom, Observatory, Base - MoonPool...) or null
        public bool InLifepod;
        public string ZoneId;     // external zone from another mod

        public static MusicContext Sense()
        {
            var c = new MusicContext();
            try
            {
                if (uGUI_MainMenu.main != null && uGUI_MainMenu.main.isActiveAndEnabled) { c.Kind = ContextKind.Menu; return c; }
                var p = Player.main;
                if (p == null || WaitScreen.IsWaiting) { c.Kind = ContextKind.Loading; return c; }
                if (Plugin.Cfg.YieldToStoryMusic && (IntroLifepodDirector.IsActive || LaunchRocket.isLaunching))
                {
                    c.Kind = ContextKind.Cinematic;
                    return c;
                }

                c.Kind = ContextKind.Game;
                c.Biome = p.GetBiomeString();
                var dnc = DayNightCycle.main;
                if (dnc != null)
                {
                    c.Night = !dnc.IsDay();
                    c.DayScalar = dnc.GetDayScalar();
                    c.Day = dnc.GetDay();
                }
                else c.Day = -1;
                c.Slot = SaveLoadManager.main != null ? SaveLoadManager.main.GetCurrentSlot() : null;
                c.InLifepod = p.escapePod.value;

                if (p.inSeamoth) c.Interior = AreaCatalog.Seamoth;
                else if (p.inExosuit) c.Interior = AreaCatalog.PrawnSuit;
                else if (p.currentSub != null)
                {
                    if (p.currentSub.isCyclops) c.Interior = AreaCatalog.Cyclops;
                    else if (p.currentSub.isBase && p.currentWaterPark == null)
                    {
                        c.Interior = AreaCatalog.Base;
                        c.Room = BaseRoom(p);
                    }
                }

                c.ZoneId = ActiveZone()?.ZoneID;
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Context sense failed: " + e.Message);
                c.Kind = ContextKind.Loading;
            }
            return c;
        }

        /// <summary>Which kind of base piece the player stands in, from the base's own cell grid.</summary>
        private static string BaseRoom(Player p)
        {
            var b = p.currentSub.GetComponent<Base>();
            if (b == null) return null;
            var pos = p.transform.position;
            var cell = b.GetCell(b.WorldToGrid(pos));
            if (cell == Base.CellType.Empty || cell == Base.CellType.Foundation)
                cell = b.GetCell(b.WorldToGrid(pos + UnityEngine.Vector3.up));
            switch (cell)
            {
                case Base.CellType.MapRoom:
                case Base.CellType.MapRoomRotated: return AreaCatalog.ScannerRoom;
                case Base.CellType.Observatory: return AreaCatalog.Observatory;
                case Base.CellType.Moonpool:
                case Base.CellType.MoonpoolRotated: return AreaCatalog.BaseMoonPool;
                case Base.CellType.Room: return AreaCatalog.BaseRoom;
                case Base.CellType.LargeRoom:
                case Base.CellType.LargeRoomRotated: return AreaCatalog.BaseLargeRoom;
                case Base.CellType.Corridor:
                case Base.CellType.Connector: return AreaCatalog.BaseCorridor;
                default: return null;
            }
        }

        private static CustomZoneDescriptor ActiveZone()
        {
            CustomZoneDescriptor best = null;
            foreach (var z in DynamicMusicAPI.Zones)
            {
                bool on;
                try { on = z.IsActive(); } catch { on = false; }
                if (!on) continue;
                if (best == null ||
                    (z.ZoneType == CustomZoneType.POI && best.ZoneType != CustomZoneType.POI) ||
                    (z.ZoneType == best.ZoneType && z.Priority > best.Priority))
                    best = z;
            }
            return best;
        }
    }

    public enum TargetKind { Folder, Layered, Silence, Original, Keep, Passthrough }

    public sealed class Target
    {
        public TargetKind Kind;
        public string Folder;          // folder to play (Folder kind) / audible stem (Layered)
        public string Area;            // base area (resume key, first-visit key)
        public List<string> Stems;     // Layered: all stem folders
        public List<string> Chain = new List<string>();
        public string Why;

        public string Id => Kind switch
        {
            TargetKind.Folder => "F:" + Folder,
            TargetKind.Layered => "L:" + string.Join("|", Stems),
            _ => Kind.ToString(),
        };

        public static Target Of(TargetKind k, string why) => new Target { Kind = k, Why = why };
    }

    /// <summary>Turns a context into what should be playing. Pure logic, no audio.</summary>
    public sealed class Resolver
    {
        private readonly MusicLibrary _lib;
        private readonly DynamicMusicConfig _cfg;

        public Resolver(MusicLibrary lib, DynamicMusicConfig cfg) { _lib = lib; _cfg = cfg; }

        public Target Resolve(MusicContext ctx)
        {
            if (!_cfg.ModEnabled) return Target.Of(TargetKind.Original, "mod disabled");
            switch (ctx.Kind)
            {
                case ContextKind.Menu:
                    return _lib.Has(AreaCatalog.MainMenu)
                        ? new Target { Kind = TargetKind.Folder, Folder = AreaCatalog.MainMenu, Area = AreaCatalog.MainMenu, Why = "main menu" }
                        : Target.Of(TargetKind.Original, "main menu (MainMenu folder empty)");
                case ContextKind.Loading: return Target.Of(TargetKind.Silence, "loading");
                case ContextKind.Cinematic: return Target.Of(TargetKind.Original, "story cinematic");
            }

            string biome = ctx.Biome ?? "";
            if (AreaCatalog.Passthrough.Contains(biome)) return Target.Of(TargetKind.Passthrough, "passthrough biome " + biome);

            List<string> chain;
            if (ctx.ZoneId != null) chain = new List<string> { ctx.ZoneId };
            else if (ctx.InLifepod) chain = AreaCatalog.ChainFor(AreaCatalog.Lifepod);
            else chain = AreaCatalog.ChainFor(biome);

            string area = chain.FirstOrDefault();
            bool isLifepod = string.Equals(area, AreaCatalog.Lifepod, StringComparison.OrdinalIgnoreCase);

            // Inside things outrank the biome: base room > base / sub / vehicle > biome chain.
            var full = new List<string>();
            if (!isLifepod)
            {
                if (ctx.Room != null) full.Add(ctx.Room);
                if (ctx.Interior != null) full.Add(ctx.Interior);
            }
            foreach (var f in chain) if (!full.Contains(f, StringComparer.OrdinalIgnoreCase)) full.Add(f);

            // Walk the chain: first folder (night variant first at night) with tracks wins.
            string play = null;
            foreach (string f in full)
            {
                var mode = _lib.Get(f)?.Settings.mode;
                if (Eq(mode, "silence")) return Done(Target.Of(TargetKind.Silence, $"[{f}] folder.json mode=silence"), full);
                if (Eq(mode, "original")) return Done(Target.Of(TargetKind.Original, $"[{f}] folder.json mode=original"), full);
                if (ctx.Night && _lib.Has(f + AreaCatalog.NightSuffix)) { play = f + AreaCatalog.NightSuffix; break; }
                if (_lib.Has(f)) { play = f; break; }
            }
            string playDay = AreaCatalog.DayName(play);
            bool fromInterior = play != null && (Eq(playDay, ctx.Interior) || Eq(playDay, ctx.Room));

            // First visit
            if (play != null && !fromInterior && area != null && _cfg.FirstVisitEnabled && !IsLayered(area))
            {
                var fv = new List<string>();
                if (ctx.Night) fv.Add(area + AreaCatalog.NightSuffix + AreaCatalog.FirstVisitSuffix);
                fv.Add(area + AreaCatalog.FirstVisitSuffix);
                string fvFolder = fv.FirstOrDefault(_lib.Has);
                if (fvFolder != null && IsFirstVisit(area, ctx))
                    return Done(new Target { Kind = TargetKind.Folder, Folder = fvFolder, Area = area, Why = "first visit" }, full);
            }

            if (play != null)
            {
                var layered = TryLayered(play, ctx.Night);
                if (layered != null) { layered.Area = area; return Done(layered, full); }
                return Done(new Target { Kind = TargetKind.Folder, Folder = play, Area = area ?? play, Why = "biome " + biome }, full);
            }

            // Lifepod with nothing in it: keep whatever was playing (1.x behaviour).
            if (isLifepod) return Done(Target.Of(TargetKind.Keep, "lifepod has no music"), full);

            string why = chain.Count == 0 ? $"no folder for biome '{biome}'" : $"no tracks in [{string.Join(" > ", full)}]";
            switch ((NoTrackMode)_cfg.NoTrack)
            {
                case NoTrackMode.KeepCurrent: return Done(Target.Of(TargetKind.Keep, why), full);
                case NoTrackMode.Silence: return Done(Target.Of(TargetKind.Silence, why), full);
                case NoTrackMode.OriginalSoundtrack: return Done(Target.Of(TargetKind.Original, why), full);
            }
            string fb = ctx.Night && _lib.Has(AreaCatalog.Fallback + AreaCatalog.NightSuffix)
                ? AreaCatalog.Fallback + AreaCatalog.NightSuffix
                : _lib.Has(AreaCatalog.Fallback) ? AreaCatalog.Fallback : null;
            if (fb != null)
                return Done(new Target { Kind = TargetKind.Folder, Folder = fb, Area = AreaCatalog.Fallback, Why = why + " → Fallback" }, full);
            return Done((NoTrackMode)_cfg.NoTrack == NoTrackMode.FallbackThenOriginal
                ? Target.Of(TargetKind.Original, why + ", Fallback empty")
                : Target.Of(TargetKind.Silence, why + ", Fallback empty"), full);
        }

        private static Target Done(Target t, List<string> chain) { t.Chain = chain; return t; }
        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        public bool IsLayered(string dayFolder) =>
            _cfg.IsBiomeLayered(dayFolder) || _lib.Get(dayFolder)?.Settings.layered == true;

        /// <summary>Stems that play in sync; moving between them is a volume crossfade, not a restart.</summary>
        private Target TryLayered(string play, bool night)
        {
            string day = AreaCatalog.DayName(play);
            var stems = new List<string>();
            string audible = play;

            string group = _lib.Get(play)?.Settings.layerGroup ?? _lib.Get(day)?.Settings.layerGroup;
            bool lavaPair = (Eq(day, "LavaPool") || Eq(day, "ActiveLavaZone")) && IsLayered("LavaPool")
                            && _lib.Has("LavaPool") && _lib.Has("ActiveLavaZone");

            if (!string.IsNullOrEmpty(group))
            {
                stems.AddRange(_lib.InLayerGroup(group).Select(f => f.Name));
            }
            else if (lavaPair)
            {
                stems.Add("ActiveLavaZone");
                stems.Add("LavaPool");
                audible = day;
            }
            else if (IsLayered(day) && _lib.Has(day + AreaCatalog.NightSuffix) && _lib.Has(day))
            {
                stems.Add(day);
                stems.Add(day + AreaCatalog.NightSuffix);
                audible = night ? day + AreaCatalog.NightSuffix : day;
            }

            if (stems.Count < 2 || !stems.Contains(audible, StringComparer.OrdinalIgnoreCase)) return null;
            stems = stems.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            return new Target { Kind = TargetKind.Layered, Folder = audible, Stems = stems, Why = "layered " + string.Join(" + ", stems) };
        }

        // ---- first visit (per save slot) ----
        private bool IsFirstVisit(string area, MusicContext ctx)
        {
            if (ctx.Day < 0 || string.IsNullOrEmpty(ctx.Slot)) return false;
            if (!_cfg.FirstVisits.TryGetValue(ctx.Slot, out var visits))
                _cfg.FirstVisits[ctx.Slot] = visits = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (!visits.TryGetValue(area, out double first))
            {
                visits[area] = ctx.Day;
                _cfg.Save();
                Plugin.Log.LogInfo($"First visit to [{area}] recorded (slot {ctx.Slot}, day {ctx.Day:F2}).");
                return true;
            }
            return ctx.Day - first < _cfg.FirstVisitDays;
        }
    }
}
