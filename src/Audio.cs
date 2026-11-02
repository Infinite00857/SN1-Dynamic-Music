using System;
using System.IO;
using FMOD;
using FMODUnity;
using Nautilus.Utility;

namespace SN1DynamicMusic
{
    /// <summary>
    /// Owns the mod's FMOD channel group. It sits under the game's music bus, so the in-game
    /// Music volume slider still applies; the mod's own volume is the group volume.
    /// </summary>
    public static class Mixer
    {
        private static ChannelGroup _group;
        private static bool _ready;
        public static int OutputRate { get; private set; } = 48000;

        public static FMOD.System Core => RuntimeManager.CoreSystem;

        public static bool TryInit()
        {
            if (_ready) return true;
            try
            {
                var bus = RuntimeManager.GetBus(AudioUtils.BusPaths.Music);
                if (bus.getChannelGroup(out var busGroup) != RESULT.OK || !busGroup.hasHandle())
                {
                    bus.lockChannelGroup();
                    return false; // the lock takes effect on the next studio update
                }
                if (Core.createChannelGroup("DynamicMusic", out _group) != RESULT.OK) return false;
                busGroup.addGroup(_group, true, out _);
                Core.getSoftwareFormat(out int rate, out _, out _);
                if (rate > 0) OutputRate = rate;
                _ready = true;
                ApplyVolume();
                Plugin.Log.LogInfo($"Mixer ready (output {OutputRate} Hz).");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Mixer init failed, will retry: " + e.Message);
                return false;
            }
        }

        public static ChannelGroup Group => _group;
        public static bool Ready => _ready;

        public static void ApplyVolume()
        {
            if (_ready) _group.setVolume(Plugin.Cfg.MusicVolume);
        }

        /// <summary>Current DSP clock of the mod's group (fade points and delays use this clock).</summary>
        public static ulong Now()
        {
            _group.getDSPClock(out ulong clock, out _);
            return clock;
        }

        public static ulong Samples(float seconds) => (ulong)Math.Max(0, seconds * OutputRate);
    }

    /// <summary>One playing (or loading) audio file.</summary>
    public sealed class Voice
    {
        public string Path { get; }
        public string Folder { get; }
        public bool Looping { get; }
        public float Gain { get; set; } = 1f;

        private Sound _sound;
        private Channel _channel;
        private bool _started;
        private bool _released;
        private bool _failed;

        // Envelope as last scheduled, so a new fade can start from the real current level.
        private ulong _envT0, _envT1;
        private float _envV0, _envV1;

        public uint LengthPcm { get; private set; }
        public float Frequency { get; private set; } = 44100f;
        /// <summary>PCM samples to skip at the start / drop at the end (MP3 encoder padding).</summary>
        public uint TrimStart { get; private set; }
        public uint TrimEnd { get; private set; }
        public uint LoopStartPcm { get; set; }
        public uint LoopEndPcm { get; set; }

        private readonly Mp3Gapless.Info _mp3;

