# Pedal FFT 1.3

An FFT-based spectral distortion effect machine for [ReBuzz](https://github.com/wasteddesign/ReBuzz).

## Requirements

- [ReBuzz](https://github.com/wasteddesign/ReBuzz)
- [.NET 10 SDK (Windows x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) — to build

## Building

```powershell
dotnet build PedalFFT/PedalFFT.csproj -c Release
```

Output `Pedal FFT.NET.dll` is written directly to `C:\Program Files\ReBuzz\Gear\Effects\`.
Override if needed:

```powershell
dotnet build PedalFFT/PedalFFT.csproj -c Release /p:BuzzDir="D:\ReBuzz"
```

## Parameters

| Parameter  | Range             | Default | Description |
|------------|-------------------|---------|-------------|
| Drive      | 0 – 200           | 50      | Spectral tanh saturation per bin |
| Harmonics  | 0 – 100           | 25      | Add 2nd + 3rd harmonic content |
| Spec Gate  | 0 – 100           | 0       | Zero bins below this % of spectral peak |
| Wet Mix    | 0 – 100           | 100     | Dry/wet blend |
| Level      | 0 – 400           | 100     | Output trim. 100 = unity, 200 = +6 dB, 400 = +12 dB |
| Bin Shift  | 0 – 256           | 128     | Spectrum shift. 128 = no shift, <128 = down, >128 = up |
| FFT Size   | 256/512/1024/2048 | 1024    | Window size; latency = size ÷ 2 samples |
| Bypass     | no / yes          | no      | Hard bypass |

## Changelog

**v1.3**
- Silence detection: Work() returns false when input is below ~-72 dBFS and the
  STFT overlap buffer has fully drained. Zero CPU usage during silence.

**v1.2**
- Added Level output trim (0–400, default 100 = unity)

**v1.1**
- Fixed ±32768 sample scale bug
- Added Bin Shift

**v1.0** — initial release

## License

MIT
