# XSurLab 0.5 architecture

- **XSurLab.Core**: WAV/RF64 decoder and streaming reader, validated JSON sessions, speaker maps, two-layer panner,
  windowed-sinc resampler, track EQ, stateful streaming mixer, BS.1770-4 loudness meter, WAV/BWF/RF64 writer,
  offline renderer, delivery with QC, edit history, and the monitor-only speaker calibration (`MonitorChain`,
  `TestSignal`, `CalibrationStore`).
- **XSurLab.Audio**: native event-driven WASAPI output (shared and exclusive), endpoint enumeration and change notifications, strict discrete layout checks; no NuGet audio dependency.
- **XSurLab.App**: native WPF UI: timeline, source panner, EQ graph, channel meters, deliver window, file dialogs and live monitor controls.
- **XSurLab.SmokeTests**: deterministic signal/file/stream checks without an external test framework.
- **XSurLab.AudioChecks**: routing contract, PCM packing, device-list and COM notification checks, plus optional
  silent hardware playback (drain, seek, cancel, and a calibrated drain with a silent test signal, in shared and
  exclusive mode).

## Signal path

`source PCM/float → windowed-sinc rate conversion (when rates differ) → 6-band track EQ → track gain × pan
(two-layer, with spread) or bed routing by channel mask → sum → master gain → [Lo/Ro fold-down for the monitor]
→ meter → PCM16/24 with TPDF dither, or float32`

The monitor adds `→ [bass management] → speaker trim → speaker delay → monitor gain → WASAPI` after the meter.
Export and delivery read the mixer directly, so calibration can never reach a file.

LFE is an explicit parallel send from the EQ'd mono source or stereo average through a 120 Hz
second-order Butterworth low-pass; track gain and the send gain apply after the filter, before master
gain. It is not bass management and does not apply a cinema +10 dB calibration gain. Beds route their
own LFE channel and take no send. Mute excludes a track; if any track has Solo enabled, only unmuted
solo tracks contribute. Track gain and master −60 dB act as mute.
Offsets are rounded to the nearest output sample. Render range covers all source tracks, including
muted tracks; the low-pass tail is truncated at this range. Delivery may fix a longer length so stems
line up with their mix.

Both export and playback consume the same `StreamingMixer`. Reads preserve sample position and
filter state between blocks. The audio thread owns mutable DSP state; the UI atomically publishes
immutable parameter blocks. Gain, pan, LFE, mute/solo, EQ and master changes ramp to their targets over
20 ms from the next block (EQ coefficients interpolate linearly, which stays inside the biquad stability
region); offline renders start at their targets. All tracks keep filter history even while muted or with
LFE disabled. `Seek` takes effect on the next read from any thread and fades in over 20 ms. Structural
edits (media, offsets, layout, rate) stop playback.

WASAPI runs on a dedicated STA thread with balanced COM initialization and releases, registered with
MMCSS as "Pro Audio". Both modes are event-driven: the endpoint signals once per device period and the
thread fills what it asks for. Two seconds without a signal while running stops playback as a lost device.

- **Shared** requests a 50 ms buffer in float32, tops up `capacity − padding` frames on each signal
  and drains queued samples before finishing. Audio passes through the Windows mixer, volume and APOs.
- **Exclusive** asks the driver for the exact WAVEFORMATEXTENSIBLE (channel count, channel mask and
  session rate), trying float32, PCM 24-in-32, PCM 32, PCM 24 and PCM 16 in that order. It uses the
  device's default period, re-initializes with a frame-aligned period when the driver asks
  (`AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED`), writes one whole period per signal and plays one silent period
  after the last audio before stopping. Nothing resamples, mixes or changes the level on the way, so
  Windows volume does not apply, and other apps are silent while it plays. Integer formats are
  rounded to the nearest code without dither (export keeps its own TPDF dither).

A seek stops and resets the stream so the jump is heard at once. Cancellation stops and releases the
endpoint. No temporary preview WAV is written. New playback waits for the preceding stream to release
its resources. Exclusive mode switched off in Windows, an endpoint held by another exclusive app, and a
removed device each end with their own message; a removed device (`AUDCLNT_E_DEVICE_INVALIDATED`) is
reported on the status line rather than in a dialog.

