using System;
using System.IO;
using System.Text;

namespace SN1DynamicMusic
{
    /// <summary>
    /// Finds a seamless loop inside a song and splits it into an intro and a loop.
    /// Pure C# (no Unity / FMOD) so it can run on a worker thread and be tested outside the game.
    ///
    /// 1. Spectral fingerprint every ~46 ms (32 log-spaced bands + loudness).
    /// 2. For every loop length L, slide along the diagonal of the self-similarity matrix and
    ///    find the start a where ~12 s around a sound most like ~12 s around a+L.
    /// 3. Refine L to the exact sample with waveform cross-correlation.
    /// 4. Bake a short crossfade into the loop's tail so its last sample flows into its first,
    ///    and the intro's last sample flows into the loop's first.
    /// </summary>
    public static class LoopFinder
    {
        public sealed class Result
        {
            public bool Ok;
            public string Message;
            public long LoopStart;   // frames (sample frames, all channels)
            public long LoopEnd;     // exclusive
            public float Score;      // 0..1, mean spectral similarity across the seam window
            public double Seconds(long frames, int rate) => frames / (double)rate;
        }

        private const int Fft = 2048;
        private const int Bands = 32;

        public static Result Find(short[] pcm, int channels, int rate, Action<float> progress = null)
        {
            long frames = pcm.Length / channels;
            double duration = frames / (double)rate;
            if (duration < 20) return Fail("Track is too short to loop (needs 20 s or more).");

            // ---- mono mixdown ----
            var mono = new float[frames];
            for (long i = 0; i < frames; i++)
            {
                int acc = 0;
                for (int c = 0; c < channels; c++) acc += pcm[i * channels + c];
                mono[i] = acc / (32768f * channels);
            }

            // ---- features ----
            int hop = Math.Max(rate / 22, 512);                 // ~45 ms
            int n = (int)((frames - Fft) / hop);
            if (n > 9000) { hop = (int)((frames - Fft) / 9000); n = (int)((frames - Fft) / hop); }
            var feat = new float[n][];
            var loud = new float[n];
            var window = new float[Fft];
            for (int i = 0; i < Fft; i++) window[i] = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * i / (Fft - 1));
            int[] bandEdge = BandEdges(rate);
            var re = new double[Fft];
            var im = new double[Fft];
            for (int f = 0; f < n; f++)
            {
                long off = (long)f * hop;
                double energy = 0;
                for (int i = 0; i < Fft; i++)
                {
                    double v = mono[off + i];
                    energy += v * v;
                    re[i] = v * window[i];
                    im[i] = 0;
                }
                FftInPlace(re, im);
                var v32 = new float[Bands];
                double mean = 0;
                for (int b = 0; b < Bands; b++)
                {
                    double e = 0;
                    for (int k = bandEdge[b]; k < bandEdge[b + 1]; k++) e += re[k] * re[k] + im[k] * im[k];
                    v32[b] = (float)Math.Log(1e-9 + e / Math.Max(1, bandEdge[b + 1] - bandEdge[b]));
                    mean += v32[b];
                }
                mean /= Bands;
                double norm = 0;
                for (int b = 0; b < Bands; b++) { v32[b] -= (float)mean; norm += v32[b] * v32[b]; }
                norm = Math.Sqrt(norm) + 1e-9;
                for (int b = 0; b < Bands; b++) v32[b] /= (float)norm;
                feat[f] = v32;
                loud[f] = (float)(10 * Math.Log10(energy / Fft + 1e-10));
                if ((f & 255) == 0) progress?.Invoke(0.3f * f / n);
            }

            // ---- diagonal search ----
            double fps = rate / (double)hop;
            int W = (int)(12 * fps);                    // seam must match for ~12 s
            int half = W / 2;
            int minL = (int)Math.Max(15 * fps, n * 0.25);
            int maxL = n - W - 1;
            if (maxL <= minL) return Fail("Track is too short for a reliable loop.");

            double bestScore = double.MinValue, bestSim = 0;
            int bestA = 0, bestL = 0;
            var diag = new float[n];
            for (int L = minL; L <= maxL; L++)
            {
                int len = n - L;
                for (int i = 0; i < len; i++)
                {
                    float[] x = feat[i], y = feat[i + L];
                    float dot = 0;
                    for (int b = 0; b < Bands; b++) dot += x[b] * y[b];
                    float dl = Math.Abs(loud[i] - loud[i + L]);
                    diag[i] = dot - 0.03f * dl;     // a 10 dB jump costs 0.3
                }
                // sliding window sum; seam frame a = start + half
                double sum = 0;
                for (int i = 0; i < W && i < len; i++) sum += diag[i];
                for (int s = 0; s + W <= len; s++)
                {
                    if (s > 0) sum += diag[s + W - 1] - diag[s - 1];
                    double sim = sum / W;
                    // Prefer long loops (more of the song) and seams that are not at the very start.
                    double score = sim + 0.06 * L / n;
                    if (score > bestScore)
                    {
                        bestScore = score; bestSim = sim; bestA = s + half; bestL = L;
                    }
                }
                if ((L & 63) == 0) progress?.Invoke(0.3f + 0.6f * (L - minL) / Math.Max(1, maxL - minL));
            }

            if (bestSim < 0.70)
                return Fail($"No convincing repeat found (best match {bestSim:P0}). This track may not loop.");

