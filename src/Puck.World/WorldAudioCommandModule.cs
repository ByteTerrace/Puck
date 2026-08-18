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
/// <c>world.volume</c> (the master-volume session lever), and <c>music.state</c> + <c>judge.state</c> (the live
/// music clock/director state and the declared judge window sets, both routed through seat 1's currently claimed
/// <see cref="WorldAuthorityEndpoint.Submissions"/> — never the boot instance's own injected link directly — so the
/// answer tracks a transferred seat the same way <see cref="PlayerCommandModule"/>'s drive-a-player verbs do). This
/// module WRITES nothing: the four
/// document sections it reads are authored through the general <see cref="WorldRowCommandModule"/> —
/// <c>world.row.set</c>/<c>world.row.remove</c> over <c>speakers</c>/<c>tunes</c>/<c>patches</c>, and
/// <c>world.row.set audio &lt;json&gt;</c> for the keyless defaults row (<c>music</c>/<c>judges</c> are boot-only —
/// no live write door exists yet) — so no section is reachable through two doors. A SEPARATE module from
/// <see cref="WorldMutationCommandModule"/> to keep every class under its analyzer ceilings.
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
    private static CommandResult RoutedQuery(Func<int, WorldQuery> query, WorldAuthorityRoute route) {
        var result = default(CommandResult);

        route.Endpoint.Submissions.Query(
            query: query((route.EntityIndex + 1)),
            completion: answer => {
                result = new CommandResult(Output: answer.Text) { IsError = answer.Refused };
            }
        );

        return result;
    }
    // The world.speakers listing: one segment per declared row off the LIVE definition, so a speaker mutation's new
    // source narrates honestly, the same live-definition read world.screens uses.
    private CommandResult SpeakersHandler(CommandContext context, WireArgs args) {
        if (args.Count != 0) {
            return CommandResult.Error(output: "[world.speakers: no arguments — lists every declared speaker]");
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
        if (args.Count != 0) {
            return CommandResult.Error(output: "[audio.state: no arguments — echoes the live speaker-device state]");
        }

        var mixer = device.Mixer;

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[audio.state: device={device.StateToken} frames={device.FramesDelivered} rebinds={device.RebindAttempts} fillFaults={device.FillFaults} sources={mixer.BoundSourceCount} voices={mixer.Synth.ActiveVoiceCount} peak={mixer.OutputPeak} droppedTriggers={mixer.DroppedTriggerCount} emitters={director.EmitterCount} fault={(device.Fault ?? "none")}]"
        ));
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
            !float.TryParse(
            s: args[0],
            style: System.Globalization.NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out var volume
        ) ||
            !float.IsFinite(f: volume) ||
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
                Kind: WorldLeverKind.MasterVolume,
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
            description: "Echoes every speaker row's AND every placement Emission facet's LIVE status (the per-row runtime half beside audio.state's device facts): kind, source token, binding status (bound | silent(no-machine|no-tune|no-device|no-source) | faulted(no-patch)), the last published resolved position (unresolved for an absent anchor — an inactive Attach carrier included), and inMix=y|n (whether the listener sits inside the row's finite support), plus the live transient-cue tail (cue:<token>=<patch>). A query — always echoes.",
            handler: (context, args) => ((args.Count != 0)
            ? CommandResult.Error(output: "[speaker.state: no arguments — echoes every speaker row's live status]")
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
            name: "audio.emitters",
            description: "Dumps the derived audio emitter table, one segment each — stable id, key (speaker:<name>|scene:<id>|placement:<id>|sound:<placement>:<name>), kind, source token, channel, gain, and support radii. Deterministic document-derived facts (never live poses), so a piped proof asserts the derivation. A query — always echoes.",
            handler: (context, args) => ((args.Count != 0)
            ? CommandResult.Error(output: "[audio.emitters: no arguments — dumps the derived emitter table]")
            : new CommandResult(Output: director.DescribeEmitters()))
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "music.state",
            description: "Reads the live music clock/director state authoritatively off seat 1's currently claimed authority — the current segment, any pending transition, elapsed clock ticks, transition count, and the tick/from/to of the most recent committed transition (none= before the first one, or when the world declares no music). Follows a transferred seat the same way player.where does, so it answers correctly whether that authority is local or remote. A query — always echoes.",
            valueKind: CommandValueKind.Digital,
            handler: _ => RoutedQuery(
                query: static index => new WorldQuery.MusicState(Index: index),
                route: seatRouter.Route(slot: 0)
            ),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "judge.state",
            description: "Reads the declared judge window sets, and the last judged grade/tick, authoritatively off seat 1's currently claimed authority. Follows a transferred seat the same way player.where does. A query — always echoes.",
            valueKind: CommandValueKind.Digital,
            handler: _ => RoutedQuery(
                query: static index => new WorldQuery.JudgeState(Index: index),
                route: seatRouter.Route(slot: 0)
            ),
            routing: CommandRouting.Immediate
        );
    }
}