        public Voice(string path, string folder, bool looping)
        {
            Path = path;
            Folder = folder;
            Looping = looping;
            if (Plugin.Cfg.Mp3GaplessTrim && path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                _mp3 = Mp3Gapless.Read(path);

            MODE mode = MODE._2D | MODE.CREATESTREAM | MODE.ACCURATETIME | MODE.NONBLOCKING |
                        (looping ? MODE.LOOP_NORMAL : MODE.LOOP_OFF);
            if (Mixer.Core.createSound(path, mode, out _sound) != RESULT.OK)
            {
                _failed = true;
                Plugin.Log.LogError("Could not open " + System.IO.Path.GetFileName(path));
            }
        }

        public string Name => System.IO.Path.GetFileName(Path);
        public bool Failed => _failed;
        public bool Started => _started;

        /// <summary>True once the file is open and ready to schedule (or has failed).</summary>
        public bool PollReady()
        {
            if (_failed || _released) return true;
            if (_sound.getOpenState(out OPENSTATE st, out _, out _, out _) != RESULT.OK) { _failed = true; return true; }
            if (st == OPENSTATE.ERROR) { _failed = true; Plugin.Log.LogError("Could not decode " + Name); return true; }
            if (st != OPENSTATE.READY) return false;

            if (LengthPcm == 0)
            {
                _sound.getLength(out uint len, TIMEUNIT.PCM);
                _sound.getDefaults(out float freq, out _);
                LengthPcm = len;
                if (freq > 0) Frequency = freq;
                if (_mp3 != null) ApplyMp3Trim();
                LoopStartPcm = TrimStart;
                LoopEndPcm = LengthPcm > TrimEnd + 1 ? LengthPcm - TrimEnd - 1 : LengthPcm;
            }
            return true;
        }

        private void ApplyMp3Trim()
        {
            if (Mp3Gapless.ComputeTrim(_mp3, LengthPcm, Name, out long start, out long end))
            {
                TrimStart = (uint)start;
                TrimEnd = (uint)end;
            }
        }

        /// <summary>Playable PCM samples from position to the (trimmed) end.</summary>
        public uint RemainingPcm(uint fromPcm)
        {
            uint end = LengthPcm > TrimEnd ? LengthPcm - TrimEnd : LengthPcm;
            return fromPcm < end ? end - fromPcm : 0;
        }

        public ulong PcmToClock(uint pcm) => (ulong)(pcm * (double)Mixer.OutputRate / Frequency);

        /// <summary>Start paused, seek, schedule the start on the DSP clock, then unpause. No pops.</summary>
        public bool Start(uint positionMs, ulong startClock, float fadeInSeconds, float targetLevel = 1f)
        {
            if (_failed || _released || !PollReady()) return false;
            if (Mixer.Core.playSound(_sound, Mixer.Group, true, out _channel) != RESULT.OK)
            {
                _failed = true;
                Plugin.Log.LogError("playSound failed for " + Name);
                return false;
            }
            _channel.setVolume(Gain);
            _channel.setVolumeRamp(true);
            if (Looping)
            {
                _channel.setLoopCount(-1);
                if (LoopEndPcm > LoopStartPcm)
                    _channel.setLoopPoints(LoopStartPcm, TIMEUNIT.PCM, LoopEndPcm, TIMEUNIT.PCM);
            }
            uint posPcm = (uint)(positionMs / 1000.0 * Frequency);
            if (posPcm < TrimStart) posPcm = TrimStart;
            if (posPcm >= LengthPcm) posPcm = TrimStart;
            _channel.setPosition(posPcm, TIMEUNIT.PCM);

            ulong now = Mixer.Now();
            ulong start = startClock > now ? startClock : now + Mixer.Samples(0.02f);
            _channel.setDelay(start, 0, false);
            if (fadeInSeconds > 0.01f) Ramp(start, 0f, targetLevel, fadeInSeconds);
            else SetEnvelope(start, targetLevel, start, targetLevel, addPoint: targetLevel < 0.999f);
            _channel.setPaused(false);
            _started = true;
            return true;
        }

        /// <summary>Stop sample-accurately at a DSP clock (used to cut MP3 padding off an intro).</summary>
        public void StopAt(ulong clock)
        {
            if (!_started || _released) return;
            _channel.setDelay(0, clock, true);
        }

        public float LevelAt(ulong clock)
        {
            if (clock <= _envT0) return _envV0;
            if (clock >= _envT1 || _envT1 == _envT0) return _envV1;
            return _envV0 + (_envV1 - _envV0) * (float)((clock - _envT0) / (double)(_envT1 - _envT0));
        }

        /// <summary>Fade from the current level to target. stopAfter ends the channel when the fade finishes.</summary>
        public void FadeTo(float target, float seconds, bool stopAfter = false)
        {
            if (!_started || _released) return;
            ulong now = Mixer.Now();
            float cur = LevelAt(now);
            _channel.removeFadePoints(now, ulong.MaxValue);
            if (seconds < 0.01f) seconds = 0.01f;
            Ramp(now, cur, target, seconds);
            if (stopAfter) _channel.setDelay(0, now + Mixer.Samples(seconds) + 64, true);
        }

        // Equal-power style curve built from a few linear fade points.
        private void Ramp(ulong t0, float from, float to, float seconds)
        {
            ulong len = Mixer.Samples(seconds);
            const int steps = 8;
            _channel.addFadePoint(t0, from);
            for (int i = 1; i <= steps; i++)
            {
                double x = i / (double)steps;
                double shaped = to > from ? Math.Sin(x * Math.PI / 2) : 1 - Math.Cos(x * Math.PI / 2);
                _channel.addFadePoint(t0 + (ulong)(len * x), (float)(from + (to - from) * shaped));
            }
            _envT0 = t0; _envV0 = from; _envT1 = t0 + len; _envV1 = to;
        }

        private void SetEnvelope(ulong t0, float v0, ulong t1, float v1, bool addPoint)
        {
            if (addPoint) _channel.addFadePoint(t0, v0);
            _envT0 = t0; _envV0 = v0; _envT1 = t1; _envV1 = v1;
        }

        public void ApplyGain()
        {
            if (_started && !_released) _channel.setVolume(Gain);
        }

        public bool IsPlaying
        {
            get
            {
                if (!_started || _released) return false;
                return _channel.isPlaying(out bool p) == RESULT.OK && p;
            }
        }

        public uint PositionMs
        {
            get
            {
                if (!_started || _released) return 0;
                _channel.getPosition(out uint ms, TIMEUNIT.MS);
                return ms;
            }
        }

        public uint PositionPcm
        {
            get
            {
                if (!_started || _released) return 0;
                _channel.getPosition(out uint pcm, TIMEUNIT.PCM);
                return pcm;
            }
        }

        public void Release()
        {
            if (_released) return;
            _released = true;
            try { if (_started) _channel.stop(); } catch { }
            try { _sound.release(); } catch { }
        }
    }

