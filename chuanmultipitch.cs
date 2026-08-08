using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

class ChuanMultiPitch
{
    const string HelpText =
        "{ MULTIPITCH BY CHUAN}\n" +
        "\n" +
        "HOW TO USE:\n" +
        "\n" +
        "YOU NEED 4 ARGUMENTS LIKE:\n" +
        "\n" +
        "[1] INPUT (.wav ONLY)\n" +
        "\n" +
        "[2] OUTPUT (.wav ONLY)\n" +
        "\n" +
        "[3] FLAG (--pitch)\n" +
        "\n" +
        "[4] VALUE (semitones -120..120, decimals ok, example: --pitch 7.5;-2.5)\n" +
        "\n" +
        "[5] OPTIONAL FLAG (--pitchtype default|a17|rubberband, default = default)\n" +
        "    default    = built-in resample + WSOLA, volume = (values / 2) + 0.5\n" +
        "    a17        = sox pitch per voice,        volume = values (needs sox)\n" +
        "    rubberband = ffmpeg rubberband per voice, volume = values (needs ffmpeg)\n" +
        "\n" +
        "[6] OPTIONAL FLAG (--type default|audiobuggy|reverse|oppositepitch, default = default)\n" +
        "    default       = plain pitch shift\n" +
        "    audiobuggy    = swap halves: [2nd half][1st half]\n" +
        "    reverse       = play the audio backwards\n" +
        "    oppositepitch = flip pitch signs, --pitch -7;6 -> 7;-6\n" +
        "\n" +
        "volume per pitch: default = (values / 2) + 0.5, a17/rubberband = values\n" +
        "pitch shifts WITHOUT speed change\n" +
        "\n" +
        "example:\n" +
        "\n" +
        "chuanmultipitch.exe input.wav output.wav --pitch 7;-5\n" +
        "\n" +
        "{ MULTIPITCH BY CHUAN}";

