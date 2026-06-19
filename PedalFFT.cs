// Pedal FFT 2.0 – Spectral drum processor for ReBuzz
// Assembly: Pedal FFT.NET.dll  →  <ReBuzz>\Gear\Effects\
//
// Three interlocking spectral processing modules:
//
//   C – Per-band processing
//       The spectrum is divided into four frequency bands:
//         Band 0 – Sub/Kick   (0 – 200 Hz)
//         Band 1 – Body       (200 – 1000 Hz)
//         Band 2 – Crack      (1000 – 6000 Hz)
//         Band 3 – Air        (6000+ Hz)
//       Each band receives independently scaled drive (Low Drive → High Drive,
//       interpolated) and level (Band Tilt).  A 2nd-harmonic exciter (Band Harm)
//       adds spectral content synthesised from the pre-drive spectrum.
//
//   D – Transient detection
//       Spectral flux (sum of positive inter-frame magnitude increases, normalised
//       by bin count) classifies each FFT frame as attack or sustain.  Processing
//       weight is set to Atk Weight on attack frames and Sus Weight on sustain
//       frames, making C and E processing rhythmically gated to drum hits.
//
//   E – Comb resonance synthesis
//       On each detected attack the dominant spectral bin is identified.  Harmonic
//       partials at 2f…6f are synthesised and added to the spectrum, scaled by
//       Resonance and the current comb energy level.  The comb decays exponentially
//       (Comb Decay) between hits.  Spread detunes upper harmonics progressively
//       for metallic/bell-like timbres vs. a clean pitched ring.
//
// Processing chain (positive bins only):
//   FFT → D (flux) → C (drive + tilt, weighted) → C (harmonics) → E (comb) → IFFT
//
// ReBuzz passes audio at ±32768. Samples are normalised before the FFT and
// denormalised after IFFT so all spectral magnitudes operate in unit scale.
// Default FFT size is 512 samples (~11.6 ms frame / 5.8 ms latency at 44100 Hz)
// for better time resolution on drum transients.

using System;
using Buzz.MachineInterface;

namespace WDE.PedalFFT
{
    [MachineDecl(
        Name        = "Pedal FFT",
        ShortName   = "PFFT",
        Author      = "WDE",
        MaxTracks   = 0,
        InputCount  = 1,
        OutputCount = 1)]
    public class PedalFFTMachine : IBuzzMachine
    {
        readonly IBuzzMachineHost host;

        // ── C: Per-band processing ────────────────────────────────────────────

        [ParameterDecl(MinValue = 0, MaxValue = 200, DefValue = 50,
            Name = "Low Drive",
            Description = "Saturation (tanh soft-clip) for the low bands (sub/kick, body). " +
                          "Drive is interpolated linearly across all four bands between " +
                          "Low Drive (band 0) and High Drive (band 3).")]
        public int LowDrive { get; set; }

        [ParameterDecl(MinValue = 0, MaxValue = 200, DefValue = 50,
            Name = "High Drive",
            Description = "Saturation (tanh soft-clip) for the high bands (crack/presence, air). " +
                          "Works with Low Drive — set differently to saturate specific regions.")]
        public int HighDrive { get; set; }

