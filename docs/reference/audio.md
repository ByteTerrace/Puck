# Audio mixer, synthesis, and music direction

Puck.Audio owns the world's deterministic mixer core and voice synth in the
`Puck.Audio.Mixing` namespace: `AudioMixer` (fixed-point mixing—s16
samples × Q16 composite gains, int32 accumulate, a deterministic polynomial
soft-clip, per-block linear coefficient ramping), `VoiceSynth` (32
fixed-struct voices—sine as a complex rotor, phase-accumulator pulse/saw/
triangle, seeded noise, ADSR, control-rate pitch sweep/vibrato, one
state-variable filter per voice), `AudioSnapshot` (the immutable
per-block input: listener pose, emitter table, seeded synth triggers), and
`MachineAudioRate` (the machine-audio output rate every booted
`IMachineRuntime` shares with the mixer).

This is presentation-adjacent state: it carries no replay/hash contract of
its own, and its own byte-identity proofs (when they exist) are
self-referential—two fresh runs agreeing, never a pinned historical value.

`Puck.Audio.Simulation` is sim state, not presentation: `MusicClock`
(tick-denominated tempo-map position—plain integer tick accumulation, since
an authored `ticksPerBeat` is already a whole engine-tick count with no
fractional remainder to carry), `MusicDirector` (an event-driven segment state
machine—a transition arms on a matching `MusicSenseEdge` this tick and
commits on the next boundary `MusicClock` reports; a segment's conditional
audio layers recompute whole every `Step`, level-triggered off that tick's
edges rather than queued like a transition—`ActiveLayerTuneIds` names every
tune active this tick; a segment's director embellishments fire instantaneously
on a matching edge, recorded in `LastEmbellishmentPatchId`/`LastEmbellishmentTick`).
Neither type references
`WorldEventFeed`/`WorldEventEdge` (the project that declares them sits above
this one in the layering)—`Puck.World.Server.MusicDirectorFactory` compiles
an authored `puck.music.v1` document into these shapes and
projects one tick's `WorldEventFeed.Edges` into `MusicSenseEdge`s at the
`WorldServer.Step` call site, immediately after `Collect()`. A rhythm hit
window is an authored `compareState` range over the `$clock:<music>:phaseError`
world-rule operand (the signed tick distance from `MusicClock`'s current
position to the nearest beat) rather than a dedicated grader type.