`DeviceWatcher` registers an `IMMNotificationClient` for endpoints being added, removed, enabled or
disabled, made the multimedia default, or given a new engine format or name. Callbacks arrive on a
Windows thread and only post to the UI dispatcher; after 400 ms without another notification the
outputs are enumerated again and the list is updated in place (`DeviceList.Sync`). Entries that did not
change keep their instance, so the selection and a running stream survive unrelated changes. When the
selected output disappears, playback stops and the first (default) output is selected but not started.

Discrete surround in shared mode requires the endpoint mix format to have the exact channel count AND
channel mask. `IsFormatSupported` must return S_OK for the session rate/float format; closest matches are
rejected. This avoids accepting a surround-to-stereo APO as discrete output. In exclusive mode the driver
decides: it may accept a layout the Windows speaker setup does not show (for example 7.1 on an endpoint
configured as 5.1), and the channels then go to the driver's outputs as WAVE order says. Stereo fold-down
is explicit and may use any endpoint that accepts stereo. Physical speaker wiring still needs a listening check;
the calibration test signal names each speaker as it plays. Output selection, output mode, fold-down and monitor
gain are not saved.

Monitor gain starts at 50%, is separate from master/export, and is applied just before sample conversion.
A block above full scale after monitor gain stops playback before that block is submitted. Export still
rejects clipping in the complete mix. Neither path is a limiter or a true-peak meter. The reported buffer
(shared) or period (exclusive) is not round-trip latency or a low-latency guarantee; the playhead
estimate is within one period in exclusive mode.

## Speaker calibration

`WasapiPlayback` plays any `IAudioSource`. The app wraps the mixer (or, in the calibration window, a `TestSignal`)
in a `MonitorChain` built for the monitor layout: the session layout, or Stereo for the fold-down. A calibration
belongs to one output (endpoint ID) and one layout and lives in `%APPDATA%\XSurLab\calibration.json`, never in a
session. Loading accepts calibration schema 1 and 2 and validates size (1 MiB), speaker names and order, trim −20…+10 dB, delay 0–50 ms and
the crossover; saving writes a sibling temporary file and swaps it in with `File.Replace`, keeping the previous file
as `.bak`. A file that cannot be read is left in place and the monitor plays flat; the first save after that copies
it to `calibration.json.unreadable-<time>` before replacing it, so rotating `.bak` cannot lose it.
Each main or height speaker has a `Small` flag (default true, including in schema 1 files). Large speakers keep
their full range and contribute no redirected bass. The flag is ignored for LFE and when bass management is off
or bypassed for test noise. Loading does not rewrite a file; saving writes schema 2 and keeps the previous file
as `.bak`. Older builds reject schema 2 rather than silently ignoring Large settings.

Per frame the chain runs bass management, then trim, then a per-speaker delay line rounded to whole samples. Bass
management is a fourth-order Linkwitz–Riley crossover (two Butterworth biquads per half): each Small speaker is
high-passed, the sum of the Small speakers' low-passed signals joins LFE on the subwoofer, optionally with the +10 dB in-band LFE gain,
and high plus low add back to a flat magnitude (allpass). LFE is not low-passed again. The filters always run, so
switching bass management on has no start-up transient; while it is off, every sample passes unchanged, and a flat
calibration is bit-exact. Each main has its own high- and low-pass histories; the Small/Large blend ramps over
20 ms after filtering, so changing size cannot inject an abrupt change into the subwoofer's filter input.

The UI publishes a calibration as an immutable record; the audio thread ramps trims, the bass-management blend, the
LFE gain and the crossover coefficients over 20 ms. A delay change crossfades from the old tap to the new one over
20 ms; a change that arrives during a crossfade waits for it, so at most two taps are mixed and the output never
jumps. A seek (counted by `IAudioSource.Seeks`) clears the delay lines and filter history, since the mixer fades in
from silence there. When the source ends, the chain keeps reading silence through the delay lines until the most
delayed speaker has played out, and only then reports `Finished`.

`TestSignal` is xorshift64* white noise through Paul Kellet's pink filter and a 24 dB/oct band-pass (500 Hz–2 kHz
for mains, 20–120 Hz for the subwoofer), scaled to −20 dBFS RMS by measuring ten seconds of its own deterministic
output and clipped at −8 dBFS. One speaker plays at a time; a new speaker waits until the old one has faded out over
20 ms. In the calibration window the chain bypasses bass management, so every speaker, the subwoofer included, is
measured on its own noise. Mixes expect LFE 10 dB above the mains in band: with bass management and LFE +10 dB the
chain adds that gain, so the subwoofer is trimmed to read the same as the mains; without bass management (or with
LFE +10 dB off) nothing is added and the subwoofer is set to read 10 dB above them. The noise never ends; closing the
window fades it out and releases the output before the main window may play again.

