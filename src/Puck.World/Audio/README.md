# Audio/ — the mixer core and the device

This folder holds the world's audio: a pure, deterministic mixing core, the
synthesizer voices behind it, and the Windows output device that drives both.
The data model that decides WHAT sounds (speaker rows, tunes, patches, cues)
lives in the world document, and the client-side
[`WorldAudioDirector`](../Client/README.md) derives the emitter table from it
and publishes `WorldAudioSnapshot`s here.

## The core (`WorldAudioMixer.cs`, `WorldAudioSnapshot.cs`)

The rate is 48000 Hz, fixed — device-native (`WorldAudioMixer.SampleRate`).
It is NOT assumed to be a multiple of the world's simulation rate: 90 Hz and
45 Hz (both required Steam Deck OLED refresh rates) divide the 50400
engine-tick base cleanly but not 48000, so a sim step's frame count is not
always a whole constant. `WorldAudioMixer.FramesPerStep` answers a one-off
ceiling ("about how many frames is one step" — used as slack, never as a
hash-critical value), and `WorldAudioMixer.CreateStepAccumulator`/
`AdvanceStepFrames` carry the exact remainder across many consecutive steps
with zero long-run drift — the same `Puck.Maths.FixedRateAccumulator`
technique `WorldBody`'s motion integration already uses. `MaxBlockFrames`
(256) is the device pump's own real-time quantum ceiling and is unrelated to
any of this: a sim step's frame total can exceed one block (at 90 Hz a step
spans ~2 blocks, at 45 Hz ~5), so `MixBlock` is never assumed to render
exactly one step, and a step is never assumed to render in exactly one block.

`MixBlock` is fixed point end to end: s16 samples times Q16 composite gains,
accumulated in int32, through a deterministic polynomial soft-clip back to
s16 — never a libm call. Per block, each emitter derives target coefficients
from the snapshot (finite-support squared-smoothstep distance attenuation
whose zero IS the cull, equal-power pan from listener-relative azimuth) and
the live coefficients ramp linearly across the block from the previous
block's values, which is what prevents zipper noise. Each distinct source
identity is pulled once per block and every feed taps the shared scratch, so
a stereo pair is two rows over one source.

`MixBlock` is synchronous and owns no thread — the device pump below is one
driver of it, and a headless harness is another. Its printed PCM hashes, when
a harness computes them, are self-referential (two fresh runs agree bit for
bit); a deliberate mix-law correction is expected to change them.

## The synth (`WorldVoiceSynth.cs`)

Thirty-two fixed-struct voices with zero steady-state allocation: a
fixed-point complex rotor for sine, phase-accumulator pulse/saw/triangle,
seeded noise, ADSR in sample units, control-rate pitch sweep and vibrato, and
one state-variable filter per voice. Triggers ride snapshots with strictly
increasing sequence numbers; allocation steals the quietest voice. Patches
arrive as `puck.synth.v1` documents, flattened once at registration.

## Tune hosting (`TuneMachineSource.cs`)

Hosts a `puck.audio.v1` tune through the `Puck.Forge` compile chain over a
synchronous emulator core, acquired while referenced and released when
orphaned.

## The device (`WorldAudioRenderService.cs`)

A hosted service owning one mixer and a governor thread: it opens the default
render endpoint through the `Puck.Platform.Audio` factory seam (null off
Windows — the service parks as `unsupported`), attaches the mixer to the
director, and watches the stream. The failure posture is "plays silent, never
crashes": any failing HRESULT parks the pump, the governor detaches the
mixer, and the service retries the default endpoint on a fixed period; a fill
fault degrades to one silent quantum and a counted fault. `audio.state`
echoes the whole story over the console (device token, frames delivered,
rebind attempts, fill faults, bound sources, live voices, peak, last fault).

Screen machines always boot with audio configured at 48000 Hz, so a speaker
row can bind a machine source at any time without a machine reboot; the
director re-resolves the binder's live machines by reference each produced
frame, so a cartridge booting late into an already-referenced slot self-heals
with no verb. One asymmetry to fix, not a design decision: a cartridge
DECLARED in the world document boots before the binder lifecycle tap is
wired, so it fires no `screen.boot` cue; runtime inserts and reconcile-driven
source changes fire it correctly.

## Verifying

Run the game and read `audio.state`, `audio.emitters`, and `speaker.state`
over stdin; there is no committed audio battery in the build today (the
offline mixer-hash and device-liveness harnesses were quarantined with the
proof suite — see the parent [`README`](../README.md)).
