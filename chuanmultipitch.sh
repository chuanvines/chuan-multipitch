#!/usr/bin/env bash
#
# { MULTIPITCH BY CHUAN}
#
# Self-contained, no-sox wrapper. The pure-C implementation is embedded
# below; this script extracts it to a temp dir, compiles it with the
# local C compiler (gcc/clang/cc) and runs it.
#
# RUN directly from the internet (nothing is saved to disk):
#   curl -fsSL https://your-host/chuanmultipitch.sh | bash -s in.wav out.wav --pitch '7;8'
#
# RUN after downloading:
#   chmod +x chuanmultipitch.sh
#   ./chuanmultipitch.sh input.wav output.wav --pitch '7;8'
#
# NOTE: quote the semicolons so the shell does not split them:
#   --pitch '7;8'     2 voices
#   --pitch '7;8;-5'  3 voices
#   All voices are mixed and auto-normalized to the input loudness (RMS).
#   Pitch shifts WITHOUT speed change. Input/output .wav only.

set -u

TMP="${TMPDIR:-/tmp}"
WORK="$(mktemp -d "${TMP}/chuanmp.XXXXXX")" || { echo "ERROR: cannot create temp dir" >&2; exit 1; }
trap 'rm -rf "$WORK"' EXIT

SRC="$WORK/chuanmultipitch.c"
BIN="$WORK/chuanmultipitch"

cat > "$SRC" <<'__CHUANMULTIPITCH_C_END__'
/*
 * { MULTIPITCH BY CHUAN }
 *
 * Pure C implementation. No sox, no external libraries.
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
 *   Pitch is shifted WITHOUT changing speed/duration
 *   (windowed-sinc resample + WSOLA time-stretch).
 *
 * Compile (Linux/macOS/Git Bash):
 *   gcc -O2 -o chuanmultipitch chuanmultipitch.c -lm
 *   clang -O2 -o chuanmultipitch chuanmultipitch.c -lm
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <stdint.h>

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
    "volume per pitch = (values / 2) + 0.5 (--pitch 7;-5 = volume 1.5, --pitch 7;5;5 = volume 2)\n"
    "pitch shifts WITHOUT speed change\n"
    "\n"
    "example:\n"
    "\n"
    "chuanmultipitch.exe input.wav output.wav --pitch 7;-5\n"
    "\n"
    "{ MULTIPITCH BY CHUAN}";

/* ------------------------------------------------------------------ */
/* WAV helpers                                                         */
/* ------------------------------------------------------------------ */

typedef struct {
    int channels;
    int sampleRate;
    int bits;
    int fmt;            /* 1 = PCM, 3 = float */
    long dataOffset;
    long dataSize;
} WavInfo;

static uint32_t rd32(const unsigned char *b)
{
    return (uint32_t)b[0] | ((uint32_t)b[1] << 8) |
           ((uint32_t)b[2] << 16) | ((uint32_t)b[3] << 24);
}

static uint16_t rd16(const unsigned char *b)
{
    return (uint16_t)((uint16_t)b[0] | ((uint16_t)b[1] << 8));
}

static void wr32(unsigned char *b, uint32_t v)
{
    b[0] = (unsigned char)(v & 0xFF);
    b[1] = (unsigned char)((v >> 8) & 0xFF);
    b[2] = (unsigned char)((v >> 16) & 0xFF);
    b[3] = (unsigned char)((v >> 24) & 0xFF);
}

static void wr16(unsigned char *b, uint16_t v)
{
    b[0] = (unsigned char)(v & 0xFF);
    b[1] = (unsigned char)((v >> 8) & 0xFF);
}

static int parseWav(const unsigned char *buf, long size, WavInfo *info)
{
    if (size < 44) return 0;
    if (memcmp(buf, "RIFF", 4) != 0) return 0;
    if (memcmp(buf + 8, "WAVE", 4) != 0) return 0;

    info->channels = 0;
    info->sampleRate = 0;
    info->bits = 0;
    info->fmt = 0;
    info->dataOffset = -1;
    info->dataSize = 0;

    long pos = 12;
    while (pos + 8 <= size) {
        char id[5];
        memcpy(id, buf + pos, 4);
        id[4] = 0;
        long csize = (long)rd32(buf + pos + 4);
        long body = pos + 8;
        if (csize < 0 || body + csize > size) return 0;

        if (strcmp(id, "fmt ") == 0) {
            if (csize < 16) return 0;
            info->fmt = (int)rd16(buf + body);
            info->channels = (int)rd16(buf + body + 2);
            info->sampleRate = (int)rd32(buf + body + 4);
            info->bits = (int)rd16(buf + body + 14);
        } else if (strcmp(id, "data") == 0) {
            if (info->dataOffset < 0) {
                info->dataOffset = body;
                info->dataSize = csize;
            }
        }
        pos = body + csize + (csize & 1);
    }

    if (info->dataOffset < 0) return 0;
    if (info->channels < 1 || info->channels > 2) return 0;
    if (info->sampleRate < 1) return 0;
    if (info->fmt != 1 && info->fmt != 3) return 0;
    if (info->fmt == 1 && info->bits != 8 && info->bits != 16) return 0;
    if (info->fmt == 3 && info->bits != 32) return 0;
    return 1;
}