    /// <summary>Reads the LAME/Xing header so MP3 intros and loops can be joined without a gap.</summary>
    public static class Mp3Gapless
    {
        private static readonly System.Collections.Generic.HashSet<string> Logged =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Samples to drop at start/end of FMOD's decode so an MP3 starts and ends on real audio.</summary>
        public static bool ComputeTrim(Info mp3, long fmodLength, string name, out long start, out long end)
        {
            start = end = 0;
            if (mp3 == null) return false;
            bool log = Logged.Add(name);
            long raw = mp3.TotalSamples;
            long trimmed = raw - mp3.Delay - mp3.Padding;
            if (Math.Abs(fmodLength - trimmed) <= 2)
            {
                if (log) Plugin.Log?.LogInfo($"MP3 gapless: FMOD already trims {name}.");
                return false;
            }
            // FMOD may output the LAME info frame as one extra frame of silence.
            long infoFrame = Math.Abs(fmodLength - (raw + mp3.SamplesPerFrame)) <= mp3.SamplesPerFrame / 2 ? mp3.SamplesPerFrame : 0;
            if (infoFrame == 0 && Math.Abs(fmodLength - raw) > mp3.SamplesPerFrame)
            {
                if (log) Plugin.Log?.LogInfo($"MP3 gapless: length mismatch for {name} (fmod {fmodLength}, lame {raw}); not trimming.");
                return false;
            }
            const int decoderDelay = 529;
            start = infoFrame + mp3.Delay + decoderDelay;
            end = Math.Max(0, mp3.Padding - decoderDelay);
            if (start + end >= fmodLength) { start = end = 0; return false; }
            if (log) Plugin.Log?.LogInfo($"MP3 gapless: {name} trim {start} start / {end} end samples.");
            return true;
        }

        public sealed class Info
        {
            public long TotalSamples;
            public int Delay, Padding, SamplesPerFrame;
        }

        public static Info Read(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var head = new byte[10];
                    if (fs.Read(head, 0, 10) < 10) return null;
                    long offset = 0;
                    if (head[0] == 'I' && head[1] == 'D' && head[2] == '3')
                    {
                        int size = (head[6] & 0x7f) << 21 | (head[7] & 0x7f) << 14 | (head[8] & 0x7f) << 7 | (head[9] & 0x7f);
                        offset = 10 + size + ((head[5] & 0x10) != 0 ? 10 : 0);
                    }
                    fs.Position = offset;
                    var buf = new byte[4096];
                    int n = fs.Read(buf, 0, buf.Length);
                    int p = 0;
                    while (p + 4 < n && !(buf[p] == 0xFF && (buf[p + 1] & 0xE0) == 0xE0)) p++;
                    if (p + 4 >= n) return null;

                    int versionBits = (buf[p + 1] >> 3) & 3; // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
                    int layerBits = (buf[p + 1] >> 1) & 3;   // 1 = Layer III
                    if (layerBits != 1) return null;
                    bool mpeg1 = versionBits == 3;
                    bool mono = ((buf[p + 3] >> 6) & 3) == 3;
                    int side = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
                    int x = p + 4 + side;
                    if (x + 8 > n) return null;
                    string tag = System.Text.Encoding.ASCII.GetString(buf, x, 4);
                    if (tag != "Xing" && tag != "Info") return null;

                    int flags = buf[x + 4] << 24 | buf[x + 5] << 16 | buf[x + 6] << 8 | buf[x + 7];
                    int q = x + 8;
                    if ((flags & 1) == 0) return null;
                    long frames = (uint)(buf[q] << 24 | buf[q + 1] << 16 | buf[q + 2] << 8 | buf[q + 3]);
                    q += 4;
                    if ((flags & 2) != 0) q += 4;
                    if ((flags & 4) != 0) q += 100;
                    if ((flags & 8) != 0) q += 4;
                    // LAME extension: 9 byte encoder string, then delay/padding at +21 (12 bits each)
                    if (q + 24 > n) return null;
                    int d = q + 21;
                    int delay = buf[d] << 4 | buf[d + 1] >> 4;
                    int padding = (buf[d + 1] & 0x0F) << 8 | buf[d + 2];
                    int spf = mpeg1 ? 1152 : 576;
                    return new Info { TotalSamples = frames * spf, Delay = delay, Padding = padding, SamplesPerFrame = spf };
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