    static int Main(string[] args)
    {
        bool nlogs = false;
        List<string> rest = new List<string>();
        string pitchtype = "default";
        string type = "default";

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                Console.WriteLine(HelpText);
                return 0;
            }
            if (a == "--nlogs")
            {
                nlogs = true;
                continue;
            }
            if (a == "--pitchtype")
            {
                if (i + 1 < args.Length) pitchtype = args[++i];
                continue;
            }
            if (a == "--type")
            {
                if (i + 1 < args.Length) type = args[++i];
                continue;
            }
            rest.Add(a);
        }

        if (pitchtype != "default" && pitchtype != "a17" && pitchtype != "rubberband")
        {
            Fail(nlogs, "ERROR: Invalid pitchtype.");
            return 1;
        }

        if (type != "default" && type != "audiobuggy" &&
            type != "reverse" && type != "oppositepitch")
        {
            Fail(nlogs, "ERROR: Invalid type.");
            return 1;
        }

        if (rest.Count < 4)
        {
            Fail(nlogs, "ERROR: Not enough arguments. You need 4 arguments.");
            return 1;
        }

        string input = rest[0];
        string output = rest[1];
        string flag = rest[2];
        string value = rest[3];

        if (flag != "--pitch")
        {
            Fail(nlogs, "ERROR: Invalid flag.");
            return 1;
        }

        string[] parts = value.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        List<double> pitches = new List<double>();
        foreach (string p in parts)
        {
            double v;
            if (!double.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < -120.0 || v > 120.0)
            {
                Fail(nlogs, "ERROR: Invalid value.");
                return 1;
            }
            pitches.Add(v);
        }
        if (pitches.Count == 0)
        {
            Fail(nlogs, "ERROR: Invalid value.");
            return 1;
        }

        if (type == "oppositepitch")
        {
            for (int i = 0; i < pitches.Count; i++)
                pitches[i] = -pitches[i];
        }

        byte[] file;
        try
        {
            file = File.ReadAllBytes(input);
        }
        catch
        {
            Fail(nlogs, "ERROR: Invalid input.");
            return 1;
        }

        WavInfo info;
        if (!ParseWav(file, out info))
        {
            Fail(nlogs, "ERROR: Not a supported WAV file (use 8/16-bit PCM or 32-bit float).");
            return 1;
        }

        long frames = info.DataSize / (info.Bits / 8) / info.Channels;
        long total = frames * info.Channels;

        double[] pcm = DecodePcm(file, info, frames);
        double[] mix = new double[total];

        if (type == "reverse")
        {
            ReversePcm(pcm, frames, info.Channels);
        }
        else if (type == "audiobuggy")
        {
            long nf;
            pcm = AudioBuggyPcm(pcm, frames, info.Channels, out nf);
            frames = nf;
            total = frames * info.Channels;
            mix = new double[total];
        }

        double inRms = 0.0;
        for (long i = 0; i < total; i++)
            inRms += pcm[i] * pcm[i];
        inRms = Math.Sqrt(inRms / (double)total);

        bool ok = true;
        bool ext = false;
        if (pitchtype == "default")
        {
            foreach (double p in pitches)
            {
                if (!AddVoice(mix, frames, info.Channels, pcm, p))
                {
                    ok = false;
                    break;
                }
            }
        }
        else
        {
            string inTmp = "chuanmp." + Process.GetCurrentProcess().Id + ".in.wav";
            if (!WriteWav(inTmp, info.Channels, info.SampleRate, pcm, frames))
            {
                Fail(nlogs, "ERROR: Cannot write temp file.");
                return 1;
            }
            for (int i = 0; i < pitches.Count && ok; i++)
            {
                string tmp = "chuanmp." + Process.GetCurrentProcess().Id + "." + i + ".wav";
                string cmd;
                if (pitchtype == "a17")
                {
                    long cents = (long)Math.Round(pitches[i] * 100.0);
                    cmd = "sox -q \"" + inTmp + "\" \"" + tmp + "\" pitch " + cents.ToString() + " 10 10 10";
                }
                else
                {
                    double ratio = Math.Pow(2.0, pitches[i] / 12.0);
                    cmd = "ffmpeg -hide_banner -loglevel error -y -i \"" + inTmp +
                          "\" -af \"rubberband=pitch=" + ratio.ToString("F6", CultureInfo.InvariantCulture) +
                          ":window=long:transients=crisp:smoothing=2.14748e+09/4.9:" +
                          "pitchq=speed:detector=percussive\" \"" + tmp + "\"";
                }
                if (!Run(cmd)) { ext = true; ok = false; break; }
                WavInfo ti;
                long tf;
                double[] tp = LoadWav(tmp, out ti, out tf);
                File.Delete(tmp);
                if (tp == null) { ext = true; ok = false; break; }
                if (ti.Channels != info.Channels) { ext = true; ok = false; break; }
                long lim = tf < frames ? tf : frames;
                for (long j = 0; j < lim * info.Channels; j++)
                    mix[j] += tp[j];
            }
            File.Delete(inTmp);
        }
        if (!ok)
        {
            if (ext) Fail(nlogs, "ERROR: External pitch tool (sox/ffmpeg) failed.");
            else Fail(nlogs, "ERROR: Out of memory.");
            return 1;
        }

        // volume per pitch: default = (values / 2) + 0.5, a17/rubberband = values;
        // scale the mix so its loudness is that many x the input; a soft limiter
        // catches the louder peaks so it never harshly clips
        double vol = (pitchtype == "default")
                     ? ((double)pitches.Count / 2.0 + 0.5) : (double)pitches.Count;
        double rms = 0.0;
        for (long i = 0; i < total; i++)
            rms += mix[i] * mix[i];
        rms = Math.Sqrt(rms / (double)total);
        if (inRms > 0.0 && rms > 0.0)
        {
            double g = (inRms * vol) / rms;
            const double T = 0.95; // soft-limiter threshold
            for (long i = 0; i < total; i++)
            {
                double v = mix[i] * g;
                double a = Math.Abs(v);
                if (a > T)
                {
                    double over = (a - T) / (1.0 - T);
                    double lim = T + (1.0 - T) * (over / (1.0 + over));
                    v = (v < 0.0) ? -lim : lim;
                }
                mix[i] = v;
            }
        }

        if (!WriteWav(output, info.Channels, info.SampleRate, mix, frames))
        {
            Fail(nlogs, "ERROR: Cannot write output.");
            return 1;
        }

        if (!nlogs)
            Console.WriteLine("Done: " + output);
        return 0;
    }

    static void Fail(bool nlogs, string msg)
    {
        if (nlogs)
            return;
        Console.WriteLine(HelpText);
        Console.WriteLine();
        Console.WriteLine(msg);
    }

    // run an external command (sox/ffmpeg); true on success
    static bool Run(string cmd)
    {
        try
        {
            Process p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + cmd)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // load a WAV file into a double buffer (used for external-tool temp files)
    static double[] LoadWav(string path, out WavInfo info, out long frames)
    {
        byte[] file;
        try { file = File.ReadAllBytes(path); }
        catch { info = null; frames = 0; return null; }
        if (!ParseWav(file, out info)) { frames = 0; return null; }
        frames = info.DataSize / (info.Bits / 8) / info.Channels;
        return DecodePcm(file, info, frames);
    }

    // ------------------------------------------------------------------
    // --type transforms
    // ------------------------------------------------------------------

    static void ReversePcm(double[] pcm, long frames, int channels)
    {
        for (long i = 0; i < frames / 2; i++)
        {
            long a = i * channels;
            long b = (frames - 1 - i) * channels;
            for (int c = 0; c < channels; c++)
            {
                double t = pcm[a + c];
                pcm[a + c] = pcm[b + c];
                pcm[b + c] = t;
            }
        }
    }

    // audiobuggy: {1} = trim start by half duration (keep 2nd half),
    // {2} = trim end by half duration (keep 1st half), concat {1,2}
    static double[] AudioBuggyPcm(double[] pcm, long frames, int channels, out long outFrames)
    {
        long half = frames / 2;
        if (half < 1) half = 1;
        long on = half * 2;
        double[] outp = new double[on * channels];
        for (long f = 0; f < half; f++)
        {
            for (int c = 0; c < channels; c++)
            {
                outp[f * channels + c] = pcm[(f + half) * channels + c];
                outp[(f + half) * channels + c] = pcm[f * channels + c];
            }
        }
        outFrames = on;
        return outp;
    }

    // ------------------------------------------------------------------
    // WAV helpers
    // ------------------------------------------------------------------

    class WavInfo
    {
        public int Channels;
        public int SampleRate;
        public int Bits;
        public int Fmt; // 1 = PCM, 3 = float
        public long DataOffset;
        public long DataSize;
    }

    static uint Rd32(byte[] b, int o)
    {
        return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }

    static ushort Rd16(byte[] b, int o)
    {
        return (ushort)(b[o] | (b[o + 1] << 8));
    }

    static bool ParseWav(byte[] buf, out WavInfo info)
    {
        info = new WavInfo();
        info.DataOffset = -1;
        if (buf.Length < 44) return false;
        if (Encoding.ASCII.GetString(buf, 0, 4) != "RIFF") return false;
        if (Encoding.ASCII.GetString(buf, 8, 4) != "WAVE") return false;

        long pos = 12;
        while (pos + 8 <= buf.Length)
        {
            string id = Encoding.ASCII.GetString(buf, (int)pos, 4);
            long csize = Rd32(buf, (int)pos + 4);
            long body = pos + 8;
            if (csize < 0 || body + csize > buf.Length) return false;

            if (id == "fmt ")
            {
                if (csize < 16) return false;
                info.Fmt = Rd16(buf, (int)body);
                info.Channels = Rd16(buf, (int)body + 2);
                info.SampleRate = (int)Rd32(buf, (int)body + 4);
                info.Bits = Rd16(buf, (int)body + 14);
            }
            else if (id == "data")
            {
                if (info.DataOffset < 0)
                {
                    info.DataOffset = body;
                    info.DataSize = csize;
                }
            }
            pos = body + csize + (csize & 1);
        }

        if (info.DataOffset < 0) return false;
        if (info.Channels < 1 || info.Channels > 2) return false;
        if (info.SampleRate < 1) return false;
        if (info.Fmt != 1 && info.Fmt != 3) return false;
        if (info.Fmt == 1 && info.Bits != 8 && info.Bits != 16) return false;
        if (info.Fmt == 3 && info.Bits != 32) return false;
        return true;
    }

    static double[] DecodePcm(byte[] buf, WavInfo info, long frames)
    {
        int ch = info.Channels;
        double[] pcm = new double[frames * ch];
        long d = info.DataOffset;

        if (info.Fmt == 1 && info.Bits == 16)
        {
            for (long i = 0; i < frames * ch; i++)
                pcm[i] = (double)(short)Rd16(buf, (int)(d + i * 2)) / 32768.0;
        }
        else if (info.Fmt == 1 && info.Bits == 8)
        {
            for (long i = 0; i < frames * ch; i++)
                pcm[i] = ((double)buf[d + i] - 128.0) / 128.0;
        }
        else // float 32
        {
            for (long i = 0; i < frames * ch; i++)
            {
                uint u = Rd32(buf, (int)(d + i * 4));
                float f = BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
                pcm[i] = (double)f;
            }
        }
        return pcm;
    }

    static bool WriteWav(string path, int channels, int sampleRate, double[] mix, long frames)
    {
        byte[] hdr = new byte[44];
        Encoding.ASCII.GetBytes("RIFF", 0, 4, hdr, 0);
        Wr32(hdr, 4, (uint)(36 + frames * channels * 2));
        Encoding.ASCII.GetBytes("WAVE", 0, 4, hdr, 8);
        Encoding.ASCII.GetBytes("fmt ", 0, 4, hdr, 12);
        Wr32(hdr, 16, 16);
        Wr16(hdr, 20, 1);
        Wr16(hdr, 22, (ushort)channels);
        Wr32(hdr, 24, (uint)sampleRate);
        Wr32(hdr, 28, (uint)(sampleRate * channels * 2));
        Wr16(hdr, 32, (ushort)(channels * 2));
        Wr16(hdr, 34, 16);
        Encoding.ASCII.GetBytes("data", 0, 4, hdr, 36);
        Wr32(hdr, 40, (uint)(frames * channels * 2));

        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                fs.Write(hdr, 0, 44);
                byte[] data = new byte[frames * channels * 2];
                for (long i = 0; i < frames * channels; i++)
                {
                    double v = mix[i];
                    if (v > 1.0) v = 1.0;
                    if (v < -1.0) v = -1.0;
                    short s = (short)Math.Round(v * 32767.0);
                    Wr16(data, (int)(i * 2), (ushort)s);
                }
                fs.Write(data, 0, data.Length);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void Wr32(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v & 0xFF);
        b[o + 1] = (byte)((v >> 8) & 0xFF);
        b[o + 2] = (byte)((v >> 16) & 0xFF);
        b[o + 3] = (byte)((v >> 24) & 0xFF);
    }

    static void Wr16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)(v & 0xFF);
        b[o + 1] = (byte)((v >> 8) & 0xFF);
    }

    // ------------------------------------------------------------------
    // DSP: windowed-sinc resampler
    // ------------------------------------------------------------------

    static double[] Resample(double[] input, double ratio)
    {
        const int half = 32; // sinc taps per side
        long n = input.Length;
        long on = Math.Max(1, (long)Math.Floor(n * ratio + 0.5));
        double[] outBuf = new double[on];

        double fc = 0.5 * (ratio < 1.0 ? ratio : 1.0); // anti-alias cutoff
        if (fc <= 0.0) fc = 0.01;

        for (long j = 0; j < on; j++)
        {
            double t = ((double)j + 0.5) / ratio - 0.5;
            long i = (long)Math.Floor(t);
            double d = t - (double)i;
            double acc = 0.0;
            for (long k = -half; k < half; k++)
            {
                long idx = i + k;
                if (idx < 0 || idx >= n) continue;
                double x = d - (double)k;
                double a = 2.0 * Math.PI * fc * x;
                double s = (a == 0.0) ? 1.0 : Math.Sin(a) / a;
                double w = 0.5 * (1.0 + Math.Cos(Math.PI * x / (double)half));
                acc += input[idx] * s * w;
            }
            outBuf[j] = acc * (2.0 * fc);
        }
        return outBuf;
    }

    // ------------------------------------------------------------------
    // DSP: WSOLA time-stretch (duration scale = alpha, pitch unchanged)
    // ------------------------------------------------------------------

    static double[] Wsola(double[] input, double alpha)
    {
        const long Hs = 256;  // synthesis hop
        const long W = 1024;  // window length (4 * Hs)
        const long W2 = W / 2;
        const long delta = 96;// search range
        const long Lc = 128;  // correlation length
        long n = input.Length;
        long on = Math.Max(1, (long)Math.Floor(n * alpha + 0.5));

        double[] outBuf = new double[on + 2 * W];
        double[] win = new double[W];
        for (long k = 0; k < W; k++)
            win[k] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * k / (double)(W - 1)));

        // first frame: analysis center = synthesis center = W2
        for (long k = 0; k < W && k < n; k++)
            outBuf[k] += input[k] * win[k];

        // absolute synthesis grid => tempo-exact stretch by alpha
        long sc = W2; // synthesis center
        while (true)
        {
            sc += Hs;
            if (sc >= on + W2) break;

            long ideal = (long)Math.Floor((double)sc / alpha);
            long lo = ideal - delta;
            if (lo < 0) lo = 0;
            long hi = ideal + delta;
            if (hi + W2 > n) hi = n - W2;
            if (hi > ideal + delta) hi = ideal + delta;
            if (hi < lo) hi = lo;

            long best = ideal;
            double bestc = double.NegativeInfinity;
            long refPos = sc - W2;
            for (long pos = lo; pos <= hi; pos++)
            {
                double c = 0.0;
                for (long m = 0; m < Lc; m++)
                {
                    long ai = pos - W2 + m;
                    long oi = refPos + m;
                    if (ai < 0 || ai >= n || oi < 0 || oi >= outBuf.Length) break;
                    c += input[ai] * outBuf[oi];
                }
                if (c > bestc) { bestc = c; best = pos; }
            }

            for (long k = 0; k < W; k++)
            {
                long ai = best - W2 + k;
                long oi = sc - W2 + k;
                if (ai < 0 || ai >= n || oi < 0 || oi >= outBuf.Length) continue;
                outBuf[oi] += input[ai] * win[k];
            }
        }

        double[] res = new double[on];
        for (long j = 0; j < on; j++)
            res[j] = outBuf[j] * 0.5; // hann 4x OLA gain
        return res;
    }

    // ------------------------------------------------------------------
    // Voice pipeline: resample (pitch) then WSOLA (restore duration)
    // ------------------------------------------------------------------

    static bool AddVoice(double[] mix, long frames, int channels, double[] pcm, double semitones)
    {
        double f = Math.Pow(2.0, semitones / 12.0);
        double ratio = 1.0 / f;
        long n = frames;

        for (int c = 0; c < channels; c++)
        {
            double[] ch = new double[n];
            for (long j = 0; j < n; j++)
                ch[j] = pcm[j * channels + c];

            double[] r = Resample(ch, ratio);
            double[] s = Wsola(r, f);

            long lim = Math.Min(s.Length, n);
            for (long j = 0; j < lim; j++)
                mix[j * channels + c] += s[j];
        }
        return true;
    }
}
