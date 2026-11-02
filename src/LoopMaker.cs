using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FMOD;
using UnityEngine;

namespace SN1DynamicMusic
{
    /// <summary>
    /// Turns a song into name-intro.wav + name-loop.wav in its folder.
    /// Decoding uses the game's own FMOD (so any format the mod can play works), a little per frame;
    /// the analysis and file writing run on a worker thread so the game keeps running.
    /// </summary>
    internal static class LoopMaker
    {
        public static bool Busy { get; private set; }

        public static void MakeForCurrent()
        {
            var d = Plugin.Instance?.Director;
            string folder = d?.ActiveFolder, path = d?.ActiveTrackPath;
            if (folder == null || path == null) { Plugin.Notify("Nothing is playing. Go somewhere with music first."); return; }
            var info = Plugin.Instance.Library.Get(folder);
            if (info != null && info.IsIntroLoop) { Plugin.Notify($"[{folder}] already has an intro+loop"); return; }
            Start(new List<(string, string)> { (folder, path) });
        }

        public static void MakeForSingleTrackFolders()
        {
            var jobs = Plugin.Instance.Library.All
                .Where(f => f.Tracks.Count == 1 && !f.IsIntroLoop)
                .Select(f => (f.Name, f.Tracks[0]))
                .ToList();
            if (jobs.Count == 0) { Plugin.Notify("No folders with exactly one track (that isn't already intro+loop)"); return; }
            Start(jobs);
        }

        private static void Start(List<(string folder, string path)> jobs)
        {
            if (Busy) { Plugin.Notify("Already making loops, please wait"); return; }
            Plugin.Instance.StartCoroutine(Run(jobs));
        }

        private static IEnumerator Run(List<(string folder, string path)> jobs)
        {
            Busy = true;
            int made = 0;
            foreach (var (folder, path) in jobs)
            {
                string name = Path.GetFileName(path);
                Plugin.Notify($"Making intro+loop from {name}…");
                Decoded pcm = null;
                string error = null;
                var dec = Decode(path, r => pcm = r, e => error = e);
                while (dec.MoveNext()) yield return dec.Current;
                if (pcm == null) { Plugin.Notify($"{name}: {error ?? "could not decode"}"); continue; }

                var task = Task.Run(() =>
                {
                    var r = LoopFinder.Find(pcm.Data, pcm.Channels, pcm.Rate);
                    if (!r.Ok) return r;
                    LoopFinder.Split(pcm.Data, pcm.Channels, pcm.Rate, r, out var intro, out var loop);
                    string dir = Path.GetDirectoryName(path);
                    string baseName = Path.GetFileNameWithoutExtension(path);
                    Wav.Write(Path.Combine(dir, baseName + "-intro.wav"), intro, pcm.Channels, pcm.Rate);
                    Wav.Write(Path.Combine(dir, baseName + "-loop.wav"), loop, pcm.Channels, pcm.Rate);
                    return r;
                });
                while (!task.IsCompleted) yield return null;

                if (task.IsFaulted)
                {
                    Plugin.Log.LogError($"Loop maker failed on {name}: {task.Exception}");
                    Plugin.Notify($"{name}: failed ({task.Exception?.InnerException?.Message})");
                    continue;
                }
                var res = task.Result;
                Plugin.Log.LogInfo($"Loop maker [{folder}] {name}: {(res.Ok ? res.Message : "no loop — " + res.Message)}");
                if (!res.Ok) { Plugin.Notify($"{name}: {res.Message}"); continue; }
                string quality = res.Score >= 0.85f ? "good" : res.Score >= 0.78f ? "okay, give it a listen" : "rough, give it a listen";
                Plugin.Notify($"[{folder}] intro+loop made ({quality}): loop {res.LoopStart / (double)pcm.Rate:F1}s–{res.LoopEnd / (double)pcm.Rate:F1}s");
                made++;
            }
            Busy = false;
            if (made > 0)
            {
                Plugin.Rescan();
                Plugin.Notify($"Made {made} intro+loop pair(s). Delete the -intro/-loop .wav files to go back to the original.");
            }
        }

