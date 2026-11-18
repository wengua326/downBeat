# downBeat

A beat-synced Mixed Reality rhythm dart game for Meta Quest built with **Unity 6 (`6000.6.7f1`)**, **Universal Render Pipeline (URP)**, **Meta XR SDK (`207.0.1`)**, and **OpenXR**.

## Features
- **Passthrough Mixed Reality Dart Arena:** Floating neon dartboard and HUD (`TIME`, `SCORE`, `DARTS`, BPM, active buffs) anchored in your room.
- **Controller-Free Hand Tracking:** Pure hand aiming and forward-throw gesture detection (`HandThrowController`) with pre-throw lookback aim lock, plus optional Editor/Simulator click-drag testing.
- **Sample-Accurate DSP Rhythm Timing:** `BeatConductor` tracks continuous beat phase via `AudioSettings.dspTime` with bar-aligned tempo escalation (`85 → 90 → 95 → 100 BPM`).
- **Darts as a Drum Pad:** `DrumPadAudioEngine` synthesizes sub-bass kicks, snares, and C-minor pentatonic synth stabs on hit and layers on-beat throws into an evolving 8-step loop.
- **Power Cards:** Hitting a **POWER** target pauses the clock and offers 3 randomized buffs (*Overdrive 2x*, *Jackpot 3x*, *Time Warp +15s*, *Quiver +5 Darts*, *Big Targets*, *Slow Motion*, *Faster Spawn*, *Combo Shield*) selected by throwing a dart at the chosen card.

## Project Structure
- `Assets/Scripts/Audio/` — `BeatConductor`, `DrumPadAudioEngine`
- `Assets/Scripts/Gameplay/` — `RhythmDartboard`, `ApproachCircleCue`, `DartFlight`, `ScoreAndComboManager`, `BuffSystem`
- `Assets/Scripts/Input/` — `HandThrowController`
- `Assets/Scripts/UI/` — `MixedRealityHUD`, `GameSessionManager`, `BuffSelectionScreen`, `SessionCountdownOverlay`, `UiTheme`
- `Assets/Scripts/VFX/` — `BackgroundVideoSwitcher`, `DemoRecordingStudio`, `ImpactVfxDirector`, `DartTrailFX`, `VfxCore`
- `Assets/Scenes/SampleScene.unity` — Main Mixed Reality scene
