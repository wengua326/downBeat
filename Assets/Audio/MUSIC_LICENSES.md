# Music & Audio Licensing Log — Rhythm Dart MR

All audio in **Rhythm Dart MR** is 100% original and procedurally synthesized at runtime in C# (`Assets/Scripts/Audio/DrumPadAudioEngine.cs`), requiring **zero third-party royalties or attribution restrictions**:

1. **`BackingGroove_100BPM` (Base BPM: 100.0 BPM)**
   - **Source:** Self-made procedural DSP synthesis (`DrumPadAudioEngine.CreateBaseBackingLoopClip`).
   - **License:** CC0 / Original Team Work (100% Royalty-Free for Meta VR Start Developer Competition 2026).
   - **Tempo Scaling:** Dynamically stepped from `100 -> 110 -> 125 -> 140 BPM` on 4-beat bar boundaries via `BeatConductor.cs`.

2. **Interactive Drum Pad Samples ("Darts as a Drum Pad")**
   - **`BullseyeSubBassKick`:** Procedural 808 sub-bass kick (`135 Hz -> 42 Hz` exponential pitch envelope).
   - **`TrebleCrispSnare`:** Procedural snare & high-frequency transient clap.
   - **`SingleSynthStab_0..9`:** Procedural C-Minor Pentatonic harmonic chord stabs mapped across the 20 radial dartboard sectors.
   - **License:** CC0 / Original Team Work.
