/*
 * { MULTIPITCH BY CHUAN }
 *
 * Pure C++ implementation. No sox, no external libraries.
 * Supports WAV (8-bit PCM, 16-bit PCM, 32-bit float; mono or stereo).
 *
 * HOW TO USE:
 *   chuanmultipitch input.wav output.wav --pitch 7;8
 *
 *   --pitch values are semitones separated by ';'.
 *   Each value is one voice. Volume per pitch = (values / 2) + 0.5,
 *   so "--pitch 7;-5" = 2 voices at volume 1.5, "--pitch 7;5;5" = 3 voices at volume 2.
 *   Peaks are soft-limited so the louder mix never harshly clips.
 *
 *   Optional --pitchtype <default|a17|rubberband> (default = "default"):
 *     default    = built-in resample + WSOLA, no external tools, volume = (values / 2) + 0.5
 *     a17        = sox "pitch" per voice,  volume = values (requires sox)
 *     rubberband = ffmpeg rubberband filter per voice, volume = values (requires ffmpeg)
 *
 *   Pitch is shifted WITHOUT changing speed/duration
 *   (windowed-sinc resample + WSOLA time-stretch).
 *
 * Compile (Linux/macOS/Git Bash):
 *   g++ -O2 -o chuanmultipitch chuanmultipitch.cpp
 *   clang++ -O2 -o chuanmultipitch chuanmultipitch.cpp
 */

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <cstdint>
#include <string>
#include <vector>
#include <fstream>
#include <algorithm>

#ifdef _WIN32
#include <process.h>
#else
#include <unistd.h>
#endif

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

static const char *HELP =
    "{ MULTIPITCH BY CHUAN}\n"
    "\n"
    "HOW TO USE:\n"
    "\n"
    "YOU NEED 4 ARGUMENTS LIKE:\n"
    "\n"
    "[1] INPUT (.wav ONLY)\n"
    "\n"
    "[2] OUTPUT (.wav ONLY)\n"
    "\n"
    "[3] FLAG (--pitch)\n"
    "\n"
    "[4] VALUE (semitones -120..120, decimals ok, example: --pitch 7.5;-2.5)\n"
    "\n"
    "[5] OPTIONAL FLAG (--pitchtype default|a17|rubberband, default = default)\n"
    "    default    = built-in resample + WSOLA, volume = (values / 2) + 0.5\n"
    "    a17        = sox pitch per voice,        volume = values (needs sox)\n"
    "    rubberband = ffmpeg rubberband per voice, volume = values (needs ffmpeg)\n"
    "\n"
    "volume per pitch: default = (values / 2) + 0.5, a17/rubberband = values\n"
    "pitch shifts WITHOUT speed change\n"
    "\n"
    "example:\n"
    "\n"
    "chuanmultipitch.exe input.wav output.wav --pitch 7;-5\n"
    "\n"
    "{ MULTIPITCH BY CHUAN}";

static uint32_t rd32(const uint8_t *b)
{
    return uint32_t(b[0]) | (uint32_t(b[1]) << 8) |
           (uint32_t(b[2]) << 16) | (uint32_t(b[3]) << 24);
}

static uint16_t rd16(const uint8_t *b)
{
    return uint16_t(uint16_t(b[0]) | (uint16_t(b[1]) << 8));
}

static void wr32(uint8_t *b, uint32_t v)
{
    b[0] = uint8_t(v & 0xFF);
    b[1] = uint8_t((v >> 8) & 0xFF);
    b[2] = uint8_t((v >> 16) & 0xFF);
    b[3] = uint8_t((v >> 24) & 0xFF);
}

static void wr16(uint8_t *b, uint16_t v)
{
    b[0] = uint8_t(v & 0xFF);
    b[1] = uint8_t((v >> 8) & 0xFF);
}

struct WavInfo {
    int channels = 0;
    int sampleRate = 0;
    int bits = 0;
    int fmt = 0;             /* 1 = PCM, 3 = float */
    long dataOffset = -1;
    long dataSize = 0;
};

