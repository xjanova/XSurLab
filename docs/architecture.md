# XSurLab 0.2 architecture

- **XSurLab.Core**: WAV decoder, validated JSON sessions, speaker maps, panner, stateful streaming mixer and offline renderer.
- **XSurLab.Audio**: native WASAPI shared-mode output, endpoint enumeration and strict discrete layout checks; no NuGet audio dependency.
- **XSurLab.App**: native WPF UI, real waveform, source panner, file dialogs and live monitor controls.
- **XSurLab.SmokeTests**: deterministic signal/file/stream checks without an external test framework.
- **XSurLab.AudioChecks**: routing contract checks and optional silent hardware playback/cancellation diagnostics.

## Signal path

`source PCM/float → linear sample-rate conversion → track gain → speaker-pair pan → sum → master gain → PCM24`

LFE is an explicit parallel send from the mono source or stereo average, after track gain and before
master gain, through a 120 Hz second-order Butterworth low-pass. It is not bass management and does not
apply a cinema +10 dB calibration gain. Mute excludes a track; if any track has Solo enabled, only
unmuted solo tracks contribute. Track gain and master −60 dB act as mute.
Offsets are rounded to the nearest output sample. Render range covers all source tracks, including
muted tracks; the low-pass tail is truncated at this range.

Both export and playback consume the same `StreamingMixer`. Reads preserve sample position and
120 Hz filter state between blocks. The audio thread owns mutable DSP state; the UI atomically
publishes immutable parameter blocks. Gain, pan, LFE, mute/solo and master changes take effect at
the next block. All tracks keep filter history even while muted or with LFE disabled. No parameter
smoothing is implemented, so abrupt edits may click. Structural edits stop playback.

WASAPI runs on a dedicated STA thread with balanced COM initialization and releases. It requests a
50 ms shared buffer, queries actual capacity/padding, writes float32 interleaved samples, and drains
queued samples before finishing. Cancellation stops and releases the endpoint. No temporary preview
WAV is written. New playback waits for the preceding stream to release its resources. Device removal
or Windows format changes stop with an error; Refresh outputs enumerates active endpoints again.

Discrete surround requires the endpoint mix format to have the exact channel count AND channel mask.
`IsFormatSupported` must return S_OK for the session rate/float format; closest matches are rejected.
This avoids accepting a surround-to-stereo APO as discrete output. Stereo fold-down is explicit and
may use any endpoint that accepts stereo. Windows/APOs can still process shared-mode audio; physical
speaker wiring and calibration require hardware checks. Default output is selected only on enumeration,
not followed automatically during playback. Output selection, fold-down and monitor gain are not saved.

Monitor gain starts at 50%, is separate from master/export, and is applied just before float conversion.
A block above full scale after monitor gain stops playback before that block is submitted. Export still
rejects clipping in the complete mix. Neither path is a limiter or a true-peak meter. Polling is every
5 ms; reported buffer duration is not round-trip latency or a low-latency guarantee.

WASAPI references: [Rendering a Stream](https://learn.microsoft.com/en-us/windows/win32/coreaudio/rendering-a-stream),
[IsFormatSupported](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-isformatsupported).

## Output identity

| Layout | WAVE mask | Interleaved order |
| --- | --- | --- |
| Stereo | 0x0003 | L, R |
| 5.1 with side surrounds | 0x060F | L, R, C, LFE, Ls, Rs |
| 5.1 with back surrounds | 0x003F | L, R, C, LFE, Lb, Rb |
| 7.1 | 0x063F | L, R, C, LFE, Lb, Rb, Ls, Rs |

Surround files use WAVEFORMATEXTENSIBLE, PCM GUID, 24 valid bits in a 24-bit container.
Channel order follows ascending channel-mask bits.
Source: [Microsoft channel masks](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/channel-mask).
5.1 Side and 5.1 Back are separate explicit session layouts. Choose the one matching the endpoint;
there is no automatic side/back substitution. Both use ±110° surround positions in the panner.

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

1. Physical discrete speaker verification/calibration, parameter smoothing, device notifications and lower-latency/exclusive/ASIO output.
2. Timeline editing, channel/bus routing, automation and processing.
3. Production-quality resampling, dither, BWF/stems and measured loudness/true peak.
4. Height beds and licensed encoder integrations after their APIs and terms are verified.

The design prototype is a reference, not a claim that its eight workspaces have native implementations.