WASAPI references: [Rendering a Stream](https://learn.microsoft.com/en-us/windows/win32/coreaudio/rendering-a-stream),
[IsFormatSupported](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-isformatsupported).

## Output identity

| Layout | WAVE mask | Interleaved order |
| --- | --- | --- |
| Stereo | 0x0003 | L, R |
| 5.1 with side surrounds | 0x060F | L, R, C, LFE, Ls, Rs |
| 5.1 with back surrounds | 0x003F | L, R, C, LFE, Lb, Rb |
| 7.1 | 0x063F | L, R, C, LFE, Lb, Rb, Ls, Rs |
| 5.1.4 | 0x2D60F | L, R, C, LFE, Ls, Rs, Tfl, Tfr, Tbl, Tbr |
| 7.1.4 | 0x2D63F | L, R, C, LFE, Lb, Rb, Ls, Rs, Tfl, Tfr, Tbl, Tbr |

Surround files use WAVEFORMATEXTENSIBLE: PCM 16 or 24 bits (valid bits equal to the container) or
IEEE float 32 with a `fact` chunk. An optional BWF `bext` v2 chunk carries the measured loudness;
files whose RIFF size passes 4 GiB are written as RF64 with a `ds64` chunk.
Channel order follows ascending channel-mask bits.
Source: [Microsoft channel masks](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/channel-mask).
5.1 Side and 5.1 Back are separate explicit session layouts. Choose the one matching the endpoint;
output never substitutes side for back. Both use ±110° surround positions in the panner. Imported beds
route channel for channel by mask, substitute side/back when only one pair exists, fold centre,
surrounds and heights at −3 dB where a speaker is missing, and drop LFE without an LFE speaker.

The ear-level ring uses adjacent angular speaker pairs with cosine/sine gains. Heights sit at 45°
elevation, ±45° and ±135°. Elevation crossfades power from the ear-level ring to the height ring by
45° and spreads it over all heights by 90°; spread averages the power of virtual sources along an arc.
Every combination keeps sum-of-squares gain at one. Layouts without heights ignore elevation; stereo
clamps pan at ±30°. Stereo sources pan as an L/R pair ±30° around their azimuth. It is an amplitude
panner: it does not model distance, room response, HRTFs or objects.
7.1 side/rear azimuths are ±90°/±150°; 5.1 surrounds are ±110°.
The channel mask expresses speaker identity, not exact physical angles.

## File safety

Imports are loaded before changing the current session. Missing/unsupported media leaves the old mix in place.
Session schema version 2 validates enum values, unique IDs and finite parameter ranges (elevation, spread,
EQ 20 Hz–20 kHz / ±24 dB / Q 0.1–10, group names). Version 1 files load with elevation and spread at 0 and a flat EQ.
Files reference media using paths relative to the session folder; moving only JSON does not move audio.
Session saves, WAV exports and deliveries write to unique sibling temporary files and replace the destination on
success. Deliver refuses existing files unless its window confirmed replacing them.
Renderer rejects source-media overwrite, clipping in integer formats and cancellation; float exports may carry overs.
Session saving also rejects source-media overwrite. No original media is edited.

Data chunks up to 8 MiB are decoded into memory (at most 1 GiB per session). Larger ones stream from disk:
each consumer opens its own reader that caches 16384-frame blocks read with `RandomAccess`, so a failing or
unplugged disk raises a catchable `IOException` instead of the in-page error a memory map would.
Edit history keeps up to 200 whole-session snapshots; continuous edits of one control merge into one step.
The test harness generates its own fixtures and always deletes its own temporary directory.

## Next milestones

1. ASIO output, measured round-trip latency, and microphone-assisted calibration (level, distance, room EQ).
2. Automation, clip trim/split/fades, bus routing and dynamics.
3. Up/downmix workspace and binaural monitoring (per-speaker Small/Large bass management is implemented).
4. Licensed encoder integrations (DTS family) after their APIs and terms are verified.

The design prototype is a reference, not a claim that its eight workspaces have native implementations.