static bool parseWav(const std::vector<uint8_t> &buf, WavInfo &info)
{
    if (buf.size() < 44) return false;
    if (memcmp(buf.data(), "RIFF", 4) != 0) return false;
    if (memcmp(buf.data() + 8, "WAVE", 4) != 0) return false;

    long pos = 12;
    while (pos + 8 <= (long)buf.size()) {
        char id[5];
        memcpy(id, buf.data() + pos, 4);
        id[4] = 0;
        long csize = (long)rd32(buf.data() + pos + 4);
        long body = pos + 8;
        if (csize < 0 || body + csize > (long)buf.size()) return false;

        if (strcmp(id, "fmt ") == 0) {
            if (csize < 16) return false;
            info.fmt = rd16(buf.data() + body);
            info.channels = rd16(buf.data() + body + 2);
            info.sampleRate = rd32(buf.data() + body + 4);
            info.bits = rd16(buf.data() + body + 14);
        } else if (strcmp(id, "data") == 0) {
            if (info.dataOffset < 0) {
                info.dataOffset = body;
                info.dataSize = csize;
            }
        }
        pos = body + csize + (csize & 1);
    }

    if (info.dataOffset < 0) return false;
    if (info.channels < 1 || info.channels > 2) return false;
    if (info.sampleRate < 1) return false;
    if (info.fmt != 1 && info.fmt != 3) return false;
    if (info.fmt == 1 && info.bits != 8 && info.bits != 16) return false;
    if (info.fmt == 3 && info.bits != 32) return false;
    return true;
}

static std::vector<double> decodePcm(const std::vector<uint8_t> &buf,
                                     const WavInfo &info, long frames)
{
    int ch = info.channels;
    std::vector<double> pcm((size_t)frames * ch);
    const uint8_t *d = buf.data() + info.dataOffset;

    if (info.fmt == 1 && info.bits == 16) {
        for (long i = 0; i < frames * ch; i++) {
            int16_t s = int16_t(rd16(d + i * 2));
            pcm[i] = double(s) / 32768.0;
        }
    } else if (info.fmt == 1 && info.bits == 8) {
        for (long i = 0; i < frames * ch; i++)
            pcm[i] = (double(d[i]) - 128.0) / 128.0;
    } else { /* float 32 */
        for (long i = 0; i < frames * ch; i++) {
            uint32_t u = rd32(d + i * 4);
            float f;
            memcpy(&f, &u, 4);
            pcm[i] = double(f);
        }
    }
    return pcm;
}

static bool writeWav(const std::string &path, int channels, int sampleRate,
                     const std::vector<double> &mix, long frames)
{
    uint8_t hdr[44] = {0};
    memcpy(hdr, "RIFF", 4);
    wr32(hdr + 4, uint32_t(36 + frames * channels * 2));
    memcpy(hdr + 8, "WAVE", 4);
    memcpy(hdr + 12, "fmt ", 4);
    wr32(hdr + 16, 16);
    wr16(hdr + 20, 1);
    wr16(hdr + 22, uint16_t(channels));
    wr32(hdr + 24, uint32_t(sampleRate));
    wr32(hdr + 28, uint32_t(sampleRate * channels * 2));
    wr16(hdr + 32, uint16_t(channels * 2));
    wr16(hdr + 34, 16);
    memcpy(hdr + 36, "data", 4);
    wr32(hdr + 40, uint32_t(frames * channels * 2));

    std::ofstream f(path, std::ios::binary | std::ios::trunc);
    if (!f) return false;
    f.write(reinterpret_cast<const char *>(hdr), 44);

    std::vector<uint8_t> data((size_t)frames * channels * 2);
    for (long i = 0; i < frames * channels; i++) {
        double v = mix[i];
        if (v > 1.0) v = 1.0;
        if (v < -1.0) v = -1.0;
        int16_t s = int16_t(std::lround(v * 32767.0));
        wr16(data.data() + i * 2, uint16_t(s));
    }
    f.write(reinterpret_cast<const char *>(data.data()), (std::streamsize)data.size());
    f.close();
    return (bool)f;
}