        [ParameterDecl(MinValue = 0, MaxValue = 200, DefValue = 100,
            Name = "Band Tilt",
            Description = "Redistribute gain across the four frequency bands. " +
                          "100 = flat. >100 boosts high bands (bright/angular). " +
                          "<100 boosts low bands (dark/heavy).")]
        public int BandTilt { get; set; }

        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 25,
            Name = "Band Harm",
            Description = "Harmonic exciter: adds 2nd harmonic content to each band " +
                          "independently, synthesised from the pre-drive spectrum. " +
                          "Scaled by Atk/Sus Weight so it fires on hits.")]
        public int BandHarm { get; set; }

        // ── D: Transient detection ────────────────────────────────────────────

        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 40,
            Name = "Sensitivity",
            Description = "Transient detection threshold. Spectral flux " +
                          "(inter-frame magnitude increases across all bins) must exceed " +
                          "this to flag a frame as an attack. " +
                          "Low = trigger on everything. High = strong hits only.")]
        public int Sensitivity { get; set; }

        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 100,
            Name = "Atk Weight",
            Description = "Processing weight on attack (transient) frames. " +
                          "100 = full drive, tilt, and harmonics on every detected hit.")]
        public int AtkWeight { get; set; }

        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 15,
            Name = "Sus Weight",
            Description = "Processing weight on sustain (non-transient) frames. " +
                          "0 = completely clean between hits. " +
                          "Higher values add continuous spectral colouring.")]
        public int SusWeight { get; set; }

        // ── E: Comb resonance synthesis ───────────────────────────────────────

        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 0,
            Name = "Resonance",
            Description = "Comb synthesis amount. On each detected attack the dominant " +
                          "spectral bin is found and harmonics at 2f–6f are synthesised " +
                          "and added to the spectrum. 0 = comb disabled.")]
        public int Resonance { get; set; }

        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 60,
            Name = "Comb Decay",
            Description = "Comb harmonic ring-off time. " +
                          "Low = tight/dry (dies within a few frames). " +
                          "High = long metallic sustain that lingers between hits.")]
        public int CombDecay { get; set; }

        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 0,
            Name = "Spread",
            Description = "Detune comb harmonics. 0 = perfectly harmonic series (pitched). " +
                          ">0 = progressively inharmonic (metallic, bell-like). " +
                          "Each higher partial is detuned by Spread x (h-1) x 2%.")]
        public int Spread { get; set; }

        // ── Global ────────────────────────────────────────────────────────────

        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 100,
            Name = "Wet Mix",
            Description = "Dry/wet blend (0 = fully dry, 100 = fully wet).")]
        public int WetMix { get; set; }

        [ParameterDecl(MinValue = 0, MaxValue = 400, DefValue = 100,
            Name = "Level",
            Description = "Output level trim. 100 = unity. 200 = +6 dB. 400 = +12 dB.")]
        public int Level { get; set; }

        [ParameterDecl(
            ValueDescriptions = new[] { "256", "512", "1024", "2048" },
            DefValue = 1,
            Name = "FFT Size",
            Description = "FFT window size. Smaller = better time resolution (punchier). " +
                          "Larger = better frequency resolution. 512 recommended for drums.")]
        public int FftSizeIdx { get; set; }

        [ParameterDecl(
            ValueDescriptions = new[] { "no", "yes" },
            DefValue = 0,
            Name = "Bypass",
            Description = "Hard bypass — all processing skipped, zero CPU.")]
        public bool Bypass { get; set; }

        // ── Internal state ────────────────────────────────────────────────────
        readonly ChannelState _chL = new ChannelState(seed: 0x4C);
        readonly ChannelState _chR = new ChannelState(seed: 0x52);
        readonly float[]      _win = new float[2048];
        int   _lastWindowN = 0;
        int   _tailSamples = 0;
        const float SILENCE_THRESH = 8.0f; // ~-72 dBFS relative to ±32768

        public PedalFFTMachine(IBuzzMachineHost host) { this.host = host; }

        static int IndexToFftSize(int idx) =>
            idx switch { 0 => 256, 1 => 512, 3 => 2048, _ => 1024 };

        void EnsureWindow(int n)
        {
            if (_lastWindowN == n) return;
            _lastWindowN = n;
            for (int i = 0; i < n; i++)
                _win[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / n);
        }

        // ── IBuzzMachine.Work ─────────────────────────────────────────────────
        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (Bypass || mode == WorkModes.WM_NOIO) return false;

            int fftN = IndexToFftSize(FftSizeIdx);
            EnsureWindow(fftN);

            // Silence detection — drain STFT buffer before stopping
            float peakIn = 0f;
            for (int i = 0; i < n; i++)
            {
                float al = MathF.Abs(input[i].L), ar = MathF.Abs(input[i].R);
                if (al > peakIn) peakIn = al;
                if (ar > peakIn) peakIn = ar;
            }
            if      (peakIn > SILENCE_THRESH) _tailSamples = fftN * 2;
            else if (_tailSamples <= 0)       return false;
            else                              _tailSamples -= n;

            float lowDrive    = LowDrive    / 50f;
            float highDrive   = HighDrive   / 50f;
            float bandTilt    = BandTilt    / 100f;
            float bandHarm    = BandHarm    / 100f;
            float sensitivity = Sensitivity / 100f;
            float atkWeight   = AtkWeight   / 100f;
            float susWeight   = SusWeight   / 100f;
            float resonance   = Resonance   / 100f;
            float combDecay   = CombDecay   / 100f;
            float spread      = Spread      / 100f;
            float wet         = WetMix      / 100f;
            float dry         = 1f - wet;
            float level       = Level       / 100f;

            for (int i = 0; i < n; i++)
            {
                float dL = input[i].L, dR = input[i].R;
                float wL = _chL.Tick(dL, fftN, _win, lowDrive, highDrive, bandTilt, bandHarm,
                                     sensitivity, atkWeight, susWeight, resonance, combDecay, spread);
                float wR = _chR.Tick(dR, fftN, _win, lowDrive, highDrive, bandTilt, bandHarm,
                                     sensitivity, atkWeight, susWeight, resonance, combDecay, spread);
                output[i].L = (dry * dL + wet * wL) * level;
                output[i].R = (dry * dR + wet * wR) * level;
            }
            return true;
        }

        // ── Cooley-Tukey radix-2 DIT FFT ─────────────────────────────────────
        internal static void FFT(float[] re, float[] im, int n, bool inverse)
        {
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                float ang  = (inverse ? -1f : 1f) * 2f * MathF.PI / len;
                float wCos = MathF.Cos(ang), wSin = MathF.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cCos = 1f, cSin = 0f;
                    int   half = len >> 1;
                    for (int j = 0; j < half; j++)
                    {
                        int   a  = i + j, b = a + half;
                        float uR = re[a], uI = im[a];
                        float vR = re[b] * cCos - im[b] * cSin;
                        float vI = re[b] * cSin + im[b] * cCos;
                        re[a] = uR + vR; im[a] = uI + vI;
                        re[b] = uR - vR; im[b] = uI - vI;
                        float nc = cCos * wCos - cSin * wSin;
                        cSin     = cCos * wSin + cSin * wCos;
                        cCos     = nc;
                    }
                }
            }
            if (inverse) for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Per-channel STFT state — overlap-add, Hann x2, 50% overlap
        // ══════════════════════════════════════════════════════════════════════
        sealed class ChannelState
        {
            const int MAX  = 2048;
            const int MAXB = MAX / 2 + 1;

            readonly float[] inFifo  = new float[MAX];
            readonly float[] outFifo = new float[MAX];
            readonly float[] accum   = new float[MAX];
            readonly float[] re      = new float[MAX];
            readonly float[] im      = new float[MAX];
            readonly float[] hRe     = new float[MAXB]; // pre-drive snapshot for harmonics
            readonly float[] hIm     = new float[MAXB];
            readonly float[] prevMag = new float[MAXB]; // last-frame magnitudes for flux

            float combBin   = 0f;
            float combLevel = 0f;
            int   rover     = MAX / 2;
            int   prevN     = -1;

            readonly Random _rng;
            public ChannelState(int seed) { _rng = new Random(seed); }

            void Reset(int n)
            {
                prevN = n;
                Array.Clear(inFifo,  0, MAX);
                Array.Clear(outFifo, 0, MAX);
                Array.Clear(accum,   0, MAX);
                Array.Clear(prevMag, 0, MAXB);
                combBin = 0f; combLevel = 0f;
                rover   = n / 2;
            }

            public float Tick(float input, int n, float[] win,
                              float lowDrive,  float highDrive, float bandTilt,  float bandHarm,
                              float sensitivity, float atkWeight, float susWeight,
                              float resonance,  float combDecay, float spread)
            {
                if (prevN != n) Reset(n);

                int hop = n / 2;
                inFifo[rover]   = input;
                float outSample = outFifo[rover - hop];
                rover++;
                if (rover < n) return outSample;

                // ── Normalise + window → FFT ───────────────────────────────────
                const float NORM   = 1f / 32768f;
                const float DENORM = 32768f;
                for (int k = 0; k < n; k++) { re[k] = inFifo[k] * win[k] * NORM; im[k] = 0f; }
                FFT(re, im, n, false);

                int bins = n / 2 + 1;

                // ── Band boundaries (44100 Hz assumed) ─────────────────────────
                int b0 = Math.Clamp((int)(200f  * n / 44100f), 1,        bins - 3);
                int b1 = Math.Clamp((int)(1000f * n / 44100f), b0 + 1,   bins - 2);
                int b2 = Math.Clamp((int)(6000f * n / 44100f), b1 + 1,   bins - 1);

                // ── D: Spectral flux → transient classification ────────────────
                // prevMag[] is updated here and reused below to avoid recomputing sqrt.
                float flux        = 0f;
                float dominantMag = 0f;
                int   dominantBin = 1;

                for (int k = 0; k < bins; k++)
                {
                    float m    = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
                    float diff = m - prevMag[k];
                    if (diff > 0f) flux += diff;
                    prevMag[k] = m;
                    if (m > dominantMag) { dominantMag = m; dominantBin = k; }
                }
                flux /= bins;

                // Quadratic mapping: gentle at low sensitivity, tight at high
                bool  isAttack = flux > sensitivity * sensitivity * 0.8f;
                float weight   = isAttack ? atkWeight : susWeight;

                // Phase at dominant bin, captured before any modifications (used by E)
                float dominantPhase = MathF.Atan2(im[dominantBin], re[dominantBin]);

                // ── C: Save pre-drive spectrum for harmonic synthesis ──────────
                if (bandHarm > 0f)
                    for (int k = 0; k < bins; k++) { hRe[k] = re[k]; hIm[k] = im[k]; }

                // ── C: Per-band drive + tilt, gated by D ──────────────────────
                float tiltAmt = bandTilt - 1f; // -1..+1

                int[] bStart = { 0,  b0, b1, b2   };
                int[] bEnd   = { b0, b1, b2, bins };

                for (int b = 0; b < 4; b++)
                {
                    // Tilt: band position maps -1 (low) to +1 (high)
                    float pos    = (b / 1.5f) - 1f;
                    float bGain  = MathF.Max(0f, 1f + tiltAmt * pos);
                    float effGain = 1f + (bGain - 1f) * weight; // blend toward unity on sustain

                    // Drive interpolated across bands, scaled by weight
                    float bDrive   = lowDrive + (highDrive - lowDrive) * (b / 3f);
                    float effDrive = bDrive * weight;

                    for (int k = bStart[b]; k < bEnd[b]; k++)
                    {
                        float m = prevMag[k]; // current frame magnitude (from flux pass)
                        if (m < 1e-12f) continue;

                        // Pre-gain (tilt) → tanh saturation, combined into one scale factor.
                        // Equivalent to: amplify by effGain, then tanh-clip the result.
                        float scale = effDrive > 0.001f
                            ? MathF.Tanh(m * effGain * effDrive) / m
                            : effGain;

                        re[k] *= scale;
                        im[k] *= scale;
                    }
                }

                // ── C: 2nd harmonic exciter (from pre-drive snapshot) ──────────
                if (bandHarm > 0f)
                {
                    float hs = bandHarm * weight * 0.5f;
                    for (int k = 1; k < bins; k++)
                    {
                        int k2 = k * 2;
                        if (k2 >= bins) break;

                        float sr = hRe[k], si = hIm[k];
                        float sm = MathF.Sqrt(sr * sr + si * si);
                        if (sm < 1e-12f) continue;

                        float sp = MathF.Atan2(si, sr);
                        float a2 = hs * sm;
                        re[k2] += a2 * MathF.Cos(2f * sp);
                        im[k2] += a2 * MathF.Sin(2f * sp);
                    }
                }

                // ── E: Comb resonance ──────────────────────────────────────────
                // Decay each frame regardless of attack state
                combLevel *= 0.3f + combDecay * 0.69f; // decay factor 0.30..0.99

                // Re-excite on attack — resets level so each hit has a clean ring
                if (isAttack && resonance > 0f && dominantBin > 0)
                {
                    combBin   = dominantBin;
                    combLevel = resonance * dominantMag;
                }

                // Synthesise harmonic partials while comb is active
                if (combBin >= 1f && combLevel > 1e-6f)
                {
                    float spreadAmt = spread * 0.02f; // up to 2% extra detune per harmonic
                    for (int h = 2; h <= 6; h++)
                    {
                        float targetF = combBin * h * (1f + spreadAmt * (h - 1));
                        int   target  = (int)targetF;
                        if (target >= bins) break;
                        if (target < 1)    continue;

                        float amp   = combLevel / h;          // 1/h amplitude roll-off
                        float phase = dominantPhase * h;      // coherent with source bin
                        re[target] += amp * MathF.Cos(phase);
                        im[target] += amp * MathF.Sin(phase);
                    }
                }

                // ── DC and Nyquist must be real ────────────────────────────────
                im[0] = 0f; im[n / 2] = 0f;

                // ── Hermitian symmetry for real IFFT ───────────────────────────
                for (int k = 1; k < n / 2; k++) { re[n - k] = re[k]; im[n - k] = -im[k]; }

                // ── IFFT + Hann overlap-add + denormalise ──────────────────────
                FFT(re, im, n, true);
                for (int k = 0; k < n; k++)
                    accum[k] += re[k] * win[k] * 2f * DENORM;

                Array.Copy (accum,  0,   outFifo, 0,  hop);
                Array.Copy (accum,  hop, accum,   0,  hop);
                Array.Clear(accum,  hop, hop);
                Array.Copy (inFifo, hop, inFifo,  0,  hop);
                Array.Clear(inFifo, hop, hop);
                rover = hop;

                return outSample;
            }
        }
    }
}