            // ---- sample-accurate refinement ----
            long a = (long)bestA * hop + Fft / 2;
            long Ls = (long)bestL * hop;
            int M = Math.Min(rate / 2, (int)(frames - a - Ls - 1));  // 0.5 s correlation window
            if (M < rate / 10) return Fail("Loop end runs past the track.");
            long best = Ls;
            double bestC = double.MinValue;
            int range = hop;
            for (int pass = 0; pass < 2; pass++)
            {
                int step = pass == 0 ? 16 : 1;
                long center = best;
                long lo = center - (pass == 0 ? range : 16), hi = center + (pass == 0 ? range : 16);
                for (long cand = lo; cand <= hi; cand += step)
                {
                    if (a + cand + M >= frames || cand <= 0) continue;
                    double c = 0, ex = 0, ey = 0;
                    for (int i = 0; i < M; i += 2)
                    {
                        double x = mono[a + i], y = mono[a + cand + i];
                        c += x * y; ex += x * x; ey += y * y;
                    }
                    c /= Math.Sqrt(ex * ey) + 1e-12;
                    if (c > bestC) { bestC = c; best = cand; }
                }
            }
            Ls = best;

            // crossfade needs audio before the loop start
            long xf = Crossfade(rate);
            if (a < xf) a = xf;
            long end = a + Ls;
            if (end > frames) return Fail("Loop end runs past the track.");

            progress?.Invoke(1f);
            return new Result
            {
                Ok = true,
                LoopStart = a,
                LoopEnd = end,
                Score = (float)bestSim,
                Message = $"loop {a / (double)rate:F2}s → {end / (double)rate:F2}s ({Ls / (double)rate:F1}s long), " +
                          $"match {bestSim:P0}, waveform {bestC:F2}",
            };
        }

        public static long Crossfade(int rate) => rate / 10; // 100 ms

        /// <summary>Intro = [0, a). Loop = [a, b) with its last 100 ms blended toward the audio just before a.</summary>
        public static void Split(short[] pcm, int channels, int rate, Result r, out short[] intro, out short[] loop)
        {
            long a = r.LoopStart, b = r.LoopEnd, len = b - a, xf = Math.Min(Crossfade(rate), len / 4);
            intro = new short[a * channels];
            Array.Copy(pcm, 0, intro, 0, intro.Length);
            loop = new short[len * channels];
            Array.Copy(pcm, a * channels, loop, 0, loop.Length);
            for (long j = 0; j < xf; j++)
            {
                double w = (j + 1) / (double)xf;          // 0 → 1 across the tail
                long dst = (len - xf + j) * channels;     // loop tail (source b - xf + j)
                long src = (a - xf + j) * channels;       // what originally led into a
                for (int c = 0; c < channels; c++)
                {
                    double v = loop[dst + c] * (1 - w) + pcm[src + c] * w;
                    loop[dst + c] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(v)));
                }
            }
        }

        private static Result Fail(string msg) => new Result { Ok = false, Message = msg };

        private static int[] BandEdges(int rate)
        {
            var edges = new int[Bands + 1];
            double lo = 60, hi = Math.Min(10000, rate / 2.0 - 100);
            for (int b = 0; b <= Bands; b++)
            {
                double hz = lo * Math.Pow(hi / lo, b / (double)Bands);
                edges[b] = (int)Math.Round(hz * Fft / rate);
            }
            for (int b = 1; b <= Bands; b++) if (edges[b] <= edges[b - 1]) edges[b] = edges[b - 1] + 1;
            return edges;
        }

        private static void FftInPlace(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int u = i + k, v = i + k + len / 2;
                        double tr = re[v] * cr - im[v] * ci, ti = re[v] * ci + im[v] * cr;
                        re[v] = re[u] - tr; im[v] = im[u] - ti;
                        re[u] += tr; im[u] += ti;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr; cr = ncr;
                    }
                }
            }
        }
    }

    public static class Wav
    {
        public static void Write(string path, short[] pcm, int channels, int rate)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                int dataBytes = pcm.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataBytes);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataBytes);
                var buf = new byte[Math.Min(dataBytes, 1 << 20)];
                for (int i = 0; i < pcm.Length;)
                {
                    int count = Math.Min(buf.Length / 2, pcm.Length - i);
                    Buffer.BlockCopy(pcm, i * 2, buf, 0, count * 2);
                    w.Write(buf, 0, count * 2);
                    i += count;
                }
            }
        }

        /// <summary>16-bit PCM WAV reader (used by the offline test harness).</summary>
        public static short[] Read16(string path, out int channels, out int rate)
        {
            var b = File.ReadAllBytes(path);
            channels = 0; rate = 0;
            int p = 12;
            short[] data = null;
            while (p + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, p, 4);
                int size = BitConverter.ToInt32(b, p + 4);
                if (id == "fmt ") { channels = BitConverter.ToInt16(b, p + 10); rate = BitConverter.ToInt32(b, p + 12); }
                else if (id == "data")
                {
                    size = Math.Min(size, b.Length - p - 8);
                    data = new short[size / 2];
                    Buffer.BlockCopy(b, p + 8, data, 0, size - size % 2);
                }
                p += 8 + size + (size & 1);
            }
            return data;
        }
    }
}