/* run an external command (sox/ffmpeg); returns the command exit code */
static int runCmd(const std::string &cmd)
{
    return system(cmd.c_str());
}

/* load a WAV file into a double buffer (used for external-tool temp files) */
static bool loadWav(const std::string &path, WavInfo &info,
                    std::vector<double> &pcm, long &frames)
{
    std::ifstream f(path, std::ios::binary);
    if (!f) return false;
    std::vector<uint8_t> buf((std::istreambuf_iterator<char>(f)),
                             std::istreambuf_iterator<char>());
    f.close();
    if (!parseWav(buf, info)) return false;
    frames = info.dataSize / (info.bits / 8) / info.channels;
    pcm = decodePcm(buf, info, frames);
    return true;
}

/* ------------------------------------------------------------------ */
/* DSP: windowed-sinc resampler                                        */
/* ------------------------------------------------------------------ */

static std::vector<double> resample(const std::vector<double> &in,
                                    double ratio)
{
    const long half = 32; /* sinc taps per side */
    long n = (long)in.size();
    long on = std::max<long>(1, (long)std::floor(n * ratio + 0.5));
    std::vector<double> out((size_t)on, 0.0);

    double fc = 0.5 * (ratio < 1.0 ? ratio : 1.0); /* anti-alias cutoff */
    if (fc <= 0.0) fc = 0.01;

    for (long j = 0; j < on; j++) {
        double t = ((double)j + 0.5) / ratio - 0.5;
        long i = (long)std::floor(t);
        double d = t - (double)i;
        double acc = 0.0;
        for (long k = -half; k < half; k++) {
            long idx = i + k;
            if (idx < 0 || idx >= n) continue;
            double x = d - (double)k;
            double a = 2.0 * M_PI * fc * x;
            double s = (a == 0.0) ? 1.0 : sin(a) / a;
            double w = 0.5 * (1.0 + cos(M_PI * x / (double)half));
            acc += in[idx] * s * w;
        }
        out[j] = acc * (2.0 * fc);
    }
    return out;
}

/* ------------------------------------------------------------------ */
/* DSP: WSOLA time-stretch (duration scale = alpha, pitch unchanged)   */
/* ------------------------------------------------------------------ */

static std::vector<double> wsola(const std::vector<double> &in, double alpha)
{
    const long Hs = 256;  /* synthesis hop */
    const long W = 1024;  /* window length (4 * Hs) */
    const long W2 = W / 2;
    const long delta = 96;/* search range */
    const long Lc = 128;  /* correlation length */
    long n = (long)in.size();
    long on = std::max<long>(1, (long)std::floor(n * alpha + 0.5));

    std::vector<double> out((size_t)(on + 2 * W), 0.0);
    std::vector<double> win((size_t)W);
    for (long k = 0; k < W; k++)
        win[k] = 0.5 * (1.0 - cos(2.0 * M_PI * k / (double)(W - 1)));

    /* first frame: analysis center = synthesis center = W2 */
    for (long k = 0; k < W && k < n; k++)
        out[(size_t)k] += in[(size_t)k] * win[(size_t)k];

    /* absolute synthesis grid => tempo-exact stretch by alpha */
    long sc = W2; /* synthesis center */
    long buflen = on + 2 * W;
    while (true) {
        sc += Hs;
        if (sc >= on + W2) break;

        long ideal = (long)std::floor((double)sc / alpha);
        long lo = std::max<long>(0, ideal - delta);
        long hi = ideal + delta;
        hi = std::min<long>(hi, n - W2);
        if (hi > ideal + delta) hi = ideal + delta;
        if (hi < lo) hi = lo;

        long best = ideal;
        double bestc = -1e300;
        long ref = sc - W2;
        for (long pos = lo; pos <= hi; pos++) {
            double c = 0.0;
            for (long m = 0; m < Lc; m++) {
                long ai = pos - W2 + m;
                long oi = ref + m;
                if (ai < 0 || ai >= n || oi < 0 || oi >= buflen) break;
                c += in[(size_t)ai] * out[(size_t)oi];
            }
            if (c > bestc) { bestc = c; best = pos; }
        }

        for (long k = 0; k < W; k++) {
            long ai = best - W2 + k;
            long oi = sc - W2 + k;
            if (ai < 0 || ai >= n || oi < 0 || oi >= buflen) continue;
            out[(size_t)oi] += in[(size_t)ai] * win[(size_t)k];
        }
    }

    for (long j = 0; j < on; j++) out[(size_t)j] *= 0.5; /* hann 4x OLA gain */
    out.resize((size_t)on);
    return out;
}

