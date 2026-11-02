using System.Runtime.CompilerServices;
using FMOD.Studio;
using HarmonyLib;

namespace SN1DynamicMusic
{
    /// <summary>
    /// The game's biome music: every FMODGameParams under the scene object "music" plays one looping
    /// FMOD event while Player.GetBiomeString() starts with its onlyInBiome prefix. Ambience lives
    /// under "background" and is left alone. Story stingers (intro, fire, rocket, credits, death)
    /// are separate emitters and are never touched.
    /// </summary>
    [HarmonyPatch(typeof(FMODGameParams), "UpdateParams")]
    internal static class FMODGameParams_UpdateParams_Patch
    {
        private static readonly ConditionalWeakTable<FMODGameParams, object> MusicPlayers = new ConditionalWeakTable<FMODGameParams, object>();
        private static readonly ConditionalWeakTable<FMODGameParams, object> NotMusic = new ConditionalWeakTable<FMODGameParams, object>();

        internal static bool IsMusic(FMODGameParams p)
        {
            if (MusicPlayers.TryGetValue(p, out _)) return true;
            if (NotMusic.TryGetValue(p, out _)) return false;
            var parent = p.transform.parent;
            bool music = parent != null && parent.name == "music";
            if (music) MusicPlayers.Add(p, null); else NotMusic.Add(p, null);
            return music;
        }

        static bool Prefix(FMODGameParams __instance)
        {
            var d = Plugin.Instance?.Director;
            if (d == null || d.AllowOriginalMusic) return true;
            if (!IsMusic(__instance)) return true;
            __instance.isPlaying = false;
            var e = __instance.loopingEmitter;
            if (e != null && e.playing) e.Stop();
            return false;
        }
    }

    /// <summary>Main menu theme: replaced only when the MainMenu folder has tracks. The menu's background bed keeps playing.</summary>
    [HarmonyPatch(typeof(MainMenuMusic), nameof(MainMenuMusic.Play))]
    internal static class MainMenuMusic_Play_Patch
    {
        static bool Prefix()
        {
            if (!MenuMusic.ShouldReplace) return true;
            var m = MainMenuMusic.main;
            if (m == null) return false;
            var bg = Traverse.Create(m).Field<EventInstance>("evt2").Value;
            if (bg.hasHandle()) bg.start();
            return false;
        }
    }

    internal static class MenuMusic
    {
        public static bool ShouldReplace =>
            Plugin.Cfg != null && Plugin.Cfg.ModEnabled && Plugin.Instance?.Library?.Has(AreaCatalog.MainMenu) == true;

        /// <summary>Called after options/rescans change whether the menu theme should be replaced.</summary>
        public static void Sync()
        {
            var m = MainMenuMusic.main;
            if (m == null || uGUI_MainMenu.main == null) return;
            var evt = Traverse.Create(m).Field<EventInstance>("evt").Value;
            if (!evt.hasHandle()) return;
            evt.getPlaybackState(out PLAYBACK_STATE st);
            bool playing = st == PLAYBACK_STATE.PLAYING || st == PLAYBACK_STATE.STARTING;
            if (ShouldReplace && playing) evt.stop(STOP_MODE.ALLOWFADEOUT);
            else if (!ShouldReplace && !playing) evt.start();
        }
    }
}
