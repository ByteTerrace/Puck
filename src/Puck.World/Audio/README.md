# Audio/ — presentation glue over the mixer core

The deterministic mixer core, voice synth, and snapshot shape live in
[`Puck.Audio.Mixing`](../../Puck.Audio/README.md) (`AudioMixer`,
`VoiceSynth`, `AudioSnapshot`, `MachineAudioRate`). This folder
holds everything presentation-side that drives that core: document intake, a
headless tune host, and the Windows output device. The data model that
decides WHAT sounds (speaker rows, tunes, patches, cues) lives in the world
document, and [`WorldAudioDirector`](../README.md) derives the emitter table
from it and publishes `AudioSnapshot`s here.

## Document intake (`WorldVoicePatchFactory.cs`)

`Puck.Audio` parses no document. `WorldVoicePatchFactory.FromDocument`
converts a normalized `puck.synth.v1` document
(`Puck.World.Authoring.SynthPatchDocument`) into the runtime
`Puck.Audio.Mixing.VoicePatch` struct — the one place a document crosses
that boundary, including mapping the document's
`Puck.World.Authoring.SynthOscillator` onto the mixer's own
`Puck.Audio.Mixing.SynthOscillator` (same ordinals, kept as two separate
enumerations rather than one shared type across the layering boundary).

## Tune hosting (`TuneMachineSource.cs`)

Hosts a `puck.audio.v1` tune through the `Puck.HumbleGamingBrick.Forge` compile chain over a
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

Screen machines always boot with audio configured at
`Puck.Audio.Mixing.MachineAudioRate.SampleRate` (48000 Hz), so a speaker
row can bind a machine source at any time without a machine reboot; the
director re-resolves the binder's live machines by reference each produced
frame, so a cartridge booting late into an already-referenced slot self-heals
with no verb. One asymmetry to fix, not a design decision: a cartridge
DECLARED in the world document boots before the binder lifecycle tap is
wired, so it fires no `screen.boot` cue; runtime inserts and reconcile-driven
source changes fire it correctly.

## Verifying

Run the game and read `audio.state`, `audio.emitters`, and `speaker.state`
over stdin; the mixer core's own proofs, if any, live beside it in
`Puck.Audio`, not here.