/* ------------------------------------------------------------------ */
/* Voice pipeline: resample (pitch) then WSOLA (restore duration)      */
/* ------------------------------------------------------------------ */

static bool addVoice(std::vector<double> &mix, long frames, int channels,
                     const std::vector<double> &pcm, double semitones)
{
    double f = std::pow(2.0, semitones / 12.0);
    double ratio = 1.0 / f;
    long n = frames;

    for (int c = 0; c < channels; c++) {
        std::vector<double> ch((size_t)n);
        for (long j = 0; j < n; j++) ch[j] = pcm[(size_t)j * channels + c];

        std::vector<double> r = resample(ch, ratio);
        std::vector<double> s = wsola(r, f);

        long lim = std::min<long>((long)s.size(), n);
        for (long j = 0; j < lim; j++)
            mix[(size_t)j * channels + c] += s[j];
    }
    return true;
}

/* ------------------------------------------------------------------ */
/* main                                                                */
/* ------------------------------------------------------------------ */

int main(int argc, char **argv)
{
    bool nlogs = false;
    std::string args[4];
    int nargs = 0;
    std::string pitchtype = "default";

    for (int i = 1; i < argc; i++) {
        std::string a = argv[i];
        if (a == "--help" || a == "-h") {
            printf("%s\n", HELP);
            return 0;
        }
        if (a == "--nlogs") {
            nlogs = true;
            continue;
        }
        if (a == "--pitchtype") {
            if (i + 1 < argc) pitchtype = argv[++i];
            continue;
        }
        if (nargs < 4) args[nargs++] = a;
    }

    if (pitchtype != "default" && pitchtype != "a17" && pitchtype != "rubberband") {
        if (!nlogs) printf("%s\n\nERROR: Invalid pitchtype.\n", HELP);
        return 1;
    }

    if (nargs < 4) {
        if (!nlogs) printf("%s\n\nERROR: Not enough arguments. You need 4 arguments.\n", HELP);
        return 1;
    }

    const std::string &input = args[0];
    const std::string &output = args[1];
    const std::string &flag = args[2];
    const std::string &value = args[3];

    if (flag != "--pitch") {
        if (!nlogs) printf("%s\n\nERROR: Invalid flag.\n", HELP);
        return 1;
    }

    std::vector<double> pitches;
    {
        std::string buf = value;
        size_t start = 0;
        while (start < buf.size()) {
            size_t end = buf.find(';', start);
            if (end == std::string::npos) end = buf.size();
            std::string tok = buf.substr(start, end - start);
            while (!tok.empty() && (tok.front() == ' ' || tok.front() == '\t')) tok.erase(tok.begin());
            while (!tok.empty() && (tok.back() == ' ' || tok.back() == '\t')) tok.pop_back();
            if (!tok.empty()) {
                char *endp = nullptr;
                double v = std::strtod(tok.c_str(), &endp);
                if (endp == tok.c_str() || *endp != 0 || v < -120.0 || v > 120.0) {
                    if (!nlogs) printf("%s\n\nERROR: Invalid value.\n", HELP);
                    return 1;
                }
                pitches.push_back(v);
            }
            start = end + 1;
        }
    }
    if (pitches.empty()) {
        if (!nlogs) printf("%s\n\nERROR: Invalid value.\n", HELP);
        return 1;
    }

    std::ifstream fi(input, std::ios::binary);
    if (!fi) {
        if (!nlogs) printf("%s\n\nERROR: Invalid input.\n", HELP);
        return 1;
    }
    std::vector<uint8_t> buf((std::istreambuf_iterator<char>(fi)),
                             std::istreambuf_iterator<char>());
    fi.close();

    WavInfo info;
    if (!parseWav(buf, info)) {
        if (!nlogs) printf("%s\n\nERROR: Not a supported WAV file (use 8/16-bit PCM or 32-bit float).\n", HELP);
        return 1;
    }

    long frames = info.dataSize / (info.bits / 8) / info.channels;
    long total = frames * info.channels;

    std::vector<double> pcm = decodePcm(buf, info, frames);
    std::vector<double> mix((size_t)total, 0.0);

    double inRms = 0.0;
    for (double v : pcm) inRms += v * v;
    inRms = std::sqrt(inRms / (double)total);

    int ok = 1;
    int ext = 0;
    if (pitchtype == "default") {
        for (double p : pitches) {
            if (!addVoice(mix, frames, info.channels, pcm, p)) {
                ok = 0;
                break;
            }
        }
    } else {
        for (int i = 0; i < (int)pitches.size() && ok; i++) {
            std::string tmp = "chuanmp." + std::to_string(GETPID()) + "." +
                              std::to_string(i) + ".wav";
            std::string cmd;
            if (pitchtype == "a17") {
                long cents = std::lround(pitches[i] * 100.0);
                cmd = "sox -q \"" + input + "\" \"" + tmp + "\" pitch " +
                      std::to_string(cents) + " 10 10 10";
            } else {
                double ratio = std::pow(2.0, pitches[i] / 12.0);
                cmd = "ffmpeg -hide_banner -loglevel error -y -i \"" + input +
                      "\" -af \"rubberband=pitch=" + std::to_string(ratio) +
                      ":window=long:transients=crisp:smoothing=2.14748e+09/4.9:"
                      "pitchq=speed:detector=percussive\" \"" + tmp + "\"";
            }
            if (runCmd(cmd) != 0) { ext = 1; ok = 0; break; }
            WavInfo ti;
            std::vector<double> tp;
            long tf = 0;
            if (!loadWav(tmp, ti, tp, tf)) { remove(tmp.c_str()); ext = 1; ok = 0; break; }
            remove(tmp.c_str());
            if (ti.channels != info.channels) { ext = 1; ok = 0; break; }
            long lim = tf < frames ? tf : frames;
            for (long j = 0; j < lim * info.channels; j++)
                mix[(size_t)j] += tp[(size_t)j];
        }
    }
    if (!ok) {
        if (!nlogs) {
            if (ext) printf("%s\n\nERROR: External pitch tool (sox/ffmpeg) failed.\n", HELP);
            else printf("%s\n\nERROR: Out of memory.\n", HELP);
        }
        return 1;
    }

    /* volume per pitch: default = (values / 2) + 0.5, a17/rubberband = values;
       scale the mix so its loudness is that many x the input; a soft limiter
       catches the louder peaks so it never harshly clips */
    double vol = (pitchtype == "default")
                 ? ((double)pitches.size() / 2.0 + 0.5) : (double)pitches.size();
    double rms = 0.0;
    for (double v : mix) rms += v * v;
    rms = std::sqrt(rms / (double)total);
    if (inRms > 0.0 && rms > 0.0) {
        double g = (inRms * vol) / rms;
        const double T = 0.95; /* soft-limiter threshold */
        for (double &x : mix) {
            double v = x * g;
            double a = std::fabs(v);
            if (a > T) {
                double over = (a - T) / (1.0 - T);
                double lim = T + (1.0 - T) * (over / (1.0 + over));
                v = (v < 0.0) ? -lim : lim;
            }
            x = v;
        }
    }

    if (!writeWav(output, info.channels, info.sampleRate, mix, frames)) {
        if (!nlogs) printf("%s\n\nERROR: Cannot write output.\n", HELP);
        return 1;
    }

    if (!nlogs) printf("Done: %s\n", output.c_str());
    return 0;
}
