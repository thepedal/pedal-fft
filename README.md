# Pedal FFT 2.0

Spectral drum processor for [ReBuzz](https://github.com/wasteddesign/ReBuzz).

## Building & deploying

```powershell
dotnet build PedalFFT.csproj -c Release
```

Deploys both `Pedal FFT.NET.dll` and `Pedal FFT.prs.xml` to
`C:\Program Files\ReBuzz\Gear\Effects\`. Override if needed:

```powershell
dotnet build PedalFFT.csproj -c Release /p:BuzzDir="D:\ReBuzz"
```

The preset bundle auto-loads on first machine drop — presets appear immediately
in the right-click menu, no import step required.

## Processing chain

```
FFT → [D: flux → isAttack] → [C: per-band drive+tilt × weight] → [C: harmonics] → [E: comb] → IFFT
```

## Parameters

### C — Per-band (4 bands: sub/kick · body · crack · air)
| Parameter  | Range  | Default | Notes |
|------------|--------|---------|-------|
| Low Drive  | 0–200  | 50      | Saturation for low bands |
| High Drive | 0–200  | 50      | Saturation for high bands |
| Band Tilt  | 0–200  | 100     | 100 = flat, >100 = bright, <100 = dark |
| Band Harm  | 0–100  | 25      | 2nd harmonic exciter per band |

### D — Transient detection
| Parameter   | Range  | Default | Notes |
|-------------|--------|---------|-------|
| Sensitivity | 0–100  | 40      | Flux threshold |
| Atk Weight  | 0–100  | 100     | Processing weight on hit frames |
| Sus Weight  | 0–100  | 15      | Processing weight between hits |

### E — Comb resonance
| Parameter  | Range  | Default | Notes |
|------------|--------|---------|-------|
| Resonance  | 0–100  | 0       | Comb amount (0 = disabled) |
| Comb Decay | 0–100  | 60      | Ring-off time |
| Spread     | 0–100  | 0       | 0 = pitched, >0 = metallic |

### Global
| Parameter | Range             | Default | Notes |
|-----------|-------------------|---------|-------|
| Wet Mix   | 0–100             | 100     | Dry/wet |
| Level     | 0–400             | 100     | Output trim |
| FFT Size  | 256/512/1024/2048 | 512     | 512 recommended for drums |
| Bypass    | no/yes            | no      | Hard bypass |

## Presets (30)

### Drum Processing
Drum Room · Kick Punch · Snare Crack · Think Break · Hard Bop · Transient Sniper

### Comb Resonance
Pitched Ring · Metal Bell · Gong Hit · Sci-Fi Drums · Tuned Kick · Snare Rattle

### Heavy Saturation
Fried Drums · Overdrive · Spectral Crush · Crunch Box · Band Splitter

### Tonal Shaping
Dark Matter · Air and Edge · Sub Emphasis · Presence Boost · Full Range

### Texture & Character
Ghost Notes · Sustain Smear · Stutter Gate · Velvet Drums · Vintage Crunch

### Experimental
Spectral Melt · Robot Drums · Through the Wall

## License

MIT
