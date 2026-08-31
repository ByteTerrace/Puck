using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The audio sections' READ-BACK + LEVER surface: <c>world.speakers</c> (the document rows), <c>audio.state</c> +
/// <c>speaker.state</c> (live device/per-row status), <c>audio.emitters</c> (the derived emitter table),
/// <c>voice.state</c> + <c>voice.babble</c> (the live voice-babble status and its debug/test trigger — see
/// <see cref="WorldAudioDirector.TriggerBabble"/>), <c>world.volume</c> (the master-volume session lever), and
/// <c>music.state</c> + <c>judge.state</c> (the live music clock/director state and the declared judge window sets,
/// both routed through seat 1's currently claimed <see cref="WorldAuthorityEndpoint.Submissions"/> — never the boot
/// instance's own injected link directly — so the answer tracks a transferred seat the same way
/// <see cref="PlayerCommandModule"/>'s drive-a-player verbs do). This module WRITES no DOCUMENT section: the four
/// document sections it reads are authored through the general <see cref="WorldRowCommandModule"/> —
/// <c>world.row.set</c>/<c>world.row.remove</c> over <c>speakers</c>/<c>tunes</c>/<c>patches</c>, and
/// <c>world.row.set audio &lt;json&gt;</c> for the keyless defaults row (<c>music</c>/<c>judges</c> are boot-only —
/// no live write door exists yet) — so no section is reachable through two doors. <c>voice.babble</c> is the one
/// exception, and a narrower one: it mutates only the director's own presentation-side scheduled-trigger state
/// (never a document field, nothing <c>world.save</c> folds back), so it carries no grant check — there is nothing
/// to gate a session lever over. A SEPARATE module from <see cref="WorldMutationCommandModule"/> to keep every
/// class under its analyzer ceilings.
/// </summary>
/// <remarks><c>world.volume</c> is a session LEVER rather than a mutation, but the same grant discipline reaches it:
/// it routes through <c>WorldServer.ApplySessionLever</c>, which applies the per-section
/// <see cref="WorldCapability.Mutate"/> check over <c>section:audio</c> — the section the lever folds into — before
/// the gain moves, exactly as <c>world.shadows</c>, <c>world.ao</c>, and <c>world.target</c> do. A principal whose
/// <c>mutate section:audio</c> grant has been revoked has its volume change refused, not applied; on accept,
/// <c>world.save</c> folds the live gain into the document's <c>audio.masterGain</c>.</remarks>
internal sealed class WorldAudioCommandModule(WorldServer server, IServerLink link, WorldAudioDirector director, Audio.WorldAudioRenderService device, WorldSeatAuthorityRouter seatRouter) : ICommandModule {
    // Answers a world-scoped audio query off seat 1's CURRENTLY CLAIMED authority (WorldSeatAuthorityRouter's own
    // one-writer table), never the boot instance's injected link directly — a seat transferred by a corner crossing
    // is routed to its new authority the same way PlayerCommandModule's drive-a-player verbs are. Seat 1's route is
    // published for every boot, so this never throws for want of a claim. The query carries the routed endpoint's
    // OWN 1-based entity index (never the local roster display number) — its Observe grant check narrows to that
    // body, the one subject a routed seat is always seeded with, so a transferred seat never needs a standing
    // world-wide grant just to read music/judge state.
    private CommandResult RoutedQuery(Func<int, WorldQuery> query) => (seatRouter.TryRouteQuery(
        factory: query,
        result: out var result,
        slot: 0,
        tagInstance: false
    )
        ? result
        : CommandResult.Error(output: "[query: seat 1 has no authority claim]")
    );
    // The world.speakers listing: one segment per declared row off the LIVE definition, so a speaker mutation's new
    // source narrates honestly, the same live-definition read world.screens uses.
    private CommandResult SpeakersHandler(CommandContext context, WireArgs args) {
        if (CommandResult.RequireNoArguments(args: args, verb: "world.speakers") is { } refusal) {
            return refusal;
        }

        var speakers = server.Definition.Speakers;

        if (speakers.Count == 0) {
            return new CommandResult(Output: "[world.speakers: none declared]");
        }

        var builder = new StringBuilder(value: "[world.speakers:");

        for (var index = 0; (index < speakers.Count); index++) {
            var speaker = speakers[index];
            var kind = (speaker switch {
                WorldSpeaker.Bed => "bed",
                WorldSpeaker.Anchored => "anchored",
                _ => "fixed",
            });
            var source = (speaker.Feed.Source switch {
                WorldSpeakerSource.Machine machine => $"machine:{machine.ScreenIndex}",
                WorldSpeakerSource.Tune tune => $"tune:{tune.TuneId}",
                WorldSpeakerSource.Synth synth => $"synth:{synth.PatchId}",
                _ => "none",
            });

            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"{((index == 0)
                ? " "
                : " | ")}{speaker.Name} {kind} {source} {speaker.Feed.Channel} gain={speaker.Feed.Gain:0.###}"
            );
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }
    // The audio.state echo: device lifecycle facts off the render service (its own counters are cross-thread-safe
    // reads), mixer meters off the service-owned mixer, and the derived-emitter count off the director. The fault
    // detail is a free-form tail so its spaces never split the machine-read fields before it.
    private CommandResult StateHandler(CommandContext context, WireArgs args) {
        if (CommandResult.RequireNoArguments(args: args, verb: "audio.state") is { } refusal) {
            return refusal;
        }

        var mixer = device.Mixer;

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[audio.state: device={device.StateToken} frames={device.FramesDelivered} rebinds={device.RebindAttempts} fillFaults={device.FillFaults} sources={mixer.BoundSourceCount} voices={mixer.Synth.ActiveVoiceCount} peak={mixer.OutputPeak} droppedTriggers={mixer.DroppedTriggerCount} emitters={director.EmitterCount} fault={(device.Fault ?? "none")}]"
        ));
    }
    // The voice.babble debug/test trigger: no game-facing producer estimates a syllable count from dialogue/caption
    // text yet (see WorldAudioDirector.TriggerBabble's own remarks), so this verb is the mechanism's one call site
    // until a real one lands. <identityId> <syllableCount> <utteranceOrdinal>. Echoes its OWN confirmation (never
    // voice.state's bracket text) so the two verbs' occurrences are never conflated by a piped proof.
    private CommandResult BabbleHandler(CommandContext context, WireArgs args) {
        if (
            (args.Count != 3) ||
            (args[0].Length == 0) ||
            !args.TryInt(
            index: 1,
            value: out var syllableCount
        ) ||
            (syllableCount < 0) ||
            !args.TryUnsignedDigits(
            index: 2,
            value: out var utteranceOrdinal
        )
        ) {
            return CommandResult.Error(output: "[voice.babble: expected <identityId> <syllableCount> <utteranceOrdinal>]");
        }

        var identityId = args[0].ToString();

        director.TriggerBabble(
            identityId: identityId,
            syllableCount: syllableCount,
            utteranceOrdinal: utteranceOrdinal
        );

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[voice.babble: identity={identityId} syllables={syllableCount} utterance={utteranceOrdinal}]"
        ));
    }
    // The world.instrument-clock lever: 0/1 engages the session lever for the ACTING principal's own seat
    // (presentation echo only — IWorldInstrumentClockLever's own remarks; the simulation-side clock fold is gated
    // by holding the screen application itself, never by this lever); no argument reads the acting seat's echo.
    private CommandResult InstrumentClockHandler(CommandContext context, WireArgs args) {
        var principal = context.ActingPrincipal();
        var seat = principal.Index;

        if (args.Count == 0) {
            return new CommandResult(Output: $"[world.instrument-clock: seat {seat} {(director.InstrumentClockEngaged(seat: seat) ? "engaged" : "disengaged")}]");
        }

        if (
            (args.Count != 1) ||
            !args.TryFloat(
            index: 0,
            value: out var flag
        )
        ) {
            return CommandResult.Error(output: "[world.instrument-clock: expected 0 or 1]");
        }

        link.SubmitSessionLever(
            lever: new WorldSessionLever(
                A: flag,
                Name: WorldSessionLevers.InstrumentClock,
                Seat: seat,
                Section: WorldSection.Audio
            ),
            principal: principal
        );

        return new CommandResult(Output: $"[world.instrument-clock: seat {seat} {(director.InstrumentClockEngaged(seat: seat) ? "engaged" : "disengaged")}]");
    }
    // The world.volume lever: one float argument engages the session lever (bounded by the shared audio gain
    // ceiling); no argument reads the effective volume and which side owns it.
    private CommandResult VolumeHandler(CommandContext context, WireArgs args) {
        if (args.Count == 0) {
            return new CommandResult(Output: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"[world.volume: {director.EffectiveMasterVolume:0.###} ({(director.MasterVolumeLeverEngaged
                ? "session lever; world.save folds"
                : "document audio.masterGain")})]"
            ));
        }

        if (
            (args.Count != 1) ||
            !args.TryFloat(
            index: 0,
            value: out var volume
        ) ||
            (volume < 0f) ||
            (volume > Puck.Forge.Authoring.CreationSoundDocument.MaxLevel)
        ) {
            return CommandResult.Error(output: $"[world.volume: expected one value within [0, {Puck.Forge.Authoring.CreationSoundDocument.MaxLevel}]]");
        }

        // Routed, not written: the server checks Mutate over section:audio — the section this lever folds into — and the
        // client applies it on accept (WorldServer.ApplySessionLever). Writing director.SetMasterVolume straight through
        // from here is the ungated-lever idiom this replaces. Over loopback DeliverSessionLever runs synchronously
        // inside SubmitSessionLever, so the echo below — read only AFTER the submit call returns, not before or
        // independently of it — honestly reports the unchanged volume when the lever was denied.
        link.SubmitSessionLever(
            lever: new WorldSessionLever(
                Section: WorldSection.Audio,
                Name: WorldSessionLevers.MasterVolume,
                A: volume
            ),
            principal: context.ActingPrincipal()
        );

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.volume: {director.EffectiveMasterVolume:0.###} ({(director.MasterVolumeLeverEngaged
            ? "session lever; world.save folds into audio.masterGain"
            : "document audio.masterGain — lever refused")})]"
        ));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.speakers",
            description: "Lists every declared speaker, one segment each — name, kind (fixed|anchored|bed), source token (none|machine:<slot>|tune:<id>|synth:<id>), channel, and gain. The document rows (the LIVE definition); audio.emitters lists the derived emitter table. A query — always echoes.",
            handler: SpeakersHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "audio.state",
            description: "Echoes the live speaker-device state (the device-state half; speaker.state joins it with per-row facts): device token (playing|silent|rebinding|unsupported|stopped), last fault, frames delivered across device generations, rebind attempts, fill faults, bound mixer sources, live synth voices, the running output peak (monotone — nonzero proves the mix has produced signal), dropped triggers, and derived emitters. A query — always echoes.",
            handler: StateHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "speaker.state",
            description: "Echoes every speaker row's, every placement Emission facet's, AND every active music-layer bed's LIVE status (the per-row runtime half beside audio.state's device facts): kind, source token, binding status (bound | silent(no-machine|no-tune|no-device|no-source) | faulted(no-patch)), the last published resolved position (unresolved for an absent anchor — an inactive Attach carrier included), and inMix=y|n (whether the listener sits inside the row's finite support), plus the live transient-cue tail (cue:<token>=<patch>, an embellishment included) and the monotone last-fired tail (lastCue:<token>=<patch>, never reset — the fact that proves a cue fired without racing the live pool's expiry). A query — always echoes.",
            handler: (context, args) => ((CommandResult.RequireNoArguments(args: args, verb: "speaker.state") is { } refusal)
            ? refusal
            : new CommandResult(Output: director.DescribeSpeakerState()))
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.volume",
            description: "The master-volume SESSION lever (the render-levers asymmetry): world.volume <0..8> applies the live mix gain NOW and owns it for the session (world.save folds it into audio.masterGain; world.status names 'audio' drift); no argument reads the effective volume. Until first engaged, the document's audio.masterGain flows live. A query/lever — always echoes.",
            handler: VolumeHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.instrument-clock",
            description: "The instrument-clock SESSION lever, for the acting principal's own seat: world.instrument-clock <0|1> engages/disengages it now; no argument reads the acting seat's current echo. Presentation echo only — the simulation-side clock fold music.state's boundary-derived fields depend on is gated by holding the screen application itself (body.engage), never by this lever; see instrument.state. A query/lever — always echoes.",
            handler: InstrumentClockHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "audio.emitters",
            description: "Dumps the derived audio emitter table, one segment each — stable id, key (speaker:<name>|scene:<id>|placement:<id>|sound:<placement>:<name>), kind, source token, channel, gain, and support radii. Deterministic document-derived facts (never live poses), so a piped proof asserts the derivation. A query — always echoes.",
            handler: (context, args) => ((CommandResult.RequireNoArguments(args: args, verb: "audio.emitters") is { } refusal)
            ? refusal
            : new CommandResult(Output: director.DescribeEmitters()))
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "voice.state",
            description: "Echoes the live voice-babble status (presentation-derived, beside audio.state's device facts and speaker.state's per-row facts): the delivered definition's identity id (or none), its authored voice selectors (none | patch:<id>/cadence:<ticks>), how many syllable triggers remain scheduled for the current utterance, how many voice.babble cue transients are currently live, and the cumulative fired count (monotone — never resets). A query — always echoes.",
            handler: (context, args) => ((CommandResult.RequireNoArguments(args: args, verb: "voice.state") is { } refusal)
            ? refusal
            : new CommandResult(Output: director.DescribeVoiceState()))
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "voice.babble",
            description: "DEBUG/TEST TRIGGER (no game-facing producer estimates syllables from text yet — see WorldAudioDirector.TriggerBabble): voice.babble <identityId> <syllableCount> <utteranceOrdinal> schedules one babbled utterance's syllable triggers off the delivered definition's authored voice profile, echoing what it scheduled — read voice.state separately for live status. A lever — always echoes.",
            handler: BabbleHandler
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "music.state",
            description: "Reads the live music clock/director state authoritatively off seat 1's currently claimed authority — the current segment, any pending transition, elapsed clock ticks, transition count, the tick/from/to of the most recent committed transition (none= before the first one, or when the world declares no music), the currently active conditional-layer tune ids, and the patch/tick of the most recent director embellishment. Follows a transferred seat the same way body.where does, so it answers correctly whether that authority is local or remote. A query — always echoes.",
            valueKind: CommandValueKind.Digital,
            handler: _ => RoutedQuery(
                query: static index => new WorldQuery.MusicState(Index: index)
            ),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "judge.state",
            description: "Reads the declared judge window sets, and the last judged grade/tick, authoritatively off seat 1's currently claimed authority. Follows a transferred seat the same way body.where does. A query — always echoes.",
            valueKind: CommandValueKind.Digital,
            handler: _ => RoutedQuery(
                query: static index => new WorldQuery.JudgeState(Index: index)
            ),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "instrument.state",
            description: "Reads which diegetic instrument screen (if any) the routed seat holds a screen application to, authoritatively off seat 1's currently claimed authority — screen index, whether the booted machine there carries the instrument-clock capability, its authored tempo in engine ticks per beat, and whether it is driving the world's music clock (holding the application is the whole gate — see world.instrument-clock). Follows a transferred seat the same way music.state/judge.state do. A query — always echoes.",
            valueKind: CommandValueKind.Digital,
            handler: _ => RoutedQuery(
                query: static index => new WorldQuery.InstrumentState(Index: index)
            ),
            routing: CommandRouting.Immediate
        );
    }
}