A world names its score, tunes and patches by rows (`music`, `tunes`,
`patches`, like `tables`), each a name, a `source` and a SHA-256 pin of the
referenced document's canonical bytes. `WorldAssetRowLoader` reads the source
beside the world document that authored the row
([paths a document names](../architecture/worlds.md#paths-a-document-names)),
so a fixture three directories below the repository root names the debug world's tune as
`../../../worlds/debug/tunes/ambient-tune.audio.json`, and a
fixture-only score sits beside the fixture.

`VoiceBabbler` is the same tier's third primitive: `ComputeTriggerTicks`
turns a caller-estimated syllable count plus an identity's authored cadence
into the trigger tick of each syllable's short pitched voice—cadence-spaced,
with a bounded forward-only jitter drawn from one `Pcg32XshRr` stream seeded
`(state: utteranceOrdinal, stream: identitySeed)`. Like `MusicClock` it
never estimates a syllable count from text or chooses which
`VoicePatch` a trigger voices—both are the caller's job. Playback wiring is
landed on top of it: `Puck.World.Client.WorldAudioDirector.TriggerBabble`
resolves the delivered definition's single `WorldIdentityDefinition.Voice`
selectors, drives `ComputeTriggerTicks` for the utterance's tick schedule, and
fires one seeded `VoiceSynth` trigger per syllable as its delay elapses
(`AdvanceBabbleSchedule`) through the reserved `voice.babble` cue token
(`WorldAudioCue.EventTokens`)—never one sustained tone for the whole
utterance. `voice.state`/`voice.babble` are its read-back/debug-trigger verbs.
HUD text uses the text-aware source below rather than estimating a syllable
count. A babbling identity has no live-body correlation yet, so every syllable
voices listener-placed rather than at a resolved world position—the same
simplification `WorldAudioDirector.SubmitEmbellishment` already takes for a
fire-time-chosen patch with no world site.

Built on `Puck.Hosting` (which itself carries `Puck.Abstractions` and
`Puck.Commands`) plus `Puck.Maths` for every fixed-point primitive; nothing
else. In particular, `Puck.Audio` parses no document—a `puck.synthesizer-patch.v1`
patch crosses into the mixer's `VoicePatch` struct through the host's patch
factory, which stays in `Puck.World` alongside the rest of the presentation glue (the tune host, the
render device) documented in [`Puck.World`'s Audio/ folder](../../src/Puck.World/Audio/README.md).

## Text speech

A HUD `Text` element may author a `voice`, reusing `WorldVoiceProfile` and a
declared `patches` row referencing a `puck.synthesizer-patch.v1` document:

```puck
{ id: "note", kind: Text, rect { x: 0, y: 0, width: 1, height: 1 }, style: Primary,
  "template": "{state.guide.note}",
  voice { patchId: "gallery-guide", cadenceTicks: 16, pitchThousandths: 1100, formantThousandths: 1150, paceThousandths: 1250 } }
```

The patch owns oscillator, pitch, envelope, sweep and vibrato. The voice's
`pitchThousandths` scales that pitch; `formantThousandths` shifts vowel
resonances; `paceThousandths` scales speed. Each scale is 250..4000, with
1000 neutral. `cadenceTicks` is 1..240 ticks at the audio clock's 240 Hz,
independent of the world's simulation rate. One character's interval is
`cadenceTicks * 200 * 1000 / paceThousandths` audio frames, using integer
division at 48 kHz.

`WorldHudSpeechFeed` observes the visible panels. A visible arrival, resolved
text change or voice change speaks once; a hidden, removed or empty element
stops. Literal `text`, `binding` and `template` resolve through the same HUD
binding resolver and template grammar the screen uses. The debug guide's note
and the nearby map sign use this hook in `worlds/debug/guide.puck`, behind
the existing `addonHud` toggle. The sign reads the same directions the map
board embosses, in a lower voice.

`TextSpeechSource` pulls the existing `VoiceSynth` at exact character
boundaries. Each Unicode scalar has an interval: vowels select two band-pass
formants (Y uses I); consonants add a short seeded noise onset to a neutral
vowel. Digits use that neutral syllable. Spaces and other non-alphanumeric
characters rest; comma/semicolon/colon rest for two intervals, sentence
punctuation and newline for three. A syllable releases within three quarters
of its interval, leaving a quiet tail even between adjacent letters. Noise
seeds derive from the text and character ordinal. No wall clock, frame delta
or utterance history enters the schedule or samples. The director binds the
source into its existing mixer as listener-placed speech, with four concurrent
utterances; a fifth replaces the oldest slot. `voice.state` adds `textStarted`
and `textSources` to its read-back.

The operator verb `audio.render <panelId> <elementId> <output.wav>` exports a fresh utterance
through the same source as 48 kHz stereo s16 PCM, without a device. It resolves
the live text but ignores panel visibility, so a headless World can render a
hidden guide or sign. It exports the voice before the mixer's master gain and
spatialization. For example, in a debug World:

```text
body.pose spawn:shape-sphere 0
world.wait 4
audio.render guide note speech-guide.wav
audio.render gallery-sign directions speech-sign.wav
```

Two exports of unchanged text and voice are byte-identical. The Windows
`WorldAudioRenderService` remains the device governor; offline export drives
the source directly through `WorldSpeechRenderer`. Authored syllable sample
sets remain the second source, tracked in [open items](../plans/open-items.md).

## Layering

`Puck.Audio` sits on Engine services, same row as `Puck.Physics`, with an
exact-equality closure the architecture gate enforces:
`Puck.Abstractions;Puck.Assets;Puck.Commands;Puck.Hosting;Puck.Maths`—no
`Puck.World.Authoring` edge (no document parsing), no presentation
or backend row. The authoritative server references it directly (its machine
host boots every machine at `MachineAudioRate.SampleRate`).

## Verifying

`dotnet build Puck.slnx -c Release` plus `puck architecture` (the exact
closure above); `dotnet test tests/Puck.Audio.Tests` for the mixer/synth and
`MusicClock`/`MusicDirector`/`VoiceBabbler` laws. Behavioral verification runs
the real windowed `Puck.World` and reads
`audio.state`/`audio.emitters`/`speaker.state`/`voice.state`/`music.state`
over stdin—there is no build-only gate for a world-audio feature.
`tests/Puck.World.Canaries/voice-babble` proves the playback wiring end to
end: triggering an identity's babble fires four DISTINCT syllable triggers
(never collapsing to one) and the mix produces measurable signal
(`audio.state`'s `peak`); the discriminating leg (no trigger) proves neither
ever happens on its own.
It declares the `audio-output` capability, so on a machine with no default
render endpoint (`audio.state` reports `device=silent` with an endpoint fault, or
`device=unsupported` where no platform backend exists) the leg is reported
unsupported rather than failed; a device that opens but produces no signal still
fails.
A region crossing fires one segment transition, and the transition fires the
`music.transition` cue token (`WorldServer.MusicTransitionTap`, wired to
`WorldAudioDirector.SubmitCue` in `WorldPostBuildWiring`)—the crossing a
world's `audio.cues` table binds a stinger patch to. No canary or test proves
that crossing end to end.
`tests/Puck.World.Canaries/music-conditional-layer-and-embellishment` proves a
director embellishment fires exactly once off a matching sense edge (the
`music.embellishment` cue token, keyed by the embellishment's own authored
patch id—never an `audio.cues` row, since that table can only bind one
patch per token) and that an unconditional layer stays active throughout; a
conditional layer's own per-tick activation is proved with exact tick control
by `MusicDirectorTests` and `MusicReplayReDerivabilityLawTests`, not
re-proved against a canary's wall-clock-uncertain pacing.

## Documentation

- [API reference](../api/index.md)
- [Engine overview](../overview.md)
- [Contributing to Puck](../development/contributing.md)
