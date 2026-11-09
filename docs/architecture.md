# XSurLab 0.1 architecture

- **XSurLab.Core**: WAV decoder, validated JSON sessions, speaker maps, panner, offline renderer.
- **XSurLab.App**: native WPF UI, real waveform, source panner, file dialogs and Windows MediaPlayer preview.
- **XSurLab.SmokeTests**: deterministic signal/file checks without an external test framework.

## Signal path

`source PCM/float → linear sample-rate conversion → track gain → speaker-pair pan → sum → master gain → PCM24`

LFE is an explicit parallel send from the mono source or stereo average, after track gain and before
master gain, through a 120 Hz second-order Butterworth low-pass. It is not bass management and does not
apply a cinema +10 dB calibration gain. Mute excludes a track; if any track has Solo enabled, only
unmuted solo tracks contribute. Track gain and master −60 dB act as mute.
Offsets are rounded to the nearest output sample. Render range covers all source tracks, including
muted tracks; the low-pass tail is truncated at this range.

Preview renders a snapshot to temporary PCM16 stereo, opens it through WPF MediaPlayer,
and removes the temporary WAV on stop/completion/close. Editing stops the old snapshot.
No signal is processed live while listening.

## Output identity

| Layout | WAVE mask | Interleaved order |
| --- | --- | --- |
| Stereo | 0x0003 | L, R |
| 5.1 with side surrounds | 0x060F | L, R, C, LFE, Ls, Rs |
| 7.1 | 0x063F | L, R, C, LFE, Lb, Rb, Ls, Rs |

Surround files use WAVEFORMATEXTENSIBLE, PCM GUID, 24 valid bits in a 24-bit container.
Channel order follows ascending channel-mask bits.
Source: [Microsoft channel masks](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/channel-mask).
5.1 uses side surround labels; it deliberately differs from the back-surround 0x003F mask.

The panner uses adjacent angular speaker pairs with cosine/sine gains, preserving sum-of-squares gain.
It is a simple horizontal amplitude panner. It does not model distance, room response, HRTFs or objects.
7.1 side/rear azimuths are ±90°/±150°; 5.1 surrounds are ±110°.
The channel mask expresses speaker identity, not exact physical angles.

## File safety

Imports are loaded before changing the current session. Missing/unsupported media leaves the old mix in place.
Session schema version 1 validates enum values, unique IDs and finite parameter ranges.
Files reference media using paths relative to the session folder; moving only JSON does not move audio.
Session saves and WAV exports write to a unique sibling temporary file and replace the destination on success.
Renderer rejects source-media overwrite, clipping, cancellation and the RIFF size limit.
Session saving also rejects source-media overwrite. No original media is edited.

Decoded audio is held in memory and export is streamed. Limits are intentionally conservative.
The test harness generates its own fixtures and always deletes its own temporary directory.

## Next milestones

1. Dedicated streaming playback engine, verified discrete device routing and output calibration.
2. Timeline editing, channel/bus routing, automation and processing.
3. Production-quality resampling, dither, BWF/stems and measured loudness/true peak.
4. Height beds and licensed encoder integrations after their APIs and terms are verified.

The design prototype is a reference, not a claim that its eight workspaces have native implementations.