        private sealed class Decoded { public short[] Data; public int Channels, Rate; }

        /// <summary>Decode a file to 16-bit PCM with FMOD, ~2 MB per frame.</summary>
        private static IEnumerator Decode(string path, Action<Decoded> done, Action<string> fail)
        {
            var core = Mixer.Core;
            if (core.createSound(path, MODE._2D | MODE.OPENONLY | MODE.ACCURATETIME, out Sound snd) != RESULT.OK)
            {
                fail("could not open");
                yield break;
            }
            IntPtr buf = IntPtr.Zero;
            try
            {
                snd.getFormat(out _, out SOUND_FORMAT fmt, out int ch, out int bits);
                snd.getDefaults(out float freq, out _);
                snd.getLength(out uint lenPcm, TIMEUNIT.PCM);
                int bytesPer = bits / 8;
                if (ch <= 0 || bytesPer <= 0 || lenPcm == 0 || fmt == SOUND_FORMAT.BITSTREAM) { fail("unsupported format"); yield break; }
                if ((long)lenPcm * ch > 400_000_000L / 2) { fail("track too long (over ~70 minutes)"); yield break; }

                var data = new short[(long)lenPcm * ch];
                const int chunk = 1 << 18;
                buf = Marshal.AllocHGlobal(chunk);
                var tmp = new byte[chunk];
                long outIdx = 0;
                int perFrame = 0;
                while (outIdx < data.Length)
                {
                    var r = snd.readData(buf, (uint)(chunk - chunk % (bytesPer * ch)), out uint read);
                    if (read == 0 || (r != RESULT.OK && r != RESULT.ERR_FILE_EOF)) break;
                    Marshal.Copy(buf, tmp, 0, (int)read);
                    int samples = (int)read / bytesPer;
                    for (int i = 0; i < samples && outIdx < data.Length; i++, outIdx++)
                        data[outIdx] = ToShort(tmp, i * bytesPer, fmt);
                    if (r == RESULT.ERR_FILE_EOF) break;
                    if (++perFrame >= 8) { perFrame = 0; yield return null; }
                }

                // Cut MP3 encoder padding so the loop math works on the real music.
                long frames = outIdx / ch;
                long trimStart = 0, trimEnd = 0;
                if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                    Mp3Gapless.ComputeTrim(Mp3Gapless.Read(path), lenPcm, Path.GetFileName(path), out trimStart, out trimEnd);
                long keep = Math.Max(0, frames - trimStart - trimEnd);
                if (trimStart > 0 || keep != frames)
                {
                    var cut = new short[keep * ch];
                    Array.Copy(data, trimStart * ch, cut, 0, cut.Length);
                    data = cut;
                }
                else if (outIdx < data.Length) Array.Resize(ref data, (int)outIdx);

                done(new Decoded { Data = data, Channels = ch, Rate = (int)Math.Round(freq) });
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                snd.release();
            }
        }

        private static short ToShort(byte[] b, int o, SOUND_FORMAT fmt)
        {
            switch (fmt)
            {
                case SOUND_FORMAT.PCM8: return (short)((sbyte)b[o] << 8);
                case SOUND_FORMAT.PCM16: return (short)(b[o] | b[o + 1] << 8);
                case SOUND_FORMAT.PCM24: return (short)(b[o + 1] | b[o + 2] << 8);
                case SOUND_FORMAT.PCM32: return (short)(b[o + 2] | b[o + 3] << 8);
                case SOUND_FORMAT.PCMFLOAT:
                    float f = BitConverter.ToSingle(b, o);
                    return (short)Mathf.Clamp(Mathf.Round(f * 32767f), -32768f, 32767f);
                default: return 0;
            }
        }
    }
}
