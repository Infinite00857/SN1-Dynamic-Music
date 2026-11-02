using FMOD.Studio;
using FMODUnity;

namespace SN1DynamicMusic
{
    /// <summary>
    /// Voice lines (PDA, AI, Cyclops) trigger the FMOD snapshot "snapshot:/duck when voice",
    /// authored in the game's sound banks, which lowers music while someone talks.
    /// With ducking turned off we stop that snapshot whenever it starts.
    /// </summary>
    internal static class DialogueDucking
    {
        private const string SnapshotPath = "snapshot:/duck when voice";
        private static EventDescription _desc;
        private static bool _looked;
        private static float _nextLookup;
        private static int _stopped;

        public static void Update()
        {
            if (Plugin.Cfg.DuckDuringDialogue) return;
            if (!_desc.isValid())
            {
                if (_looked && UnityEngine.Time.unscaledTime < _nextLookup) return;
                _looked = true;
                _nextLookup = UnityEngine.Time.unscaledTime + 5f;
                if (RuntimeManager.StudioSystem.getEvent(SnapshotPath, out _desc) != FMOD.RESULT.OK || !_desc.isValid())
                    return;
                Plugin.Log.LogInfo("Found voice-ducking snapshot; it will be suppressed.");
            }
            if (_desc.getInstanceCount(out int n) != FMOD.RESULT.OK || n == 0) return;
            if (_desc.getInstanceList(out EventInstance[] list) != FMOD.RESULT.OK) return;
            foreach (var inst in list)
            {
                inst.getPlaybackState(out PLAYBACK_STATE st);
                if (st == PLAYBACK_STATE.STOPPED) continue;
                inst.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
                if (_stopped++ < 3) Plugin.Log.LogInfo("Suppressed dialogue ducking.");
            }
        }
    }
}