static void decodePcm(const unsigned char *buf, long size, const WavInfo *info,
                      double *pcm, long frames)
{
    int ch = info->channels;
    if (info->fmt == 1 && info->bits == 16) {
        for (long i = 0; i < frames * ch; i++)
            pcm[i] = (double)(int16_t)((uint16_t)rd16(buf + i * 2)) / 32768.0;
    } else if (info->fmt == 1 && info->bits == 8) {
        for (long i = 0; i < frames * ch; i++)
            pcm[i] = ((double)buf[i] - 128.0) / 128.0;
    } else { /* float 32 */
        for (long i = 0; i < frames * ch; i++) {
            uint32_t u = (uint32_t)buf[i * 4] | ((uint32_t)buf[i * 4 + 1] << 8) |
                         ((uint32_t)buf[i * 4 + 2] << 16) | ((uint32_t)buf[i * 4 + 3] << 24);
            float f;
            memcpy(&f, &u, 4);
            pcm[i] = (double)f;
        }
    }
    (void)size;
}

static int writeWav(const char *path, int channels, int sampleRate,
                    const double *mix, long frames)
{
    unsigned char hdr[44];
    memset(hdr, 0, 44);
    memcpy(hdr, "RIFF", 4);
    wr32(hdr + 4, (uint32_t)(36 + frames * channels * 2));
    memcpy(hdr + 8, "WAVE", 4);
    memcpy(hdr + 12, "fmt ", 4);
    wr32(hdr + 16, 16);
    wr16(hdr + 20, 1);
    wr16(hdr + 22, (uint16_t)channels);
    wr32(hdr + 24, (uint32_t)sampleRate);
    wr32(hdr + 28, (uint32_t)(sampleRate * channels * 2));
    wr16(hdr + 32, (uint16_t)(channels * 2));
    wr16(hdr + 34, 16);
    memcpy(hdr + 36, "data", 4);
    wr32(hdr + 40, (uint32_t)(frames * channels * 2));

    FILE *f = fopen(path, "wb");
    if (!f) return 0;
    int ok = 1;
    if (fwrite(hdr, 1, 44, f) != 44) ok = 0;

    unsigned char *data = (unsigned char *)malloc((size_t)frames * channels * 2);
    if (!data) { ok = 0; }
    if (ok) {
        for (long i = 0; i < frames * channels; i++) {
            double v = mix[i];
            if (v > 1.0) v = 1.0;
            if (v < -1.0) v = -1.0;
            int16_t s = (int16_t)lrint(v * 32767.0);
            wr16(data + i * 2, (uint16_t)s);
        }
        if (fwrite(data, 1, (size_t)frames * channels * 2, f) != (size_t)frames * channels * 2) ok = 0;
        free(data);
    }
    if (fclose(f) != 0) ok = 0;
    if (!ok) remove(path);
    return ok;
}

/* ------------------------------------------------------------------ */
/* DSP: windowed-sinc resampler                                        */
/* ------------------------------------------------------------------ */

