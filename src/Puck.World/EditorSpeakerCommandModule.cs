using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The speaker authoring numeric twins — <c>editor.speaker.place/move/gain/channel/radius/delete</c>,
/// name-addressed whole-row <see cref="WorldMutation.UpsertSpeaker"/>/<see cref="WorldMutation.RemoveSpeaker"/> acts
/// beside the selection-driven channel (speakers also select, drag, move, and delete through the selection/drag machinery).
/// CONSOLE-ONLY: every chord slot on the editor place page is already spoken for (grab/stamp/
/// cancel/snap on the face diamond, spawn ghosts + creation cycling on the D-pad), so the speaker verbs ride the
/// console/binding-data seam rather than evicting an existing act. A SEPARATE module for the analyzer ceilings.
/// </summary>
/// <remarks>Mutation verbs route <see cref="CommandRouting.Simulation"/> (the stdin barrier serializes a following
/// <c>world.speakers</c>/<c>speaker.state</c> read-after-write). Acts carry the ACTING SEAT principal and require the
/// seat in editor mode (the focus point places, and grant denials land on the seat that asked).</remarks>
internal sealed class EditorSpeakerCommandModule(WorldEditorSession session, WorldClient client, IServerLink link) : ICommandModule {
    private readonly WorldEditorSession m_session = session;
    private readonly WorldClient m_client = client;
    private readonly IServerLink m_link = link;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Simulation(
            name: "editor.speaker.place",
            description: "Places a NEW fixed speaker at the editor focus point as one mutation: editor.speaker.place <name> <none|machine:<slot>|tune:<id>|synth:<patchId>> [radius] [seat]. The feed boots mix-channel at unity gain; radius sets an explicit attenuation (else the audio defaults coalesce). Tune/patch/screen references validate at apply.",
            handler: PlaceHandler
        );
        yield return Simulation(
            name: "editor.speaker.move",
            description: "Moves a speaker by name to an ABSOLUTE point as one whole-row mutation: editor.speaker.move <name> <x> <y> <z> [seat]. Fixed moves its position, a bed its center, an ANCHORED row its attachment offset (the documented v1 numeric channel — anchored rows do not drag).",
            handler: MoveHandler
        );
        yield return Simulation(
            name: "editor.speaker.gain",
            description: "Sets a speaker's feed gain: editor.speaker.gain <name> <gain> [seat]. One whole-row mutation; the gain ceiling validates at apply.",
            handler: (context, args) => FeedHandler(context: context, args: args, verb: "editor.speaker.gain", form: "<name> <gain>", apply: static (speaker, token) =>
                ((float.TryParse(s: token, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var gain) && float.IsFinite(f: gain))
                    ? WithFeed(speaker: speaker, feed: (speaker.Feed with { Gain = gain }))
                    : null))
        );
        yield return Simulation(
            name: "editor.speaker.channel",
            description: "Sets a speaker's stereo channel selector: editor.speaker.channel <name> <mix|left|right> [seat]. One whole-row mutation (stereo separation = two rows sharing a source with left/right selectors).",
            handler: (context, args) => FeedHandler(context: context, args: args, verb: "editor.speaker.channel", form: "<name> <mix|left|right>", apply: static (speaker, token) =>
                ((token is WorldSpeakerFeed.ChannelMix or WorldSpeakerFeed.ChannelLeft or WorldSpeakerFeed.ChannelRight)
                    ? WithFeed(speaker: speaker, feed: (speaker.Feed with { Channel = token }))
                    : null))
        );
        yield return Simulation(
            name: "editor.speaker.radius",
            description: "Sets a speaker's audible support radius: editor.speaker.radius <name> <radius> [seat]. Fixed/anchored rows write their attenuation radius (curve kept, else the default); a bed writes its outer radius. One whole-row mutation.",
            handler: RadiusHandler
        );
        yield return Simulation(
            name: "editor.speaker.delete",
            description: "Removes a speaker by name as one mutation: editor.speaker.delete <name> [seat].",
            handler: (context, args) => {
                var (name, slot, error) = ResolveNameAndSlot(context: context, args: args, extra: 0, verb: "editor.speaker.delete", form: "<name>");

                if (error is { } resolveError) {
                    return resolveError;
                }

                m_link.SubmitWorldMutation(mutation: new WorldMutation.RemoveSpeaker(Principal: WorldPrincipal.Seat(slot: slot), Name: name!));

                return Echo(slot: slot, verb: "editor.speaker.delete", detail: $"speaker '{name}' — remove submitted");
            }
        );
    }

    private CommandResult PlaceHandler(CommandContext context, string[] args) {
        if (args.Length is (< 2 or > 4)) {
            return Error(text: "[editor.speaker.place: expected <name> <none|machine:<slot>|tune:<id>|synth:<patchId>> [radius] [seat]]");
        }

        // The optional third token is a radius when numeric; the seat then rides fourth.
        var hasRadius = ((args.Length >= 3) && float.TryParse(s: args[2], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out _));
        var (slot, slotError) = EditorCommandModule.ResolveSlot(context: context, args: args, at: (hasRadius ? 3 : 2), verb: "editor.speaker.place");

        if (slotError is { } resolveError) {
            return resolveError;
        }

        if (Guard(slot: slot, verb: "editor.speaker.place") is { } guard) {
            return guard;
        }

        if (ParseSource(token: args[1]) is not { } source) {
            return Error(text: $"[editor.speaker.place: unknown source '{args[1]}' — none|machine:<slot>|tune:<id>|synth:<patchId>]");
        }

        WorldSpeakerAttenuation? attenuation = null;

        if (hasRadius) {
            if (!float.TryParse(s: args[2], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var radius) || !float.IsFinite(f: radius) || (radius <= 0f)) {
                return Error(text: $"[editor.speaker.place: bad radius '{args[2]}']");
            }

            attenuation = new WorldSpeakerAttenuation(Radius: radius, Curve: null);
        }

        var focus = m_session.Focus(slot: slot);
        var speaker = new WorldSpeaker.Fixed(
            Name: args[0],
            Position: focus,
            Feed: new WorldSpeakerFeed(Source: source, Channel: WorldSpeakerFeed.ChannelMix, Gain: 1f),
            Attenuation: attenuation
        );

        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertSpeaker(Principal: WorldPrincipal.Seat(slot: slot), Speaker: speaker));

        return Echo(slot: slot, verb: "editor.speaker.place", detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"speaker '{speaker.Name}' at ({focus.X:0.00}, {focus.Y:0.00}, {focus.Z:0.00}) — one mutation submitted"
        ));
    }

    private CommandResult MoveHandler(CommandContext context, string[] args) {
        var (name, slot, error) = ResolveNameAndSlot(context: context, args: args, extra: 3, verb: "editor.speaker.move", form: "<name> <x> <y> <z>");

        if (error is { } resolveError) {
            return resolveError;
        }

        if (!TryFloat(token: args[1], value: out var x) || !TryFloat(token: args[2], value: out var y) || !TryFloat(token: args[3], value: out var z)) {
            return Error(text: "[editor.speaker.move: could not parse <x> <y> <z> as finite numbers]");
        }

        if (Find(name: name!) is not { } speaker) {
            return Error(text: $"[editor.speaker.move: no speaker '{name}' in the live definition]");
        }

        var target = new Vector3(x: x, y: y, z: z);
        var moved = (speaker switch {
            WorldSpeaker.Fixed fixedSpeaker => (fixedSpeaker with { Position = target }),
            WorldSpeaker.Bed bed => (bed with { Center = target }),
            WorldSpeaker.Anchored anchored => (anchored with { Offset = target }),
            _ => speaker,
        });

        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertSpeaker(Principal: WorldPrincipal.Seat(slot: slot), Speaker: moved));

        return Echo(slot: slot, verb: "editor.speaker.move", detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"speaker '{name}' {((speaker is WorldSpeaker.Anchored) ? "offset" : "->")} ({x:0.00}, {y:0.00}, {z:0.00}) — one mutation submitted"
        ));
    }

    private CommandResult RadiusHandler(CommandContext context, string[] args) {
        var (name, slot, error) = ResolveNameAndSlot(context: context, args: args, extra: 1, verb: "editor.speaker.radius", form: "<name> <radius>");

        if (error is { } resolveError) {
            return resolveError;
        }

        if (!TryFloat(token: args[1], value: out var radius) || (radius <= 0f)) {
            return Error(text: $"[editor.speaker.radius: bad radius '{args[1]}']");
        }

        if (Find(name: name!) is not { } speaker) {
            return Error(text: $"[editor.speaker.radius: no speaker '{name}' in the live definition]");
        }

        var resized = (speaker switch {
            WorldSpeaker.Bed bed => (bed with { Radius = radius }),
            _ => WithAttenuation(speaker: speaker, attenuation: new WorldSpeakerAttenuation(Radius: radius, Curve: speaker.Attenuation?.Curve)),
        });

        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertSpeaker(Principal: WorldPrincipal.Seat(slot: slot), Speaker: resized));

        return Echo(slot: slot, verb: "editor.speaker.radius", detail: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"speaker '{name}' radius {radius:0.###} — one mutation submitted"
        ));
    }

    // The shared feed-field act: resolve name+seat, apply the field transform, submit the whole row.
    private CommandResult FeedHandler(CommandContext context, string[] args, string verb, string form, Func<WorldSpeaker, string, WorldSpeaker?> apply) {
        var (name, slot, error) = ResolveNameAndSlot(context: context, args: args, extra: 1, verb: verb, form: form);

        if (error is { } resolveError) {
            return resolveError;
        }

        if (Find(name: name!) is not { } speaker) {
            return Error(text: $"[{verb}: no speaker '{name}' in the live definition]");
        }

        if (apply(arg1: speaker, arg2: args[1]) is not { } changed) {
            return Error(text: $"[{verb}: bad value '{args[1]}']");
        }

        m_link.SubmitWorldMutation(mutation: new WorldMutation.UpsertSpeaker(Principal: WorldPrincipal.Seat(slot: slot), Speaker: changed));

        return Echo(slot: slot, verb: verb, detail: $"speaker '{name}' {args[1]} — one mutation submitted");
    }

    // Resolve "<name> <extra args...> [seat]" with the editing guard applied; name is args[0].
    private (string? Name, int Slot, CommandResult? Error) ResolveNameAndSlot(CommandContext context, string[] args, int extra, string verb, string form) {
        if ((args.Length < (1 + extra)) || (args.Length > (2 + extra))) {
            return (Name: null, Slot: 0, Error: Error(text: $"[{verb}: expected {form} [seat]]"));
        }

        var (slot, error) = EditorCommandModule.ResolveSlot(context: context, args: args, at: (1 + extra), verb: verb);

        if (error is { } resolveError) {
            return (Name: null, Slot: 0, Error: resolveError);
        }

        if (Guard(slot: slot, verb: verb) is { } guard) {
            return (Name: null, Slot: 0, Error: guard);
        }

        return (Name: args[0], Slot: slot, Error: null);
    }

    private WorldSpeaker? Find(string name) {
        foreach (var speaker in m_client.Definition.Speakers) {
            if (string.Equals(a: speaker.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return speaker;
            }
        }

        return null;
    }

    private static WorldSpeaker WithFeed(WorldSpeaker speaker, WorldSpeakerFeed feed) => speaker switch {
        WorldSpeaker.Fixed fixedSpeaker => (fixedSpeaker with { Feed = feed }),
        WorldSpeaker.Anchored anchored => (anchored with { Feed = feed }),
        WorldSpeaker.Bed bed => (bed with { Feed = feed }),
        _ => speaker,
    };

    private static WorldSpeaker WithAttenuation(WorldSpeaker speaker, WorldSpeakerAttenuation attenuation) => speaker switch {
        WorldSpeaker.Fixed fixedSpeaker => (fixedSpeaker with { Attenuation = attenuation }),
        WorldSpeaker.Anchored anchored => (anchored with { Attenuation = attenuation }),
        _ => speaker,
    };

    private static WorldSpeakerSource? ParseSource(string token) {
        if (string.Equals(a: token, b: "none", comparisonType: StringComparison.Ordinal)) {
            return new WorldSpeakerSource.None();
        }

        if (token.StartsWith(value: "machine:", comparisonType: StringComparison.Ordinal) &&
            int.TryParse(s: token.AsSpan(start: "machine:".Length), provider: CultureInfo.InvariantCulture, result: out var screenIndex)) {
            return new WorldSpeakerSource.Machine(ScreenIndex: screenIndex);
        }

        if (token.StartsWith(value: "tune:", comparisonType: StringComparison.Ordinal) && (token.Length > "tune:".Length)) {
            return new WorldSpeakerSource.Tune(TuneId: token["tune:".Length..]);
        }

        if (token.StartsWith(value: "synth:", comparisonType: StringComparison.Ordinal) && (token.Length > "synth:".Length)) {
            return new WorldSpeakerSource.Synth(PatchId: token["synth:".Length..]);
        }

        return null;
    }

    private CommandResult? Guard(int slot, string verb) {
        if (m_session.IsEditing(slot: slot)) {
            return null;
        }

        return Error(text: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} is not editing — editor.enter first]");
    }

    private static bool TryFloat(string token, out float value) =>
        (float.TryParse(s: token, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out value) && float.IsFinite(f: value));

    private static CommandDefinition Simulation(string name, string description, Func<CommandContext, string[], CommandResult> handler) {
        return CommandDefinition.WithTrailingArgs(name: name, description: description, handler: handler, routing: CommandRouting.Simulation);
    }

    private static CommandResult Echo(int slot, string verb, string detail) =>
        new(Output: $"[{verb}: seat {PlayerRoster.DisplayNumber(slot: slot)} {detail}]");

    private static CommandResult Error(string text) => new(Output: text) {
        IsError = true,
    };
}
