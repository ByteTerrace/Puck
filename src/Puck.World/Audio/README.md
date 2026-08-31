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

## The diegetic instrument (`Puck.HumbleGamingBrick.Forge.Tune.TuneInstrumentEngine`/`TuneInstrumentMachine`)

A second, server-side host over the same `puck.audio.v1` → jukebox-cart
compile chain (`TuneRom.Build`), not in this folder (`Puck.HumbleGamingBrick.Forge/Tune/`,
reachable by `Puck.World.Server`'s `WorldMachineHost` without a dependency on
this composition-root project) and not built on `TuneMachineSource` — a
screen's declared `Machine` source names engine id `tune-instrument`, whose
content is a `puck.audio.v1` document rather than a cartridge ROM;
`TuneInstrumentEngine.Create` parses/validates/normalizes it and boots a real
`Puck.HumbleGamingBrick.MachineHost` from the compiled cart, so the instrument
gets a genuine diegetic screen, real `IAudioMachine` output, and pad-driven
`Step` — the same tick-authoritative, engageable machine any `gaming-brick`
screen is, never the presentation-side pull-driven core `TuneMachineSource`
wraps for a passive background tune. It additionally implements
`Puck.Abstractions.Machines.IInstrumentClockSource`
(`TicksPerBeat`, derived from the document's own `Tempo`): while a seat holds
the screen application, `Server.WorldServer.InstrumentClockBoundary` folds
that tempo into the world's own `MusicClock` boundary each tick — see that
method's own remarks for why this is gated by holding the application rather
than by a session lever (`WorldSessionLever`'s own remarks: presentation-only,
never a simulation input).

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

## The music-transition cue lane

A committed `MusicDirector` segment transition fires the `music.transition`
cue token, listener-placed. The wiring lives outside this folder —
`WorldServer.MusicTransitionTap` (`Puck.World.Server`, invoked from
`WorldServer.Step`'s music-step call site) reaches `WorldAudioDirector.SubmitCue`
through the same tap-and-wiring shape `WorldPostBuildWiring` uses for the
edit-echo and machine-lifecycle cue lanes. A world authors a stinger off it by
declaring a `patches` row and an `audio.cues` row naming
`"event": "music.transition"` and that patch's id.

## The conditional-layer and embellishment lanes

A segment's conditional audio layers (`MusicDirector.ActiveLayerTuneIds`) and
director embellishments (`LastEmbellishmentPatchId`/`LastEmbellishmentTick`)
each reach the client through their own tap-and-wiring pair, both wired in
`WorldPostBuildWiring` beside the music-transition lane above:

- `WorldServer.MusicLayerTap` fires on any tick the active-layer TUNE ID SET
  changes (level-triggered — unlike a transition, this is not gated to a
  commit tick), reaching `WorldAudioDirector.SetActiveMusicLayers`, which
  re-runs `ReconcileSpeakers` against the cached definition so the derived
  plan tracks it immediately. `DeriveMusicLayers` (in `WorldAudioDirector`,
  called from `ReconcileSpeakers`) admits one continuous
  `AudioEmitterKind.Bed` emitter per active tune id, anchored at the world
  origin with a support radius engineered to always read full presence (a
  music layer carries no world position) — so a layer entering/leaving
  cross-fades over the Bed kind's existing presence-gain slew rather than a
  hard cut. `speaker.state` echoes each as a `musicLayer:<tuneId>` row
  alongside every `speaker:`/`placement:` row.
- `WorldServer.MusicEmbellishmentTap` fires the tick an embellishment fires,
  carrying its own resolved patch id — unlike `music.transition`, this cannot
  ride `WorldAudioDirector.SubmitCue`'s token→row lookup, because an
  embellishment's patch is chosen PER EMBELLISHMENT in the `puck.music.v1`
  document, not by one fixed `audio.cues` row shared across every firing.
  `WorldAudioDirector.SubmitEmbellishment` fires the transient directly
  (listener-placed, like `music.transition`), recorded under the
  `music.embellishment` cue token so `speaker.state`'s live transient-cue
  tail reads it the same way every other cue reads. Every cue firing also
  writes `speaker.state`'s monotone `lastCue:<token>=<patch>` tail (never
  reset), the fact a proof reads to show a cue fired without racing the
  live transient pool's expiry.

Neither a layer's nor an embellishment's authored `gainThousandths` reaches
presentation gain yet — see `Puck.World.Authoring.MusicLayerDocument
.GainThousandths`'s remarks.

## The voice-babble playback lane

`WorldAudioDirector.TriggerBabble` (`../Client/WorldAudioDirector.cs`, not in
this folder) is the production path over `Puck.Audio.Simulation.VoiceBabbler`:
given an identity id, an estimated syllable count, and an utterance ordinal,
it resolves the delivered definition's single `WorldIdentityDefinition.Voice`
selectors (`PatchId`/`CadenceTicks`), drives `VoiceBabbler.ComputeTriggerTicks`
for the utterance's cadence-jittered per-syllable tick schedule, and stages one
`FireBabbleSyllable` firing per syllable at its own deterministic delay —
never one sustained tone for the whole utterance. Each firing rides the same
transient-cue mechanism `SubmitEmbellishment` uses (a short-lived listener-
placed emitter plus one seeded `VoiceSynth` trigger), recorded under the
reserved `voice.babble` cue token so `speaker.state`'s live transient-cue tail
reads it the same way every other cue reads. Every syllable's seed folds the
identity id, the utterance ordinal, and the syllable index — never wall-clock
— so a babbled utterance reproduces bit-identically across runs. No producer
yet estimates a syllable count from dialogue/caption text (a presentation/
content concern), and a babbling identity has no live-body correlation yet, so
every syllable voices listener-placed rather than at a resolved world
position — `voice.babble` is the debug/test call site until a real one lands.
`voice.state` echoes the live status: the delivered identity's voice
selectors, how many syllable triggers remain scheduled, how many
`voice.babble` cue transients are currently live, and the cumulative fired
count (monotone).

## Verifying

Run the game and read `audio.state`, `audio.emitters`, `speaker.state`,
`voice.state`, `music.state`, and `instrument.state` over stdin; the mixer
core's own proofs, if any, live beside it in `Puck.Audio`, not here.
`tests/Puck.World.Canaries/music-conditional-layer-and-embellishment` proves
the embellishment and unconditional-layer lanes through the real windowed
composition root; `tests/Puck.World.Canaries/instrument-clock-source` proves
the diegetic-instrument engage → `world.instrument-clock` → clock-fold path
the same way; `tests/Puck.World.Canaries/voice-babble` proves the babble
playback lane fires four distinct syllable triggers (never one sustained
tone) and that the mix measurably produces signal.