static double *resample(const double *in, long n, double ratio, long *outN)
{
    const long half = 32; /* sinc taps per side */
    long on = (long)floor(n * ratio + 0.5);
    if (on < 1) on = 1;
    double *out = (double *)calloc((size_t)on, sizeof(double));
    if (!out) return NULL;

    double fc = 0.5 * (ratio < 1.0 ? ratio : 1.0); /* anti-alias cutoff */
    if (fc <= 0.0) fc = 0.01;

    for (long j = 0; j < on; j++) {
        double t = ((double)j + 0.5) / ratio - 0.5;
        long i = (long)floor(t);
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
    *outN = on;
    return out;
}

/* ------------------------------------------------------------------ */
/* DSP: WSOLA time-stretch (duration scale = alpha, pitch unchanged)   */
/* ------------------------------------------------------------------ */

static double *wsola(const double *in, long n, double alpha, long *outN)
{
    const long Hs = 256;  /* synthesis hop */
    const long W = 1024;  /* window length (4 * Hs) */
    const long W2 = W / 2;
    const long delta = 96;/* search range */
    const long Lc = 128;  /* correlation length */
    long on = (long)floor(n * alpha + 0.5);
    if (on < 1) on = 1;

    double *out = (double *)calloc((size_t)(on + 2 * W), sizeof(double));
    if (!out) return NULL;
    double *win = (double *)malloc((size_t)W * sizeof(double));
    if (!win) { free(out); return NULL; }
    for (long k = 0; k < W; k++)
        win[k] = 0.5 * (1.0 - cos(2.0 * M_PI * k / (double)(W - 1)));

    /* first frame: analysis center = synthesis center = W2 */
    for (long k = 0; k < W && k < n; k++)
        out[k] += in[k] * win[k];

    /* absolute synthesis grid => tempo-exact stretch by alpha */
    long sc = W2; /* synthesis center */
    while (1) {
        sc += Hs;
        if (sc >= on + W2) break;

        long ideal = (long)floor((double)sc / alpha);
        long lo = ideal - delta;
        if (lo < 0) lo = 0;
        long hi = ideal + delta;
        if (hi + W2 > n) hi = n - W2;
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
                if (ai < 0 || ai >= n || oi < 0 || oi >= on + 2 * W) break;
                c += in[ai] * out[oi];
            }
            if (c > bestc) { bestc = c; best = pos; }
        }

        for (long k = 0; k < W; k++) {
            long ai = best - W2 + k;
            long oi = sc - W2 + k;
            if (ai < 0 || ai >= n || oi < 0 || oi >= on + 2 * W) continue;
            out[oi] += in[ai] * win[k];
        }
    }

    free(win);
    for (long j = 0; j < on; j++) out[j] *= 0.5; /* hann 4x OLA gain */
    *outN = on;
    return out;
}

/* ------------------------------------------------------------------ */
/* Voice pipeline: resample (pitch) then WSOLA (restore duration)      */
/* ------------------------------------------------------------------ */

static int addVoice(double *mix, long frames, int channels,
                    const double *pcm, double semitones)
{
    double f = pow(2.0, semitones / 12.0);
    double ratio = 1.0 / f;
    long n = frames;

    for (int c = 0; c < channels; c++) {
        double *ch = (double *)malloc((size_t)n * sizeof(double));
        if (!ch) return 0;
        for (long j = 0; j < n; j++) ch[j] = pcm[j * channels + c];

        long rn = 0, sn = 0;
        double *r = resample(ch, n, ratio, &rn);
        if (!r) { free(ch); return 0; }
        double *s = wsola(r, rn, f, &sn);
        free(r);
        if (!s) { free(ch); return 0; }

        long lim = sn < n ? sn : n;
        for (long j = 0; j < lim; j++)
            mix[j * channels + c] += s[j];
        free(s);
        free(ch);
    }
    return 1;
}

/* ------------------------------------------------------------------ */
/* main                                                                */
/* ------------------------------------------------------------------ */

