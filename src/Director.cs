using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SN1DynamicMusic
{
    /// <summary>
    /// Decides and drives what plays. Runs every frame from Plugin.Update.
    /// Programs: a shuffled playlist, an intro+loop pair, or a set of synced layered stems.
    /// All fades and joins are scheduled on FMOD's DSP clock, so they don't depend on frame rate.
    /// </summary>
    public sealed class Director
    {
        private readonly MusicLibrary _lib;
        private readonly DynamicMusicConfig _cfg;
        private readonly Resolver _resolver;
        private readonly System.Random _rng = new System.Random();

        private Program _program;
        private Target _active;            // what we are currently doing
        private Target _pending;           // what the world wants (debounced)
        private float _pendingSince;
        private float _nextResolve;
        private float _gapUntil;
        private readonly List<Voice> _dying = new List<Voice>();
        private readonly ResumeCache _resume = new ResumeCache(6);
        private bool _skipRequested;
        private MusicContext _ctx;
        private int _libVersion;
        private bool _forceImmediate;

        public Director(MusicLibrary lib, DynamicMusicConfig cfg)
        {
            _lib = lib;
            _cfg = cfg;
            _resolver = new Resolver(lib, cfg);
        }

        /// <summary>True when the game's own biome music may play.</summary>
        public bool AllowOriginalMusic =>
            !_cfg.ModEnabled || _active == null && _ctx.Kind == ContextKind.Menu || _active?.Kind == TargetKind.Original;

        public string ActiveFolder => _program?.Folder;
        public string ActiveTrack => _program?.CurrentVoice?.Name;
        public string ActiveTrackPath => _program?.CurrentVoice?.Path;

        public void Skip() => _skipRequested = true;

        /// <summary>Re-evaluate now and switch without the biome-change delay (settings changed, rescan...).</summary>
        public void ForceRefresh()
        {
            _nextResolve = 0;
            _pending = null;
            _forceImmediate = true;
        }

        public void OnLibraryRescanned(bool clearResume)
        {
            if (clearResume) _resume.Clear();
            // Folder switched between playlist and intro+loop (e.g. the loop maker just ran): restart it.
            var info = _program != null ? _lib.Get(_program.Folder) : null;
            if (info != null && (_program is PlaylistProgram && info.IsIntroLoop || _program is IntroLoopProgram && !info.IsIntroLoop))
                _active = null;
            ForceRefresh();
        }

        public void ApplyGains()
        {
            if (_program == null) return;
            foreach (var v in _program.Voices)
            {
                v.Gain = _lib.Get(v.Folder)?.Settings.volume ?? 1f;
                v.ApplyGain();
            }
        }

        // ------------------------------------------------------------------
        public void Update()
        {
            if (!Mixer.TryInit()) return;
            float now = Time.unscaledTime;

            if (_lib.Version != _libVersion) { _libVersion = _lib.Version; ForceRefresh(); }

            if (now >= _nextResolve)
            {
                _nextResolve = now + 0.2f;
                _ctx = MusicContext.Sense();
                var want = _resolver.Resolve(_ctx);
                Consider(want, now);
            }

            _program?.Tick(this);
            ReapDying();
        }

        private void Consider(Target want, float now)
        {
            if (want.Kind == TargetKind.Passthrough) { _pending = null; return; }
            if (want.Kind == TargetKind.Keep)
            {
                _pending = null;
                if (_active == null || _active.Kind == TargetKind.Keep) _active = want;
                return;
            }

            if (_active != null && want.Id == _active.Id)
            {
                _pending = null;
                _forceImmediate = false;
                // Same layered set: only the audible stem may have changed (day/night, lava pool...).
                if (want.Kind == TargetKind.Layered && _program is LayeredProgram lp && !Eq(lp.Audible, want.Folder))
                    lp.SetAudible(want.Folder, Crossfade(want.Folder));
                _active = want;
                return;
            }

            if (_pending == null || _pending.Id != want.Id) { _pending = want; _pendingSince = now; }
            _pending = want; // refresh details (chain, why)

            // Debounce only matters while something is audible; borders between biomes flicker.
            bool nothingPlaying = _program == null && (_active == null || _active.Kind != TargetKind.Original);
            bool immediate = _forceImmediate || nothingPlaying || _ctx.Kind != ContextKind.Game || !_cfg.ModEnabled;
            if (!immediate && now - _pendingSince < _cfg.BiomeChangeDelay) return;
            _forceImmediate = false;

            Switch(_pending);
            _pending = null;
        }

        private void Switch(Target t)
        {
            Plugin.Log.LogInfo($"Music → {Describe(t)}  ({t.Why})");
            var old = _program;
            _active = t;
            _skipRequested = false;
            if (!_cfg.SilenceSurvivesBiomeChange) _gapUntil = 0;

            switch (t.Kind)
            {
                case TargetKind.Silence:
                case TargetKind.Original:
                    StopProgram(old, Crossfade(null));
                    _program = null;
                    return;

                case TargetKind.Layered:
                    StopProgram(old, Crossfade(t.Folder));
                    _program = new LayeredProgram(this, t);
                    return;

                case TargetKind.Folder:
                    var info = _lib.Get(t.Folder);
                    if (info == null || !info.HasTracks) { StopProgram(old, Crossfade(null)); _program = null; return; }

                    // Same file name in the new folder? Keep it playing, just adopt the new playlist.
                    if (_cfg.KeepSameTrackAcrossAreas && old is PlaylistProgram op && op.CurrentVoice != null &&
                        op.CurrentVoice.IsPlaying && !info.IsIntroLoop)
                    {
                        string same = info.Tracks.FirstOrDefault(p => Eq(Path.GetFileName(p), op.CurrentVoice.Name));
                        if (same != null)
                        {
                            SaveResume(old, except: op.CurrentVoice);
                            _program = PlaylistProgram.Adopt(this, t.Folder, info, op.TakeVoice(), same);
                            Plugin.Log.LogInfo("Same track in new area — continuing " + Path.GetFileName(same));
                            return;
                        }
                    }

                    SaveResume(old);
                    StopProgram(old, Crossfade(t.Folder));
                    bool fadeIn = true; // new area: always fade in (from silence or over the old music)
                    _program = info.IsIntroLoop
                        ? (Program)new IntroLoopProgram(this, t.Folder, info, _resume.Take(t.Folder), fadeIn)
                        : new PlaylistProgram(this, t.Folder, info, _resume.Take(t.Folder), fadeIn);
                    return;
            }
        }

        private void StopProgram(Program p, float fade)
        {
            if (p == null) return;
            foreach (var v in p.Voices)
            {
                if (v.Started) v.FadeTo(0f, fade, stopAfter: true);
                _dying.Add(v);
            }
            p.Abandon();
        }

        private void SaveResume(Program p, Voice except = null)
        {
            if (!_cfg.ResumeOnReEntry || p == null) return;
            var r = p.ResumePoint();
            if (r != null && r.Voice != except) _resume.Put(p.Folder, r);
        }

        private void ReapDying()
        {
            for (int i = _dying.Count - 1; i >= 0; i--)
            {
                var v = _dying[i];
                if (v.Failed || !v.Started || !v.IsPlaying) { v.Release(); _dying.RemoveAt(i); }
            }
        }

        public void StopAll()
        {
            StopProgram(_program, 0.3f);
            _program = null;
            _active = null;
        }

        // ---- helpers used by programs ----
        internal float Crossfade(string folder)
        {
            float f = folder != null ? _lib.Get(folder)?.Settings.crossfade ?? -1f : -1f;
            return f >= 0 ? f : _cfg.CrossfadeSeconds;
        }

        internal float Gain(string folder) => _lib.Get(folder)?.Settings.volume ?? 1f;
        internal DynamicMusicConfig Cfg => _cfg;
        internal MusicLibrary Lib => _lib;
        internal System.Random Rng => _rng;

        internal bool InSilenceGap => Time.unscaledTime < _gapUntil;
        internal float GapRemaining => Mathf.Max(0, _gapUntil - Time.unscaledTime);

        internal void BeginGap()
        {
            if (!_cfg.SilenceBetweenTracks) return;
            float min = Mathf.Max(0, Mathf.Min(_cfg.SilenceMinSeconds, _cfg.SilenceMaxSeconds));
            float max = Mathf.Max(_cfg.SilenceMinSeconds, _cfg.SilenceMaxSeconds);
            float len = UnityEngine.Random.Range(min, max);
            _gapUntil = Time.unscaledTime + len;
            Plugin.Log.LogInfo($"Silence gap: {len:F0}s");
        }

        internal bool GapBlocksNewProgram => InSilenceGap && _cfg.SilenceSurvivesBiomeChange;

        internal bool ConsumeSkip()
        {
            bool s = _skipRequested;
            _skipRequested = false;
            if (s) _gapUntil = 0;
            return s;
        }

        internal void Retire(Voice v, float fade)
        {
            if (v == null) return;
            if (v.Started) v.FadeTo(0f, fade, stopAfter: true);
            _dying.Add(v);
        }

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string Describe(Target t) => t.Kind switch
        {
            TargetKind.Folder => "[" + t.Folder + "]",
            TargetKind.Layered => "layered [" + string.Join(" + ", t.Stems) + "] audible " + t.Folder,
            TargetKind.Original => "original soundtrack",
            _ => t.Kind.ToString().ToLowerInvariant(),
        };

        // ------------------------------------------------------------------
        //  Debug HUD
        // ------------------------------------------------------------------
        private GUIStyle _hudStyle;

        public void DrawHud()
        {
            if (!_cfg.ShowDebugHud) return;
            if (_hudStyle == null)
            {
                _hudStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, richText = true, wordWrap = true };
                _hudStyle.normal.textColor = Color.white;
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<b>Dynamic Music 2.0</b>");
            sb.AppendLine($"context: {_ctx.Kind}   biome: <b>{_ctx.Biome}</b>");
            if (_ctx.Kind == ContextKind.Game)
                sb.AppendLine($"{(_ctx.Night ? "night" : "day")} ({_ctx.DayScalar:F3})  day {_ctx.Day:F2}  interior: {_ctx.Interior ?? "-"}{(_ctx.Room != null ? " / " + _ctx.Room : "")}{(_ctx.InLifepod ? "  lifepod" : "")}{(_ctx.ZoneId != null ? "  zone: " + _ctx.ZoneId : "")}");
            if (_active != null)
            {
                sb.AppendLine($"target: <b>{Describe(_active)}</b>");
                sb.AppendLine($"why: {_active.Why}");
                if (_active.Chain != null && _active.Chain.Count > 0)
                    sb.AppendLine("chain: " + string.Join(" > ", _active.Chain.Select(f =>
                        f + "(" + (_lib.Get(f)?.Tracks.Count ?? 0) + (_lib.Has(f + AreaCatalog.NightSuffix) ? "+n" : "") + ")")));
            }
            if (_pending != null) sb.AppendLine($"pending: {Describe(_pending)} in {Mathf.Max(0, _cfg.BiomeChangeDelay - (Time.unscaledTime - _pendingSince)):F1}s");
            if (_program != null) sb.AppendLine(_program.Status());
            if (InSilenceGap) sb.AppendLine($"silence gap: {GapRemaining:F0}s left");
            sb.AppendLine($"original music allowed: {AllowOriginalMusic}   fading voices: {_dying.Count}");
            GUI.Box(new Rect(10, 10, 560, 190), sb.ToString(), _hudStyle);
        }

        public string StatusLine()
        {
            return $"biome={_ctx.Biome} night={_ctx.Night} interior={_ctx.Interior} room={_ctx.Room} target={(_active != null ? Describe(_active) : "-")} " +
                   $"why={_active?.Why} track={ActiveTrack ?? "-"}";
        }
    }

    // ======================================================================
    //  Resume cache
    // ======================================================================
    public sealed class ResumePoint
    {
        public string Path;
        public uint PositionMs;
        public bool InLoop;   // intro+loop: true once the loop part was playing
        public Voice Voice;   // not persisted, only used to avoid saving a voice we keep
    }

    public sealed class ResumeCache
    {
        private readonly int _cap;
        private readonly List<KeyValuePair<string, ResumePoint>> _items = new List<KeyValuePair<string, ResumePoint>>();
        public ResumeCache(int cap) { _cap = cap; }

        public void Put(string folder, ResumePoint p)
        {
            _items.RemoveAll(kv => string.Equals(kv.Key, folder, StringComparison.OrdinalIgnoreCase));
            _items.Add(new KeyValuePair<string, ResumePoint>(folder, p));
            while (_items.Count > _cap) _items.RemoveAt(0);
        }

        public ResumePoint Take(string folder)
        {
            int i = _items.FindIndex(kv => string.Equals(kv.Key, folder, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return null;
            var p = _items[i].Value;
            _items.RemoveAt(i);
            return File.Exists(p.Path) ? p : null;
        }

        public void Clear() => _items.Clear();
    }

    // ======================================================================
    //  Programs
    // ======================================================================
    public abstract class Program
    {
        protected readonly Director D;
        public string Folder { get; protected set; }
        protected bool Abandoned;

        protected Program(Director d, string folder) { D = d; Folder = folder; }

        public abstract IEnumerable<Voice> Voices { get; }
        public abstract Voice CurrentVoice { get; }
        public abstract void Tick(Director d);
        public abstract string Status();
        public virtual ResumePoint ResumePoint() => null;
        public bool IsAudible => Voices.Any(v => v.Started && v.IsPlaying);
        public void Abandon() => Abandoned = true;

        protected static string Fmt(uint ms) => $"{ms / 60000}:{ms / 1000 % 60:00}";
    }

    /// <summary>Shuffled (or ordered) playlist of a folder.</summary>
    public sealed class PlaylistProgram : Program
    {
        private readonly List<string> _order;
        private int _index;
        private Voice _voice;
        private bool _starting;
        private uint _startAtMs;
        private float _fadeIn;
        private string _lastPlayed;

        public PlaylistProgram(Director d, string folder, FolderInfo info, ResumePoint resume, bool fadeIn) : base(d, folder)
        {
            _order = MakeOrder(info, null);
            _fadeIn = fadeIn || resume != null ? d.Crossfade(folder) : 0f;
            if (resume != null)
            {
                int i = _order.FindIndex(p => string.Equals(p, resume.Path, StringComparison.OrdinalIgnoreCase));
                if (i >= 0)
                {
                    _index = i;
                    _startAtMs = resume.PositionMs;
                    Plugin.Log.LogInfo($"Resuming {Path.GetFileName(resume.Path)} at {Fmt(resume.PositionMs)}");
                }
            }
        }

        private PlaylistProgram(Director d, string folder, List<string> order, int index, Voice voice) : base(d, folder)
        {
            _order = order;
            _index = index;
            _voice = voice;
            _lastPlayed = voice?.Path;
        }

        public static PlaylistProgram Adopt(Director d, string folder, FolderInfo info, Voice voice, string samePath)
        {
            var order = MakeOrder(info, null, d.Rng);
            order.Remove(samePath);
            order.Insert(0, samePath);
            if (voice != null) { voice.Gain = d.Gain(folder); voice.ApplyGain(); }
            return new PlaylistProgram(d, folder, order, 0, voice);
        }

        public Voice TakeVoice()
        {
            var v = _voice;
            _voice = null;
            return v;
        }

        private List<string> MakeOrder(FolderInfo info, string avoidFirst) => MakeOrder(info, avoidFirst, D.Rng);

        private static List<string> MakeOrder(FolderInfo info, string avoidFirst, System.Random rng = null)
        {
            var list = new List<string>(info.Tracks);
            if (info.Settings.shuffle)
            {
                rng ??= new System.Random();
                for (int i = list.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (list[i], list[j]) = (list[j], list[i]);
                }
                if (avoidFirst != null && list.Count > 1 && string.Equals(list[0], avoidFirst, StringComparison.OrdinalIgnoreCase))
                    (list[0], list[list.Count - 1]) = (list[list.Count - 1], list[0]);
            }
            return list;
        }

        public override IEnumerable<Voice> Voices { get { if (_voice != null) yield return _voice; } }
        public override Voice CurrentVoice => _voice;

        public override void Tick(Director d)
        {
            if (Abandoned) return;

            if (d.ConsumeSkip() && _voice != null)
            {
                d.Retire(_voice, 0.5f);
                _voice = null;
                Advance();
                _fadeIn = 0.5f;
            }

            if (_voice == null)
            {
                if (d.InSilenceGap || _order.Count == 0) return;
                var info = d.Lib.Get(Folder);
                if (info == null || !info.HasTracks) return;
                string path = _order[_index % _order.Count];
                if (!File.Exists(path)) { Advance(); return; }
                _voice = new Voice(path, Folder, looping: false) { Gain = d.Gain(Folder) };
                _starting = true;
            }

            if (_starting)
            {
                if (!_voice.PollReady()) return;
                _starting = false;
                if (_voice.Failed || !_voice.Start(_startAtMs, 0, _fadeIn))
                {
                    _voice.Release();
                    _voice = null;
                    Advance();
                    return;
                }
                Plugin.Log.LogInfo($"Now playing [{Folder}] {_voice.Name}" + (_startAtMs > 0 ? " from " + Fmt(_startAtMs) : ""));
                _lastPlayed = _voice.Path;
                _startAtMs = 0;
                _fadeIn = 0f;
                return;
            }

            if (_voice != null && !_voice.IsPlaying)
            {
                _voice.Release();
                _voice = null;
                Advance();
                d.BeginGap();
            }
        }

        private void Advance()
        {
            _index++;
            if (_index >= _order.Count)
            {
                _index = 0;
                var info = D.Lib.Get(Folder);
                if (info != null && info.HasTracks)
                {
                    var fresh = MakeOrder(info, _lastPlayed);
                    _order.Clear();
                    _order.AddRange(fresh);
                }
            }
        }

        public override ResumePoint ResumePoint() =>
            _voice != null && _voice.IsPlaying
                ? new ResumePoint { Path = _voice.Path, PositionMs = _voice.PositionMs, Voice = _voice }
                : null;

        public override string Status()
        {
            if (_voice == null) return $"playlist [{Folder}] idle ({_order.Count} tracks)";
            return $"playlist [{Folder}] {_index % Math.Max(1, _order.Count) + 1}/{_order.Count}: <b>{_voice.Name}</b> {Fmt(_voice.PositionMs)}";
        }
    }

    /// <summary>name-intro + name-loop: intro once, loop forever, joined sample-accurately.</summary>
    public sealed class IntroLoopProgram : Program
    {
        private Voice _intro, _loop;
        private bool _scheduled;
        private ulong _loopClock;
        private readonly ResumePoint _resume;
        private readonly float _fadeIn;
        private readonly FolderInfo _info;

        public IntroLoopProgram(Director d, string folder, FolderInfo info, ResumePoint resume, bool fadeIn) : base(d, folder)
        {
            _info = info;
            _resume = resume;
            _fadeIn = fadeIn || resume != null ? d.Crossfade(folder) : 0f;
        }

        public override IEnumerable<Voice> Voices
        {
            get { if (_intro != null) yield return _intro; if (_loop != null) yield return _loop; }
        }

        public override Voice CurrentVoice => InLoop ? _loop : _intro ?? _loop;

        private bool InLoop => _loop != null && _scheduled && Mixer.Now() >= _loopClock;

        public override void Tick(Director d)
        {
            if (Abandoned) return;
            if (!_scheduled)
            {
                if (d.GapBlocksNewProgram) return;
                bool resumeLoop = _resume != null && _resume.InLoop;
                if (_loop == null)
                {
                    _loop = new Voice(_info.Loop, Folder, looping: true) { Gain = d.Gain(Folder) };
                    if (!resumeLoop) _intro = new Voice(_info.Intro, Folder, looping: false) { Gain = d.Gain(Folder) };
                }
                if (!_loop.PollReady() || (_intro != null && !_intro.PollReady())) return;
                _scheduled = true;

                if (_info.Settings.loopEndMs > _info.Settings.loopStartMs)
                {
                    _loop.LoopStartPcm = (uint)(_info.Settings.loopStartMs / 1000.0 * _loop.Frequency);
                    _loop.LoopEndPcm = (uint)(_info.Settings.loopEndMs / 1000.0 * _loop.Frequency);
                }

                ulong t0 = Mixer.Now() + Mixer.Samples(0.05f);
                if (_intro != null && !_intro.Failed)
                {
                    uint startMs = _resume != null ? _resume.PositionMs : 0;
                    uint startPcm = Math.Max(_intro.TrimStart, (uint)(startMs / 1000.0 * _intro.Frequency));
                    _intro.Start(startMs, t0, _fadeIn);
                    _loopClock = t0 + _intro.PcmToClock(_intro.RemainingPcm(startPcm));
                    _intro.StopAt(_loopClock);
                    _loop.Start(0, _loopClock, 0f);
                    Plugin.Log.LogInfo($"Intro+loop [{Folder}]: {_intro.Name} then {_loop.Name} (joined on DSP clock)");
                }
                else
                {
                    _loopClock = t0;
                    _loop.Start(resumeLoop ? _resume.PositionMs : 0, t0, _fadeIn);
                    Plugin.Log.LogInfo($"Loop [{Folder}]: {_loop.Name}" + (resumeLoop ? " (resumed)" : ""));
                }
                return;
            }

            if (_intro != null && InLoop && !_intro.IsPlaying) { _intro.Release(); _intro = null; }
        }

        public override ResumePoint ResumePoint()
        {
            if (!_scheduled) return null;
            if (InLoop && _loop.IsPlaying)
                return new ResumePoint { Path = _loop.Path, PositionMs = _loop.PositionMs, InLoop = true, Voice = _loop };
            if (_intro != null && _intro.IsPlaying)
                return new ResumePoint { Path = _intro.Path, PositionMs = _intro.PositionMs, InLoop = false, Voice = _intro };
            return null;
        }

        public override string Status()
        {
            var v = CurrentVoice;
            return v == null ? $"intro+loop [{Folder}] loading" :
                $"intro+loop [{Folder}] {(InLoop ? "loop" : "intro")}: <b>{v.Name}</b> {Fmt(v.PositionMs)}";
        }
    }

    /// <summary>Synced stems; only the audible one is up, the rest run silently in step.</summary>
    public sealed class LayeredProgram : Program
    {
        private readonly Dictionary<string, Voice> _stems = new Dictionary<string, Voice>(StringComparer.OrdinalIgnoreCase);
        private bool _scheduled;
        private float _fadeIn;
        public string Audible { get; private set; }

        public LayeredProgram(Director d, Target t) : base(d, t.Folder)
        {
            Audible = t.Folder;
            _fadeIn = d.Crossfade(t.Folder);
            foreach (string s in t.Stems)
            {
                string file = d.Lib.Get(s)?.FirstTrackByName;
                if (file != null) _stems[s] = new Voice(file, s, looping: true) { Gain = d.Gain(s) };
            }
        }

        public override IEnumerable<Voice> Voices => _stems.Values;
        public override Voice CurrentVoice => _stems.TryGetValue(Audible, out var v) ? v : null;

        public override void Tick(Director d)
        {
            if (Abandoned || _scheduled) return;
            if (_stems.Values.Any(v => !v.PollReady())) return;
            _scheduled = true;
            ulong t0 = Mixer.Now() + Mixer.Samples(0.05f);
            foreach (var kv in _stems)
                kv.Value.Start(0, t0, _fadeIn, string.Equals(kv.Key, Audible, StringComparison.OrdinalIgnoreCase) ? 1f : 0f);
            Plugin.Log.LogInfo("Layered stems started in sync: " + string.Join(", ", _stems.Select(kv => kv.Key + "=" + kv.Value.Name)));
        }

        public void SetAudible(string folder, float fade)
        {
            if (string.Equals(folder, Audible, StringComparison.OrdinalIgnoreCase)) return;
            Plugin.Log.LogInfo($"Layered: {Audible} → {folder}");
            Audible = folder;
            Folder = folder;
            if (!_scheduled) return;
            foreach (var kv in _stems)
                kv.Value.FadeTo(string.Equals(kv.Key, folder, StringComparison.OrdinalIgnoreCase) ? 1f : 0f, fade);
        }

        public override string Status()
        {
            var v = CurrentVoice;
            return $"layered ({_stems.Count} stems) audible [{Audible}]" + (v != null ? $": <b>{v.Name}</b> {Fmt(v.PositionMs)}" : "");
        }
    }
}
