using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The audio sections' verb surface — the dev reflection of the mutation vocabulary (<c>world.speaker.*</c>,
/// <c>world.tune.*</c>, <c>world.patch.*</c>, <c>world.audio.set</c>: the inline-JSON Row pattern over the SAME
/// <see cref="WorldMutation"/> messages) plus the two listings: <c>world.speakers</c> (the document rows) and
/// <c>audio.emitters</c> (the derived emitter table — the document→emitter derivation made assertable). A SEPARATE
/// module from <see cref="WorldMutationCommandModule"/> to keep every class under its analyzer ceilings.
/// </summary>
/// <remarks>JSON arguments must be a single whitespace-free token (compact JSON) — the console tokenizer rule the
/// mutation module documents. Mutation verbs route <see cref="CommandRouting.Simulation"/> (the stdin barrier makes a
/// following listing read the settled state); the listings are queries.</remarks>
internal sealed class WorldAudioCommandModule(WorldServer server, IServerLink link, WorldAudioDirector director, Audio.WorldAudioRenderService device) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Row(
            name: "world.speaker.set",
            description: "Upserts a placeable speaker (whole-row, keyed by name) from one inline-JSON WorldSpeaker ($type fixed|anchored|bed): world.speaker.set <speaker-json>. The camera pair's audio sibling; the derived emitter keeps its id across property edits and re-enters from silence on a kind/anchor/source-identity change.",
            info: WorldJsonContext.Default.WorldSpeaker,
            toMutation: static speaker => new WorldMutation.UpsertSpeaker(Principal: WorldPrincipal.Console, Speaker: speaker)
        );
        yield return Simulation(
            name: "world.speaker.remove",
            description: "Removes a speaker by name: world.speaker.remove <name>.",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.speaker.remove", form: "<name>");
                }

                return Submit(mutation: new WorldMutation.RemoveSpeaker(Principal: WorldPrincipal.Console, Name: args[0]));
            }
        );
        yield return Row(
            name: "world.tune.set",
            description: "Upserts a tune ASSET row (whole-row, keyed by id) from one inline-JSON WorldTune {id, document, hash}: world.tune.set <json>. The compose boundary re-canonicalizes the puck.audio.v1 document and REJECTS a hash the pipeline did not itself compute (the rejection names the canonical sha256).",
            info: WorldJsonContext.Default.WorldTune,
            toMutation: static tune => new WorldMutation.UpsertTune(Principal: WorldPrincipal.Console, Tune: tune)
        );
        yield return Simulation(
            name: "world.tune.remove",
            description: "Removes a tune asset row by id: world.tune.remove <id>. Rejected loudly while speakers still reference it (no cascade — the dependents are named).",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.tune.remove", form: "<id>");
                }

                return Submit(mutation: new WorldMutation.RemoveTune(Principal: WorldPrincipal.Console, Id: args[0]));
            }
        );
        yield return Row(
            name: "world.patch.set",
            description: "Upserts a synth-patch ASSET row (whole-row, keyed by id) from one inline-JSON WorldPatch {id, document, hash}: world.patch.set <json>. The puck.synth.v1 twin of world.tune.set — same canonicalize + hash-pin boundary.",
            info: WorldJsonContext.Default.WorldPatch,
            toMutation: static patch => new WorldMutation.UpsertPatch(Principal: WorldPrincipal.Console, Patch: patch)
        );
        yield return Simulation(
            name: "world.patch.remove",
            description: "Removes a synth-patch asset row by id: world.patch.remove <id>. Rejected loudly while speakers or emission facets still reference it (no cascade — the dependents are named).",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.patch.remove", form: "<id>");
                }

                return Submit(mutation: new WorldMutation.RemovePatch(Principal: WorldPrincipal.Console, Id: args[0]));
            }
        );
        yield return Row(
            name: "world.audio.set",
            description: "Replaces the audio host-section defaults from one inline-JSON WorldAudioDefaults {masterGain, defaultSpeakerRadius, defaultCurve, defaultBedFadeSeconds, listener, cues?}: world.audio.set <json>. Applies LIVE (the derivation coalescing, the listener policy, and the cue table read the delivered row; masterGain also flows live UNTIL the world.volume session lever engages — the lever then owns the live gain and masterGain owns the next boot).",
            info: WorldJsonContext.Default.WorldAudioDefaults,
            toMutation: static audio => new WorldMutation.SetAudioDefaults(Principal: WorldPrincipal.Console, Audio: audio)
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.speakers",
            description: "Lists every declared speaker, one segment each — name, kind (fixed|anchored|bed), source token (none|machine:<slot>|tune:<id>|synth:<id>), channel, and gain. The document rows (the LIVE definition); audio.emitters lists the derived emitter table. A query — always echoes.",
            handler: SpeakersHandler,
            echoesData: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "audio.state",
            description: "Echoes the live speaker-device state (the device-state half; speaker.state joins it with per-row facts): device token (playing|silent|rebinding|unsupported|stopped), last fault, frames delivered across device generations, rebind attempts, fill faults, bound mixer sources, live synth voices, the running output peak (monotone — nonzero proves the mix has produced signal), dropped triggers, and derived emitters. A query — always echoes.",
            handler: StateHandler,
            echoesData: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "speaker.state",
            description: "Echoes every speaker row's LIVE status (the per-row runtime half beside audio.state's device facts): kind, source token, binding status (bound | silent(no-machine|no-tune|no-device|no-source) | faulted(no-patch)), the last published resolved position (unresolved for an absent anchor), and inMix=y|n (whether the listener sits inside the row's finite support), plus the live transient-cue tail (cue:<token>=<patch>). A query — always echoes.",
            handler: (context, args) => ((args.Count != 0)
                ? new CommandResult(Output: "[speaker.state: no arguments — echoes every speaker row's live status]") { IsError = true }
                : new CommandResult(Output: director.DescribeSpeakerState())),
            echoesData: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.volume",
            description: "The master-volume SESSION lever (the render-levers asymmetry): world.volume <0..8> applies the live mix gain NOW and owns it for the session (world.save folds it into audio.masterGain; world.status names 'audio' drift); no argument reads the effective volume. Until first engaged, the document's audio.masterGain flows live. A query/lever — always echoes.",
            handler: VolumeHandler,
            echoesData: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "audio.emitters",
            description: "Dumps the derived audio emitter table, one segment each — stable id, key (speaker:<name>|scene:<id>|placement:<id>|sound:<placement>:<name>), kind, source token, channel, gain, and support radii. Deterministic document-derived facts (never live poses), so a piped proof asserts the derivation. A query — always echoes.",
            handler: (context, args) => ((args.Count != 0)
                ? new CommandResult(Output: "[audio.emitters: no arguments — dumps the derived emitter table]") { IsError = true }
                : new CommandResult(Output: director.DescribeEmitters())),
            echoesData: true
        );
    }

    // The world.volume lever: one float argument engages the session lever (bounded by the shared audio gain
    // ceiling); no argument reads the effective volume and which side owns it.
    private CommandResult VolumeHandler(CommandContext context, WireArgs args) {
        if (args.Count == 0) {
            return new CommandResult(Output: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"[world.volume: {director.EffectiveMasterVolume:0.###} ({(director.MasterVolumeLeverEngaged ? "session lever; world.save folds" : "document audio.masterGain")})]"
            ));
        }

        if ((args.Count != 1) ||
            !float.TryParse(s: args[0], style: System.Globalization.NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var volume) ||
            !float.IsFinite(f: volume) || (volume < 0f) || (volume > Puck.Authoring.CreationSoundDocument.MaxLevel)) {
            return new CommandResult(Output: $"[world.volume: expected one value within [0, {Puck.Authoring.CreationSoundDocument.MaxLevel}]]") {
                IsError = true,
            };
        }

        director.SetMasterVolume(value: volume);

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.volume: {volume:0.###} (session lever; world.save folds into audio.masterGain)]"
        ));
    }

    // The audio.state echo: device lifecycle facts off the render service (its own counters are cross-thread-safe
    // reads), mixer meters off the service-owned mixer, and the derived-emitter count off the director. The fault
    // detail is a free-form tail so its spaces never split the machine-read fields before it.
    private CommandResult StateHandler(CommandContext context, WireArgs args) {
        if (args.Count != 0) {
            return new CommandResult(Output: "[audio.state: no arguments — echoes the live speaker-device state]") {
                IsError = true,
            };
        }

        var mixer = device.Mixer;

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[audio.state: device={device.StateToken} frames={device.FramesDelivered} rebinds={device.RebindAttempts} fillFaults={device.FillFaults} sources={mixer.BoundSourceCount} voices={mixer.Synth.ActiveVoiceCount} peak={mixer.OutputPeak} droppedTriggers={mixer.DroppedTriggerCount} emitters={director.EmitterCount} fault={device.Fault ?? "none"}]"
        ));
    }

    // The world.speakers listing: one segment per declared row off the LIVE definition, so a speaker mutation's new
    // source narrates honestly, the same live-definition read world.screens uses.
    private CommandResult SpeakersHandler(CommandContext context, WireArgs args) {
        if (args.Count != 0) {
            return new CommandResult(Output: "[world.speakers: no arguments — lists every declared speaker]") {
                IsError = true,
            };
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
                handler: $"{((index == 0) ? " " : " | ")}{speaker.Name} {kind} {source} {speaker.Feed.Channel} gain={speaker.Feed.Gain:0.###}"
            );
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }

    // The Row/Simulation/Submit helpers duplicate WorldMutationCommandModule's: each module owns its own copies to
    // stay under the analyzer ceiling.
    private CommandDefinition Row<T>(string name, string description, JsonTypeInfo<T> info, Func<T, WorldMutation> toMutation) {
        return CommandDefinition.WithTrailingArgs(
            name: name,
            description: description,
            handler: (context, args) => {
                var raw = RawArgument(context: context, args: args);

                if (!TryParseJson(json: raw, info: info, value: out var value, error: out var error)) {
                    return new CommandResult(Output: $"[{name}: {error}]");
                }

                return Submit(mutation: toMutation(arg: value));
            },
            routing: CommandRouting.Simulation
        );
    }

    private static CommandDefinition Simulation(string name, string description, Func<CommandContext, string[], CommandResult> handler) {
        return CommandDefinition.WithTrailingArgs(name: name, description: description, handler: handler, routing: CommandRouting.Simulation);
    }

    private CommandResult Submit(WorldMutation mutation) {
        link.SubmitWorldMutation(mutation: mutation);

        return CommandResult.None;
    }

    private static CommandResult Usage(string verb, string form) {
        return new CommandResult(Output: $"[{verb}: expected {form}]") {
            IsError = true,
        };
    }

    private static string RawArgument(CommandContext context, string[] args) {
        if (context.Text is { } text) {
            var span = text.AsSpan().TrimStart();
            var separator = span.IndexOfAny(value0: ' ', value1: '\t');

            return ((separator < 0) ? string.Empty : span[(separator + 1)..].Trim().ToString());
        }

        return string.Join(separator: ' ', values: args);
    }

    private static bool TryParseJson<T>(string json, JsonTypeInfo<T> info, out T value, out string error) {
        value = default!;

        if (string.IsNullOrWhiteSpace(value: json)) {
            error = "expected a compact inline-JSON argument";

            return false;
        }

        try {
            if (JsonSerializer.Deserialize(json: json, jsonTypeInfo: info) is not { } parsed) {
                error = "the JSON parsed to null";

                return false;
            }

            value = parsed;
            error = string.Empty;

            return true;
        } catch (JsonException exception) {
            error = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }
}