int main(int argc, char **argv)
{
    int nlogs = 0;
    char *args[4];
    int nargs = 0;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--help") == 0 || strcmp(argv[i], "-h") == 0) {
            printf("%s\n", HELP);
            return 0;
        }
        if (strcmp(argv[i], "--nlogs") == 0) {
            nlogs = 1;
            continue;
        }
        if (nargs < 4) args[nargs++] = argv[i];
    }

    if (nargs < 4) {
        if (!nlogs) { printf("%s\n\nERROR: Not enough arguments. You need 4 arguments.\n", HELP); }
        return 1;
    }

    const char *input = args[0];
    const char *output = args[1];
    const char *flag = args[2];
    const char *value = args[3];

    if (strcmp(flag, "--pitch") != 0) {
        if (!nlogs) { printf("%s\n\nERROR: Invalid flag.\n", HELP); }
        return 1;
    }

    double pitches[256];
    int np = 0;
    {
        char buf[4096];
        snprintf(buf, sizeof(buf), "%s", value);
        char *tok = strtok(buf, ";");
        while (tok && np < 256) {
            char *end = NULL;
            double v = strtod(tok, &end);
            while (end && *end && *end == ' ') end++;
            if (end == tok || (end && *end != 0) || v < -120.0 || v > 120.0) {
                if (!nlogs) { printf("%s\n\nERROR: Invalid value.\n", HELP); }
                return 1;
            }
            pitches[np++] = v;
            tok = strtok(NULL, ";");
        }
    }
    if (np == 0) {
        if (!nlogs) { printf("%s\n\nERROR: Invalid value.\n", HELP); }
        return 1;
    }

    FILE *fi = fopen(input, "rb");
    if (!fi) {
        if (!nlogs) { printf("%s\n\nERROR: Invalid input.\n", HELP); }
        return 1;
    }
    fseek(fi, 0, SEEK_END);
    long fsize = ftell(fi);
    fseek(fi, 0, SEEK_SET);
    unsigned char *buf = (unsigned char *)malloc((size_t)fsize);
    if (!buf || fread(buf, 1, (size_t)fsize, fi) != (size_t)fsize) {
        fclose(fi);
        free(buf);
        if (!nlogs) { printf("%s\n\nERROR: Cannot read input.\n", HELP); }
        return 1;
    }
    fclose(fi);

    WavInfo info;
    if (!parseWav(buf, fsize, &info)) {
        free(buf);
        if (!nlogs) { printf("%s\n\nERROR: Not a supported WAV file (use 8/16-bit PCM or 32-bit float).\n", HELP); }
        return 1;
    }

    long frames = info.dataSize / (info.bits / 8) / info.channels;
    long total = frames * info.channels;

    double *pcm = (double *)malloc((size_t)total * sizeof(double));
    double *mix = (double *)calloc((size_t)total, sizeof(double));
    if (!pcm || !mix) {
        free(buf); free(pcm); free(mix);
        if (!nlogs) { printf("%s\n\nERROR: Out of memory.\n", HELP); }
        return 1;
    }
    decodePcm(buf + info.dataOffset, info.dataSize, &info, pcm, frames);
    free(buf);

    /* remember the input loudness so the output can be matched to it */
    double inRms = 0.0;
    for (long i = 0; i < total; i++) {
        double v = pcm[i];
        inRms += v * v;
    }
    inRms = sqrt(inRms / (double)total);

    int ok = 1;
    for (int i = 0; i < np && ok; i++)
        ok = addVoice(mix, frames, info.channels, pcm, pitches[i]);
    free(pcm);

    if (!ok) {
        free(mix);
        if (!nlogs) { printf("%s\n\nERROR: Out of memory.\n", HELP); }
        return 1;
    }

    /* volume per pitch = (values / 2) + 0.5: scale the mix so its loudness is
       (np / 2 + 0.5) x the input (--pitch 7;-5 = volume 1.5, --pitch 7;5;5 = volume 2);
       a soft limiter catches the louder peaks so it never harshly clips */
    double rms = 0.0;
    for (long i = 0; i < total; i++)
        rms += mix[i] * mix[i];
    rms = sqrt(rms / (double)total);
    if (inRms > 0.0 && rms > 0.0) {
        double g = (inRms * ((double)np / 2.0 + 0.5)) / rms;
        const double T = 0.95; /* soft-limiter threshold */
        for (long i = 0; i < total; i++) {
            double v = mix[i] * g;
            double a = fabs(v);
            if (a > T) {
                double over = (a - T) / (1.0 - T);
                double lim = T + (1.0 - T) * (over / (1.0 + over));
                v = (v < 0.0) ? -lim : lim;
            }
            mix[i] = v;
        }
    }

    if (!writeWav(output, info.channels, info.sampleRate, mix, frames)) {
        free(mix);
        if (!nlogs) { printf("%s\n\nERROR: Cannot write output.\n", HELP); }
        return 1;
    }
    free(mix);

    if (!nlogs) printf("Done: %s\n", output);
    return 0;
}
__CHUANMULTIPITCH_C_END__

CC=""
for c in gcc clang cc; do
  if command -v "$c" >/dev/null 2>&1; then CC="$c"; break; fi
done
if [ -z "$CC" ]; then
  echo "ERROR: No C compiler (gcc/clang/cc) found." >&2
  exit 1
fi

if ! "$CC" -O2 -o "$BIN" "$SRC" -lm >/dev/null 2>&1; then
  echo "ERROR: failed to compile embedded chuanmultipitch.c" >&2
  exit 1
fi

"$BIN" "$@"
exit $?
