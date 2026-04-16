// Pedal FFT – spectral FFT distortion effect for ReBuzz
// Assembly: Pedal FFT.NET.dll  →  <ReBuzz>\Gear\Effects\
//
// Algorithm: STFT overlap-add (Hann window, 50 % overlap).
//   - Drive      : per-bin tanh saturation in the frequency domain
//   - Harmonics  : adds scaled 2nd and 3rd harmonic content to each bin
//   - Spec Gate  : zeros bins whose magnitude is below a % of the spectral peak
//   - Bin Shift  : shifts the entire spectrum up/down by N bins (inharmonic pitch shift)
//   - Wet Mix    : dry/wet blend
//   - FFT Size   : 256 / 512 / 1024 / 2048  (latency = FFT Size / 2 samples)
//
// ReBuzz passes audio at ±32768 (not ±1.0).  Samples are normalised before the
// FFT and denormalised after the IFFT so Drive and Harmonics operate correctly.

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
        // ── ReBuzz host ───────────────────────────────────────────────────────
        readonly IBuzzMachineHost host;

        // ── Parameters exposed to ReBuzz ──────────────────────────────────────

        [ParameterDecl(MinValue = 0, MaxValue = 200, DefValue = 50,
            Name = "Drive",
            Description = "Spectral saturation – tanh soft-clip on bin magnitudes (0=off, 200=heavy)")]
        public int Drive { get; set; }

        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 25,
            Name = "Harmonics",
            Description = "Add 2nd + 3rd spectral harmonic content (0=off, 100=full)")]
        public int Harmonics { get; set; }

        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 0,
            Name = "Spec Gate",
            Description = "Spectral noise gate – zero bins below this % of spectral peak (0=off)")]
        public int SpectralGate { get; set; }

        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 100,
            Name = "Wet Mix",
            Description = "Wet/dry blend (0 = fully dry, 100 = fully wet)")]
        public int WetMix { get; set; }

        [ParameterDecl(MinValue = 0, MaxValue = 400, DefValue = 100,
            Name = "Level",
            Description = "Output level trim. 100 = unity, 200 = +6 dB, 400 = +12 dB. " +
                          "Use to compensate for Drive's natural volume reduction.")]
        public int Level { get; set; }

        [ParameterDecl(MinValue = 0, MaxValue = 256, DefValue = 128,
            Name = "Bin Shift",
            Description = "Shift spectrum up or down by N bins. " +
                          "128 = no shift, <128 = down, >128 = up. " +
                          "At 1024 FFT / 44100 Hz each bin is ~43 Hz. " +
                          "Creates inharmonic pitch-shift and metallic textures.")]
        public int BinShift { get; set; }

        [ParameterDecl(
            ValueDescriptions = new[] { "256", "512", "1024", "2048" },
            DefValue = 2,
            Name = "FFT Size",
            Description = "FFT window size (latency = size / 2 samples)")]
        public int FftSizeIdx { get; set; }

        [ParameterDecl(
            ValueDescriptions = new[] { "no", "yes" },
            DefValue = 0,
            Name = "Bypass",
            Description = "Bypass all processing")]
        public bool Bypass { get; set; }

        // ── Internal state ────────────────────────────────────────────────────
        readonly ChannelState _chL = new ChannelState();
        readonly ChannelState _chR = new ChannelState();
        readonly float[]      _win = new float[2048];
        int _lastWindowN = 0;

        // ── Constructor ───────────────────────────────────────────────────────
        public PedalFFTMachine(IBuzzMachineHost host)
        {
            this.host = host;
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        static int IndexToFftSize(int idx) => idx switch { 0 => 256, 1 => 512, 3 => 2048, _ => 1024 };

        void EnsureWindow(int n)
        {
            if (_lastWindowN == n) return;
            _lastWindowN = n;
            // Hann window
            for (int i = 0; i < n; i++)
                _win[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / n);
        }

        // ── IBuzzMachine.Work ─────────────────────────────────────────────────
        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (Bypass || mode == WorkModes.WM_NOIO)
                return false;

            int   fftN = IndexToFftSize(FftSizeIdx);
            EnsureWindow(fftN);

            float driveScale = Drive       / 50f;   // 0..4
            float harmAmt    = Harmonics   / 100f;  // 0..1
            float gateAmt    = SpectralGate / 100f; // 0..1
            float wet        = WetMix      / 100f;
            float dry        = 1f - wet;
            float levelScale = Level       / 100f;  // 0..4  (100 = unity)
            int   binShift   = BinShift - 128; // 0–256 param, 128 = centre = no shift

            for (int i = 0; i < n; i++)
            {
                float dL = input[i].L;
                float dR = input[i].R;

                float wL = _chL.Tick(dL, fftN, _win, driveScale, harmAmt, gateAmt, binShift);
                float wR = _chR.Tick(dR, fftN, _win, driveScale, harmAmt, gateAmt, binShift);

                output[i].L = (dry * dL + wet * wL) * levelScale;
                output[i].R = (dry * dR + wet * wR) * levelScale;
            }
            return true;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Cooley–Tukey radix-2 DIT FFT (in-place, separate real/imag arrays)
        // When inverse == true the output is divided by n.
        // ══════════════════════════════════════════════════════════════════════
        internal static void FFT(float[] re, float[] im, int n, bool inverse)
        {
            // Bit-reversal permutation
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

            // Butterfly stages
            for (int len = 2; len <= n; len <<= 1)
            {
                float ang  = (inverse ? -1f : 1f) * 2f * MathF.PI / len;
                float wCos = MathF.Cos(ang);
                float wSin = MathF.Sin(ang);

                for (int i = 0; i < n; i += len)
                {
                    float curCos = 1f, curSin = 0f;
                    int   half   = len >> 1;

                    for (int j = 0; j < half; j++)
                    {
                        int a = i + j, b = a + half;
                        float uR = re[a], uI = im[a];
                        float vR = re[b] * curCos - im[b] * curSin;
                        float vI = re[b] * curSin + im[b] * curCos;

                        re[a] = uR + vR;  im[a] = uI + vI;
                        re[b] = uR - vR;  im[b] = uI - vI;

                        float nc = curCos * wCos - curSin * wSin;
                        curSin   = curCos * wSin + curSin * wCos;
                        curCos   = nc;
                    }
                }
            }

            if (inverse)
                for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Per-channel STFT streaming state
        //
        // Uses the classic overlap-add pattern:
        //   rover  = write/read cursor inside the N-sample FIFO
        //   Latency = N/2 samples (one hop)
        //   Normalisation: Hann^2 analysis×synthesis, 50 % overlap → ×2
        // ══════════════════════════════════════════════════════════════════════
        sealed class ChannelState
        {
            const int MAX = 2048;

            readonly float[] inFifo  = new float[MAX];
            readonly float[] outFifo = new float[MAX];
            readonly float[] accum   = new float[MAX]; // overlap-add accumulator
            readonly float[] re      = new float[MAX];
            readonly float[] im      = new float[MAX];
            // Pre-allocated copy buffer for harmonic processing (positive bins only)
            readonly float[] hRe     = new float[MAX / 2 + 1];
            readonly float[] hIm     = new float[MAX / 2 + 1];

            int rover   = MAX / 2;
            int prevN   = -1;

            // Called once when FFT size changes (or on first use)
            void Reset(int n)
            {
                prevN = n;
                Array.Clear(inFifo,  0, MAX);
                Array.Clear(outFifo, 0, MAX);
                Array.Clear(accum,   0, MAX);
                rover = n / 2; // pre-fill latency: output silence for first N/2 samples
            }

            // Process one input sample, return one output sample (delayed by N/2).
            public float Tick(float input, int n, float[] win,
                              float drive, float harmonics, float gate, int binShift)
            {
                if (prevN != n) Reset(n);

                int hop = n / 2;

                // Write input; read the output produced by the last frame
                inFifo[rover]    = input;
                float outSample  = outFifo[rover - hop]; // rover >= hop always
                rover++;

                if (rover < n) return outSample; // haven't filled a hop yet

                // ── We just collected a full hop of new samples ───────────────
                // Normalise to ±1.0 before FFT so Drive and Harmonics operate
                // on a unit-scale signal regardless of ReBuzz's ±32768 convention.
                const float NORM    = 1f / 32768f;
                const float DENORM  = 32768f;

                for (int k = 0; k < n; k++) { re[k] = inFifo[k] * win[k] * NORM; im[k] = 0f; }

                FFT(re, im, n, false);

                int bins = n / 2 + 1;

                // ── Bin Shift: slide the entire spectrum up or down ───────────
                // Uses hRe/hIm as scratch (they are overwritten for harmonics later).
                // Bins that shift out of range are zeroed; bins that shift in from
                // below zero are also zeroed (no aliasing from negative frequencies).
                if (binShift != 0)
                {
                    // Copy current positive-frequency bins to scratch
                    for (int k = 0; k < bins; k++) { hRe[k] = re[k]; hIm[k] = im[k]; }

                    for (int k = 0; k < bins; k++)
                    {
                        int src = k - binShift; // source bin index before shift
                        if (src >= 0 && src < bins)
                        {
                            re[k] = hRe[src];
                            im[k] = hIm[src];
                        }
                        else
                        {
                            re[k] = 0f;
                            im[k] = 0f;
                        }
                    }

                    // Restore Hermitian symmetry after shift
                    for (int k = 1; k < n / 2; k++)
                    { re[n - k] = re[k]; im[n - k] = -im[k]; }
                }

                // ── Spectral gate: find peak magnitude ────────────────────────
                float maxMag = 1e-10f;
                if (gate > 0f)
                    for (int k = 0; k < bins; k++)
                    {
                        float m = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
                        if (m > maxMag) maxMag = m;
                    }
                float gateThresh = gate * maxMag;

                // ── Save positive-frequency bins for harmonic synthesis ────────
                if (harmonics > 0f)
                {
                    for (int k = 0; k < bins; k++) { hRe[k] = re[k]; hIm[k] = im[k]; }
                }

                // ── Per-bin spectral processing ───────────────────────────────
                for (int k = 0; k < bins; k++)
                {
                    float r  = re[k];
                    float iv = im[k];
                    float mag = MathF.Sqrt(r * r + iv * iv);

                    // Gate: zero bins below threshold
                    if (mag < gateThresh) { re[k] = 0f; im[k] = 0f; continue; }

                    // Drive: tanh soft-clipping of bin magnitude (unit-scale signal)
                    if (drive > 0.001f && mag > 1e-12f)
                    {
                        float driven = MathF.Tanh(mag * drive);
                        float scale  = driven / mag;
                        re[k] = r  * scale;
                        im[k] = iv * scale;
                    }
                }

                // ── Harmonic exciter: add 2nd and 3rd harmonic content ────────
                if (harmonics > 0f)
                {
                    for (int k = 1; k < bins; k++)
                    {
                        float sr  = hRe[k], si = hIm[k];
                        float sm  = MathF.Sqrt(sr * sr + si * si);
                        if (sm < 1e-12f) continue;

                        float sp = MathF.Atan2(si, sr);

                        // 2nd harmonic (amplitude = 50 % of fundamental contribution)
                        int k2 = k * 2;
                        if (k2 < bins)
                        {
                            float a2 = harmonics * 0.50f * sm;
                            re[k2] += a2 * MathF.Cos(2f * sp);
                            im[k2] += a2 * MathF.Sin(2f * sp);
                        }

                        // 3rd harmonic (amplitude = 25 %)
                        int k3 = k * 3;
                        if (k3 < bins)
                        {
                            float a3 = harmonics * 0.25f * sm;
                            re[k3] += a3 * MathF.Cos(3f * sp);
                            im[k3] += a3 * MathF.Sin(3f * sp);
                        }
                    }

                    // Restore Hermitian symmetry so IFFT produces real output
                    for (int k = 1; k < n / 2; k++)
                    { re[n - k] = re[k]; im[n - k] = -im[k]; }
                }

                // ── IFFT ──────────────────────────────────────────────────────
                FFT(re, im, n, true);

                // ── Overlap-add (Hann^2 50 %-overlap norm = ×2, then denormalise)
                for (int k = 0; k < n; k++)
                    accum[k] += re[k] * win[k] * 2f * DENORM;

                // ── Copy first hop samples to output FIFO ─────────────────────
                Array.Copy(accum, 0, outFifo, 0, hop);

                // ── Slide accumulator left by one hop ─────────────────────────
                Array.Copy (accum, hop, accum, 0, hop);
                Array.Clear(accum, hop, hop);

                // ── Slide input FIFO left by one hop, reset rover ─────────────
                Array.Copy (inFifo, hop, inFifo, 0, hop);
                Array.Clear(inFifo, hop, hop);
                rover = hop;

                return outSample;
            }
        }
    }
}
