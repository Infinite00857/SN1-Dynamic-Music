using System;
using System.Collections.Generic;
using Nautilus.Json;

namespace SN1DynamicMusic
{
    public enum NoTrackMode
    {
        FallbackThenOriginal = 0,
        FallbackThenSilence = 1,
        KeepCurrent = 2,
        Silence = 3,
        OriginalSoundtrack = 4,
    }

    /// <summary>
    /// Saved as config.json next to the DLL. Property names shared with 1.x keep their meaning,
    /// so dropping 2.0 over an old install keeps volume, toggles and per-biome layered choices.
    /// </summary>
    public class DynamicMusicConfig : ConfigFile
    {
        // ---- carried over from 1.x ----
        public bool ModEnabled { get; set; } = true;
        public bool LayeredDayNight { get; set; } = false;
        public float MusicVolume { get; set; } = 0.13f;
        public bool SilenceBetweenTracks { get; set; } = false;
        public bool ResumeOnReEntry { get; set; } = true;
        public bool FirstVisitEnabled { get; set; } = true;
        public string BiomeLayeredRaw { get; set; } = "";
        /// <summary>1.x setting (0 fallback, 1 keep, 2 silence). Read once to seed NoTrack.</summary>
        public int NoTrackBehavior { get; set; } = -1;

        // ---- new in 2.0 ----
        public int ConfigVersion { get; set; } = 0;
        public int NoTrack { get; set; } = (int)NoTrackMode.FallbackThenOriginal;
        public float CrossfadeSeconds { get; set; } = 2f;
        public float BiomeChangeDelay { get; set; } = 1.5f;
        public float SilenceMinSeconds { get; set; } = 30f;
        public float SilenceMaxSeconds { get; set; } = 600f;
        public bool SilenceSurvivesBiomeChange { get; set; } = true;
        public float FirstVisitDays { get; set; } = 2f;
        public bool KeepSameTrackAcrossAreas { get; set; } = true;
        public bool Mp3GaplessTrim { get; set; } = true;
        public bool YieldToStoryMusic { get; set; } = true;
        public bool DuckDuringDialogue { get; set; } = true;
        public int FolderPreset { get; set; } = 0;
        public bool ShowDebugHud { get; set; } = false;
        /// <summary>save slot -> (area folder -> in-game day of first visit)</summary>
        public Dictionary<string, Dictionary<string, double>> FirstVisits { get; set; } =
            new Dictionary<string, Dictionary<string, double>>();

        internal void Migrate()
        {
            if (ConfigVersion >= 2) return;
            if (NoTrackBehavior >= 0)
            {
                // 1.x: 0 = Fallback folder (else silence), 1 = keep playing, 2 = silence
                NoTrack = NoTrackBehavior switch
                {
                    0 => (int)NoTrackMode.FallbackThenSilence,
                    1 => (int)NoTrackMode.KeepCurrent,
                    _ => (int)NoTrackMode.Silence,
                };
                NoTrackBehavior = -1;
            }
            ConfigVersion = 2;
            Save();
        }

        // ---- per-biome layered choices (kept in the 1.x string format) ----
        private Dictionary<string, bool> _layered;

        public bool IsBiomeLayered(string folder)
        {
            if (LayeredDayNight) return true;
            if (string.IsNullOrEmpty(folder)) return false;
            EnsureLayered();
            return _layered.TryGetValue(folder, out bool v) && v;
        }

        public bool GetBiomeLayeredSetting(string folder)
        {
            EnsureLayered();
            return _layered.TryGetValue(folder, out bool v) && v;
        }

        public void SetBiomeLayered(string folder, bool value)
        {
            EnsureLayered();
            _layered[folder] = value;
            var parts = new List<string>();
            foreach (var kv in _layered) if (kv.Value) parts.Add(kv.Key + "=true");
            BiomeLayeredRaw = string.Join(",", parts);
            Save();
        }

        private void EnsureLayered()
        {
            if (_layered != null) return;
            _layered = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(BiomeLayeredRaw)) return;
            foreach (var part in BiomeLayeredRaw.Split(','))
            {
                var kv = part.Trim().Split('=');
                if (kv.Length == 2 && kv[0].Length > 0)
                    _layered[kv[0]] = kv[1].Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
