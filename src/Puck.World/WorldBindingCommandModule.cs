using System.Text;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The rebind console surface — the assist layer beside the chord-first binding UI. <c>player.bind</c> live-remaps
/// one source OR declares a chord row for a seat (its unsaved SESSION layer, recomposed and hot-reloaded at once);
/// <c>player.bindings</c> echoes a seat's composed ACTIVE mapping (its context-group derivation first — see
/// <see cref="WorldSeatBindings.DescribeContextDerivation"/> — then resting-page entries plus every chord row's
/// meaning); <c>player.signal</c> synthesizes a raw input signal into the router (the scripted twin of a physical
/// pad, so an agent can drive chords over the pipe); <c>identity.bindings.save</c> folds a seat's session rebinds
/// into its selected identity's durable <c>bindingOverlays</c> section on the owned world document (persisted
/// through <see cref="WorldOwnedWorlds.Save()"/>, UNGATED like every other identity door — no grant check, the
/// document is the seat's own), then empties the session layer. A SEPARATE
/// module from the identity/settings surface to keep each class under its analyzer ceilings.
/// </summary>
/// <remarks>Live rebinding changes the input→command mapping mid-run — deliberately breaking replay-stable command
/// streams (Puck.World is not determinism-gated). <c>player.bind</c>/<c>player.signal</c>/<c>identity.bindings.save</c>
/// route Simulation so the stdin barrier serializes a following <c>player.bindings</c> read-after-write;
/// <c>player.bindings</c> is an Immediate read.</remarks>
internal sealed class WorldBindingCommandModule(PlayerRoster roster, WorldSeatBindings seatBindings, IServerLink link, Func<InputRouter> router, Func<CommandRegistry> registry, IInputClock clock, WorldDefinition definition, WorldOwnedWorlds ownedWorlds) : ICommandModule {
    private readonly PlayerRoster m_roster = roster;
    private readonly WorldSeatBindings m_seatBindings = seatBindings;
    private readonly IServerLink m_link = link;
    // LAZY by necessity: the router's construction consumes the CommandRegistry, which aggregates this module — a
    // direct constructor dependency would cycle the container. Resolved on the first player.signal.
    private readonly Func<InputRouter> m_router = router;
    // LAZY for the same cycle: the registry aggregates this module, so world.affordances resolves it at dispatch.
    private readonly Func<CommandRegistry> m_registry = registry;
    private readonly IInputClock m_clock = clock;
    private readonly WorldOwnedWorlds m_ownedWorlds = ownedWorlds;
    // The declared channel table world.affordances echoes alongside the command manifest — the second vocabulary
    // a binding destination may name, and it goes through the same affordance gate a command name does.
    private readonly IReadOnlyList<WorldChannel> m_channels = definition.Channels;

    // The player.bind chord grammar: chord:<m1>+<m2>[+...] declares a command chord row in the DEFAULT group;
    // chord:<group>:<m1>+<m2> targets an explicit group.
    private const string ChordPrefix = "chord:";

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "player.bind",
            description: "Live-remaps one binding for a seat's SESSION layer (unsaved until identity.bindings.save): player.bind <seat> <source> <destination> [scale:<value>|value:<value>] — <seat> 1..4. <destination> is a command name (e.g. editor.enter) or channel:<name> (e.g. channel:jump). [scale:<value>] is only meaningful beside a channel destination (raw [-1, 1]; defaults to +1) and is refused on a BINARY channel. [value:<value>] is only meaningful beside a COMMAND destination whose declared value kind is Axis1D — the constant a digital source drives it with (the step-twin shape: a key bound with value:1 steps one way, value:-1 the other, exactly like world.affordances reports the destination taking); REQUIRED there (no default — there is no natural direction to assume), and refused by name on any other destination kind (Digital included — every other bindable verb keeps today's plain source→command behavior with no constant at all). scale: and value: are mutually exclusive; naming both is refused. <source> is a provider-neutral input source id (e.g. keyboard.e, gamepad.buttonEast) for a resting-page entry, or a CHORD ROW declaration: chord:<m1>+<m2> binds the ordered modifier chord to the destination in the default (play) group, chord:<group>:<m1>+<m2> targets an explicit group (modifier ids: lt, rt). Recomposes and hot-reloads that seat's mapping at once; a later bind of the same source or (group, chord) replaces it. This changes the input→command mapping mid-run (replay streams shift — World is not determinism-gated).",
            handler: BindHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "player.bindings",
            description: "Echoes a seat's composed ACTIVE mapping after the engine default ⊕ world overlays ⊕ profile bindings ⊕ live session rebinds merge. Leads with the seat's context derivation — group=<active> (<step>) where <step> is context <family>=<state>, requested, or default — then contexts: one segment per admitted family (<family>=<state>→<group> (wins), →<group> (shadowed), or →(no row)) and requested=<group|(none)> (marked (shadowed) when a context row overrides it); then the default group's resting-page trigger→destination entries and every chord row with its meaning (chord <group>:[m1+m2]→<destination> or →page <id>); a destination is a command name (with a trailing value:<v> when the entry carries a constant activation value — see player.bind) or channel:<name>; a trigger is a plain source id, or activator:held[...]/activator:tapped[...] for a row whose trigger is an ordered sequence, with a trailing [toggle] when the entry's mode is Toggle: player.bindings [seat] (optional seat 1..4, default 1).",
            handler: BindingsHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "player.signal",
            description: "Synthesizes one raw input signal into the router — the scripted twin of a physical control, so chords, sticks, and bindings are drivable over the pipe: player.signal <source> <press|release|value> or player.signal <source> <x> <y>. A scalar is an Axis1D Active sample; x/y is an Axis2D Active sample (for example gamepad.leftStick). The signal folds into the NEXT simulation tick's snapshot exactly like device input and remains carried until a release/zero sample, matching the physical backend rather than bypassing its binding.",
            handler: SignalHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.affordances",
            description: "Echoes the affordance manifest — every dispatchable command with its declared value kind, routing, and BINDABILITY — as one compact JSON array sorted by name: world.affordances. This is the SINGLE machine-readable vocabulary binding documents are validated against: a binding entry naming a command absent from this list, sending it the wrong value kind, or naming one whose \\\"bindable\\\" is false (an authority verb — the world grant/mutation surface, the editor apply paths, profile administration) is refused loudly at player.bind, at world.row.set bindingOverlays, at every recompose, and by the document validators, instead of resolving to a silently dead key or a reachable escalation. Immediate.",
            handler: (_, args) => ((args.Count > 0)
                ? CommandResult.Error(output: "[world.affordances: expected no arguments]")
                : new CommandResult(Output: DescribeAffordances()))
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "identity.bindings.save",
            description: "Folds a seat's live session rebinds into its identity-owned world: identity.bindings.save [seat].",
            handler: SaveHandler,
            routing: CommandRouting.Simulation
        );
    }

    // The player.bind destination grammar: channel:<name> declares a channel destination instead of a plain command
    // name. An optional trailing token modifies the destination: scale:<value> (channel only, its scale) or
    // value:<value> (command only, a constant CommandValue.Axis a digital source drives it with — the same
    // mechanism WorldDefaultBindings.Claim already uses for F1..F4's slot constant, now reachable live). Exactly
    // one of the two may ride a given bind; naming both is refused. Naming value: beside a destination whose
    // declared ValueKind is not Axis1D is refused upfront, by name, here (Digital included — every other bindable
    // verb keeps its plain source→command behavior with no constant). The reverse gap — an Axis1D destination with
    // no value: token — is deliberately not re-checked here: a digital source with no constant dispatches its own
    // Digital sample, and the vocabulary gate below (WorldAffordances.Validate/BindingVocabularyCheck) already
    // refuses that mismatch on its own, so value:'s absence needs no second, duplicate check.
    private const string ChannelDestinationPrefix = "channel:";
    private const string ScaleTokenPrefix = "scale:";
    private const string ValueTokenPrefix = "value:";

    // Whether the token at `index` starts with `prefix` — used only to detect the "both scale: and value: given"
    // shape ahead of the ordinary arity check, so that refusal can name the real rule instead of a bare token count.
    private static bool IsToken(in WireArgs args, int index, string prefix) =>
        args[index].StartsWith(value: prefix, comparisonType: StringComparison.OrdinalIgnoreCase);
    private CommandResult BindHandler(CommandContext context, WireArgs args) {
        if ((args.Count != 3) && (args.Count != 4)) {
            // A 5-token line naming BOTH scale: and value: (either order) gets its own message — "too many
            // arguments" would leave an author guessing which two collided, when the actual rule is that only one
            // of the two may ride a bind at all.
            if ((args.Count == 5) &&
                (IsToken(args: in args, index: 3, prefix: ScaleTokenPrefix) || IsToken(args: in args, index: 3, prefix: ValueTokenPrefix)) &&
                (IsToken(args: in args, index: 4, prefix: ScaleTokenPrefix) || IsToken(args: in args, index: 4, prefix: ValueTokenPrefix))) {
                return CommandResult.Error(output: "[player.bind: scale:<value> and value:<value> are mutually exclusive — name at most one]");
            }

            return CommandResult.Error(output: "[player.bind: expected <seat> <source> <destination> [scale:<value>|value:<value>] — seat 1..4; <source> may be chord:<m1>+<m2> or chord:<group>:<m1>+<m2>; <destination> a command name or channel:<name>]");
        }

        if (!WorldArgs.TryParseIndex(args: args, at: 0, min: 1, max: PlayerRoster.MaxSlots, fallback: null, value: out var seat)) {
            return CommandResult.Error(output: $"[player.bind: <seat> must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        var source = args[1].ToString();
        var destination = args[2].ToString();

        if (string.IsNullOrWhiteSpace(value: source) || string.IsNullOrWhiteSpace(value: destination)) {
            return CommandResult.Error(output: "[player.bind: <source> and <destination> must be non-empty]");
        }

        string? command = null;
        ChannelRef? channel = null;

        if (destination.StartsWith(value: ChannelDestinationPrefix, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            var channelName = destination[ChannelDestinationPrefix.Length..];

            if (string.IsNullOrWhiteSpace(value: channelName)) {
                return CommandResult.Error(output: "[player.bind: channel: must name a channel — e.g. channel:forward]");
            }
            channel = new ChannelRef.Name(Value: channelName);
        } else {
            command = destination;
        }

        float? scale = null;
        CommandValue? constantValue = null;

        if (args.Count == 4) {
            var token = args[3].ToString();
            var isScale = token.StartsWith(value: ScaleTokenPrefix, comparisonType: StringComparison.OrdinalIgnoreCase);
            var isValue = token.StartsWith(value: ValueTokenPrefix, comparisonType: StringComparison.OrdinalIgnoreCase);

            if (!isScale && !isValue) {
                return CommandResult.Error(output: "[player.bind: expected scale:<value> or value:<value> as the fourth argument]");
            }

            if (isScale) {
                if (channel is null) {
                    return CommandResult.Error(output: "[player.bind: scale:<value> is only meaningful beside a channel destination]");
                }

                if (!float.TryParse(s: token.AsSpan(start: ScaleTokenPrefix.Length), style: System.Globalization.NumberStyles.Float, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var parsedScale) ||
                    !float.IsFinite(f: parsedScale)) {
                    return CommandResult.Error(output: "[player.bind: expected scale:<value> as the fourth argument]");
                }

                scale = parsedScale;
            } else {
                if (command is null) {
                    return CommandResult.Error(output: "[player.bind: value:<value> is only meaningful beside a command destination]");
                }

                if (!m_registry().TryGetMetadata(name: command, metadata: out var destinationMetadata)) {
                    return CommandResult.Error(output: $"[player.bind: '{command}' names no registered command — see world.affordances]");
                }

                if (destinationMetadata.ValueKind != CommandValueKind.Axis1D) {
                    return CommandResult.Error(output: $"[player.bind: value:<value> only applies to a destination whose declared value kind is Axis1D — '{command}' takes {destinationMetadata.ValueKind.ToString().ToLowerInvariant()}]");
                }

                if (!float.TryParse(s: token.AsSpan(start: ValueTokenPrefix.Length), style: System.Globalization.NumberStyles.Float, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var parsedValue) ||
                    !float.IsFinite(f: parsedValue)) {
                    return CommandResult.Error(output: "[player.bind: expected value:<value> as the fourth argument]");
                }

                constantValue = CommandValue.Axis(value: parsedValue);
            }
        }

        var slot = PlayerRoster.SlotFromDisplay(number: seat);
        var current = m_seatBindings.SessionRebind(slot: slot);
        BindingProfileDocument rebind;
        BindingProfileDocument probe;

        if (source.StartsWith(value: ChordPrefix, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            if (!TryParseChordToken(token: source, group: out var group, members: out var members)) {
                return CommandResult.Error(output: "[player.bind: a chord source must be chord:<m1>+<m2>[+...] or chord:<group>:<m1>+<m2> — e.g. chord:lt+rt]");
            }

            rebind = UpsertChordRebind(current: current, group: group, members: members, command: command, channel: channel, scale: scale, value: constantValue);
            probe = UpsertChordRebind(current: null, group: group, members: members, command: command, channel: channel, scale: scale, value: constantValue);
        } else {
            rebind = UpsertRebind(current: current, source: source, command: command, channel: channel, scale: scale, value: constantValue);
            probe = UpsertRebind(current: null, source: source, command: command, channel: channel, scale: scale, value: constantValue);
        }

        var destinationLabel = ((channel is not null) ? ChannelLabel(channel: channel) : FormatCommandDestination(command: command!, value: constantValue));

        // The vocabulary gate first: a command/channel name the registry/channel table does not carry (or one taking
        // a different value kind than this binding dispatches, or a scale that is meaningless on a binary channel) is
        // the most likely authoring mistake, and without this check it would echo success and bind a key the router
        // silently drops at resolve time. Checked on THIS bind's own entry alone (PROBE — the same source/destination
        // folded onto an empty base) rather than the whole accumulated session document: a PRIOR entry the seat
        // carried out of a world it has since left (a channel the currently routed world no longer declares) is
        // WorldSeatBindings.RecomposeSeat's own concern from here on — its skip-and-narrate pre-pass drops it with
        // its own narration the moment SetSessionRebind recomposes below, and a genuinely structural leftover still
        // rejects that recompose loudly there. Re-validating the whole document here would let one stale row veto
        // every later bind on the seat, with no surgical way to remove just that row.
        var vocabularyErrors = new List<string>();

        WorldAffordances.Validate(document: probe, channels: m_seatBindings.Channels(slot: slot), errors: vocabularyErrors);

        if (vocabularyErrors.Count > 0) {
            return CommandResult.Error(output: $"[player.bind: '{source}' → '{destinationLabel}' refused — {vocabularyErrors[0]}]");
        }

        // Verify the ACTUAL routed composition before installing it, so the echo is truthful. The destination's
        // overlays may declare groups the boot default does not, and stale rows from a prior world must receive the
        // same surgical filtering RecomposeSeat applies instead of vetoing this unrelated bind.
        if (!m_seatBindings.TryValidateSessionRebind(slot: slot, rebinds: rebind, reason: out var reason)) {
            return CommandResult.Error(output: $"[player.bind: '{source}' → '{destinationLabel}' does not compose ({reason})]");
        }

        m_seatBindings.SetSessionRebind(slot: slot, rebinds: rebind);

        return new CommandResult(Output: $"[player.bind: seat {seat} '{source}' → '{destinationLabel}' (unsaved — identity.bindings.save to persist)]");
    }
    private CommandResult SignalHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (2 or 3)) {
            return CommandResult.Error(output: "[player.signal: expected <source> <press|release|value> or <source> <x> <y>]");
        }

        var source = args[0].ToString();

        if (string.IsNullOrWhiteSpace(value: source)) {
            return CommandResult.Error(output: "[player.signal: <source> must be non-empty]");
        }

        CommandPhase phase;
        CommandValue value;

        if (args.Count == 3) {
            if (!args.TryFloat(index: 1, value: out var x) || !args.TryFloat(index: 2, value: out var y) ||
                !float.IsFinite(f: x) || !float.IsFinite(f: y)) {
                return CommandResult.Error(output: "[player.signal: axis values must be finite numbers]");
            }
            phase = CommandPhase.Active;
            value = CommandValue.Axis(value: new System.Numerics.Vector2(
                x: Math.Clamp(value: x, min: -1f, max: 1f),
                y: Math.Clamp(value: y, min: -1f, max: 1f)));
        } else if (args.Is(index: 1, value: "press")) {
            phase = CommandPhase.Started;
            value = CommandValue.Digital(active: true);
        } else if (args.Is(index: 1, value: "release")) {
            phase = CommandPhase.Completed;
            value = CommandValue.Digital(active: false);
        } else if (float.TryParse(s: args[1], style: System.Globalization.NumberStyles.Float, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var sample)) {
            phase = CommandPhase.Active;
            value = CommandValue.Axis(value: sample);
        } else {
            return CommandResult.Error(output: "[player.signal: the second value must be press, release, or a number]");
        }

        m_router().Capture(signal: new InputSignal(
            CaptureTick: m_clock.NowTicks,
            DeviceId: default,
            Phase: phase,
            Source: source,
            Value: value
        ));

        var describedSample = ((args.Count == 3) ? $"{args[1].ToString().ToLowerInvariant()} {args[2].ToString().ToLowerInvariant()}" : args[1].ToString().ToLowerInvariant());
        return new CommandResult(Output: $"[player.signal: {source} {describedSample}]");
    }
    private CommandResult BindingsHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[player.bindings: expected at most 1 value — an optional seat index]");
        }

        if (!WorldArgs.TryParseIndex(args: args, at: 0, min: 1, max: PlayerRoster.MaxSlots, fallback: 1, value: out var seat)) {
            return CommandResult.Error(output: $"[player.bindings: seat must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        var slot = PlayerRoster.SlotFromDisplay(number: seat);
        var document = m_seatBindings.ComposedDocument(slot: slot);
        var builder = new StringBuilder(value: $"[player.bindings: seat {seat}");
        var defaultGroup = ((document.Chords is { Count: > 0 } chords) ? chords[0].Group : string.Empty);
        var seen = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var any = true;

        // The context derivation first — the read-back rule's half of context-derived groups: the resolved active
        // group with its derivation step, then every admitted family's state and match (winner marked, a shadowed
        // match visibly distinct from "no row"), then the requested group (marked shadowed when a row overrides it).
        var derivation = m_seatBindings.DescribeContextDerivation(slot: slot);

        _ = builder.Append(value: " group=").Append(value: derivation.ActiveGroup)
            .Append(value: " (").Append(value: derivation.Step).Append(value: ") | contexts:");

        foreach (var family in derivation.Families) {
            _ = builder.Append(value: ' ').Append(value: family.Family).Append(value: '=').Append(value: family.State).Append(value: '→');
            _ = ((family.Group is { } matchedGroup)
                ? builder.Append(value: matchedGroup).Append(value: (family.Wins ? " (wins)" : " (shadowed)"))
                : builder.Append(value: "(no row)"));
            _ = builder.Append(value: ',');
        }

        _ = builder.Append(value: " requested=").Append(value: (derivation.RequestedGroup ?? "(none)"));

        if (derivation.RequestedShadowed) {
            _ = builder.Append(value: " (shadowed)");
        }

        // The default group's resting-page entries first (the classic source→command glance)...
        foreach (var row in (document.Chords ?? [])) {
            if ((row.Chord is { Count: > 0 }) || (row.Page?.Entries is not { } entries) ||
                !string.Equals(a: row.Group, b: defaultGroup, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            foreach (var entry in entries) {
                // The destination label — a channel entry echoes as channel:<name> (the same token player.bind
                // accepts), a command entry as its bare name plus a trailing value:<v> when it carries a constant.
                var destination = ((entry.Channel is { } channel) ? ChannelLabel(channel: channel) : FormatCommandDestination(command: entry.Command!, value: entry.Value));

                // The trigger label — a plain entry echoes its Source (unchanged); an ACTIVATOR entry (no Source)
                // echoes activator:<mode>[step,step,...] instead, so player.bindings names the primitive honestly
                // rather than printing a blank/null trigger.
                var trigger = (entry.Source ?? ((entry.Activator is { } activator)
                    ? $"activator:{activator.Mode.ToString().ToLowerInvariant()}[{string.Join(separator: ',', values: activator.Sequence)}]"
                    : "(unset)"));

                // One segment per distinct trigger→destination pair (a hold/release pair collapses to one). A
                // Toggle-mode entry carries a [toggle] suffix — the same property player.bind's echo names.
                if (seen.Add(item: $"{trigger}\0{destination}")) {
                    _ = builder.Append(value: (any ? " | " : " ")).Append(value: trigger).Append(value: "→").Append(value: destination);

                    if (entry.Mode == BindingEntryMode.Toggle) {
                        _ = builder.Append(value: " [toggle]");
                    }

                    any = true;
                }
            }
        }

        // ...then every chord row with its meaning: chord <group>:[m1+m2]→<command> or →page <id>.
        foreach (var row in (document.Chords ?? [])) {
            if (row.Chord is not { Count: > 0 } chord) {
                continue;
            }

            _ = builder.Append(value: (any ? " | " : " "))
                .Append(value: "chord ").Append(value: row.Group).Append(value: ":[").Append(value: string.Join(separator: '+', values: chord)).Append(value: "]→");
            _ = (row.Command switch {
                { Channel: { } channel } => builder.Append(value: ChannelLabel(channel: channel)),
                { } command => builder.Append(value: FormatCommandDestination(command: command.Command!, value: command.Value)),
                null => builder.Append(value: "page ").Append(value: row.Page?.Id),
            });
            any = true;
        }

        if (!any) {
            _ = builder.Append(value: " (none)");
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }
    private CommandResult SaveHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[identity.bindings.save: expected at most 1 value — an optional seat index]");
        }

        if (!WorldArgs.TryParseIndex(args: args, at: 0, min: 1, max: PlayerRoster.MaxSlots, fallback: 1, value: out var seat)) {
            return CommandResult.Error(output: $"[identity.bindings.save: seat must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        var slot = PlayerRoster.SlotFromDisplay(number: seat);

        if (m_roster.ProfileAt(slot: slot) is not { } profile) {
            return CommandResult.Error(output: $"[identity.bindings.save: seat {seat} is not joined — see world.players]");
        }

        if (m_seatBindings.SessionRebind(slot: slot) is not { } session) {
            return CommandResult.Error(output: $"[identity.bindings.save: seat {seat} has no unsaved rebinds]");
        }

        // Fold the session rebinds into the identity's existing binding layer (or start one) and persist directly
        // through WorldOwnedWorlds.Save() — UNGATED, like every other identity door: this writes the seat's OWN
        // owned world in-process, never a WorldMutation or a server-side SetPlayerSection submission (that protocol
        // was deleted with the player-document family in ad5935ae).
        var merged = WorldBindingComposer.Compose(profile.Bindings, session);

        profile.Bindings = merged;
        if (profile.Document is { } document) {
            profile.ReplaceDocument(document: document with { BindingOverlays = [new WorldBindingOverlay(Id: "identity", Document: merged)] });
        }
        m_ownedWorlds.Save();
        m_seatBindings.SetSessionRebind(slot: slot, rebinds: null);
        RefreshSeatsBoundTo(profileId: profile.Id);
        return new CommandResult(Output: $"[identity.bindings.save: seat {seat} → world:{profile.Id}]");
    }

    // Re-derives the profile-bindings input layer for every ACTIVE seat whose selected identity is <paramref name="profileId"/>,
    // so a durable bindings edit (identity.bindings.save) recomposes and hot-reloads the couch's mappings at once —
    // the seat handles are the same shared WorldIdentity the edit above mutated in place, so the new section reads
    // straight off the live handle. The ONE seat-refresh path this durable-edit verb uses.
    private void RefreshSeatsBoundTo(string profileId) {
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if ((m_roster.ProfileAt(slot: slot) is { } profile) && string.Equals(a: profile.Id, b: profileId, comparisonType: StringComparison.Ordinal)) {
                m_seatBindings.SetProfileLayers(slot: slot, bindings: profile.Bindings);
            }
        }
    }

    // Parse a chord token: chord:<m1>+<m2>[+...] (the default play group) or chord:<group>:<m1>+<m2>.
    private static bool TryParseChordToken(string token, out string group, out string[] members) {
        group = WorldDefaultBindings.PlayGroup;
        members = [];

        var body = token[ChordPrefix.Length..];
        var groupSplit = body.Split(separator: ':');

        if (groupSplit.Length == 2) {
            group = groupSplit[0];
            body = groupSplit[1];
        } else if (groupSplit.Length != 1) {
            return false;
        }

        if (string.IsNullOrWhiteSpace(value: group) || string.IsNullOrWhiteSpace(value: body)) {
            return false;
        }

        members = body.Split(separator: '+');

        foreach (var member in members) {
            if (string.IsNullOrWhiteSpace(value: member)) {
                return false;
            }
        }

        return (members.Length > 0);
    }

    // Build the seat's session-rebind document with one resting-page entry replaced: keep every prior rebind row,
    // filter the target source out of the resting page, append the new source→destination (a command with its
    // optional constant value, or a channel with its optional scale).
    private static BindingProfileDocument UpsertRebind(BindingProfileDocument? current, string source, string? command, ChannelRef? channel, float? scale, CommandValue? value) {
        var entries = new List<BindingPageEntryDefinition>();
        var rows = new List<BindingChordDefinition>();

        foreach (var row in (current?.Chords ?? [])) {
            if (IsSessionRestingPage(row: row)) {
                foreach (var entry in (row.Page!.Entries ?? [])) {
                    if (!string.Equals(a: entry.Source, b: source, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                        entries.Add(item: entry);
                    }
                }
            } else {
                rows.Add(item: row);
            }
        }

        entries.Add(item: new BindingPageEntryDefinition(Source: source, Command: command, Channel: channel, Scale: scale, Value: value));
        rows.Insert(index: 0, item: new BindingChordDefinition(
            Group: WorldDefaultBindings.PlayGroup,
            Chord: [],
            Page: new BindingPageDefinition(Id: WorldDefaultBindings.BasePageId, Entries: entries)
        ));

        return new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: rows
        );
    }

    // Build the seat's session-rebind document with one chord row upserted: keep every prior row except the same
    // (group, ordered chord) — a later bind of the same chord replaces its meaning (a command with its optional
    // constant value, or a channel with its optional scale).
    private static BindingProfileDocument UpsertChordRebind(BindingProfileDocument? current, string group, string[] members, string? command, ChannelRef? channel, float? scale, CommandValue? value) {
        var rows = new List<BindingChordDefinition>();

        foreach (var row in (current?.Chords ?? [])) {
            if (string.Equals(a: row.Group, b: group, comparisonType: StringComparison.Ordinal) &&
                ((row.Chord?.Count ?? 0) == members.Length) &&
                (row.Chord?.SequenceEqual(second: members, comparer: StringComparer.Ordinal) ?? false)) {
                continue;
            }

            rows.Add(item: row);
        }

        rows.Add(item: new BindingChordDefinition(
            Group: group,
            Chord: members,
            Command: new BindingCommandDefinition(Command: command, Channel: channel, Scale: scale, Value: value)
        ));

        return new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: rows
        );
    }

    // A session-layer resting-page row (the play group's empty chord) — the row entry rebinds accumulate on.
    private static bool IsSessionRestingPage(BindingChordDefinition row) {
        return (string.Equals(a: row.Group, b: WorldDefaultBindings.PlayGroup, comparisonType: StringComparison.Ordinal) &&
            (row.Chord is not { Count: > 0 }) &&
            (row.Page is not null));
    }
    private static string ChannelLabel(ChannelRef channel) => channel switch {
        ChannelRef.Name name => $"channel:{name.Value}",
        _ => "channel:(invalid)",
    };

    // A command destination's label for player.bind's echo and player.bindings' read-back: the bare name, or the
    // name plus its trailing value:<v> when the entry carries a constant activation value (the same visibility
    // rule for BOTH — the read-back rule says a decision surface must be echoable, and a constant IS a decision).
    private static string FormatCommandDestination(string command, CommandValue? value) =>
        ((value is { } constant) ? $"{command} value:{(double)constant.AsAxis1D:0.###}" : command);

    // The world.affordances echo: every distinct dispatchable command as compact JSON, ordinal-sorted by name so two
    // reads diff cleanly. Hand-built — names, kinds, and routings are identifier-safe by construction (the registry's
    // own claim guard owns name hygiene), so no serializer dependency is warranted for one flat array.
    private string DescribeAffordances() {
        var builder = new StringBuilder(value: "[world.affordances:");
        var count = 0;

        // The registry already hands these out ordinal-sorted by name, so two reads diff cleanly.
        foreach (var definition in m_registry().Definitions) {
            _ = builder.Append(value: ((count > 0) ? "," : " ["))
                .Append(value: "{\"name\":\"").Append(value: definition.Name)
                .Append(value: "\",\"valueKind\":\"").Append(value: definition.ValueKind.ToString().ToLowerInvariant())
                .Append(value: "\",\"routing\":\"").Append(value: definition.Routing.ToString().ToLowerInvariant())
                .Append(value: "\",\"bindable\":").Append(value: ((definition.Bindability == CommandBindability.Bindable) ? "true" : "false"))
                .Append(value: "}");
            count++;
        }

        _ = ((count > 0) ? builder.Append(value: ']') : builder.Append(value: " []"));
        _ = builder.Append(value: ",\"channels\":");

        var channelCount = 0;

        foreach (var channel in m_channels) {
            _ = builder.Append(value: ((channelCount > 0) ? "," : "["))
                .Append(value: "{\"name\":\"").Append(value: channel.Name)
                .Append(value: "\",\"shape\":\"").Append(value: channel.Shape.ToString().ToLowerInvariant())
                .Append(value: "\",\"consumer\":\"").Append(value: ((channel.Role is { } role) ? role.ToString().ToLowerInvariant() : "composition"))
                .Append(value: "\"}");
            channelCount++;
        }

        _ = ((channelCount > 0) ? builder.Append(value: ']') : builder.Append(value: "[]"));

        return builder.Append(value: ']').ToString();
    }

    // A server read-back rendered with its verdict: a refusal (a missing/inactive subject) fails, so it reaches
    // wire.errors rather than scrolling past as data.
    private static CommandResult Answered(in QueryAnswer answer) => new(Output: answer.Text) {
        IsError = answer.Refused,
    };

    // Submits a query and renders its answer — the completion fires INLINE over loopback, so the result is settled
    // before this call returns.
    private CommandResult QueryResult(WorldQuery query) {
        var result = default(CommandResult);

        m_link.Query(query: query, completion: answer => {
            result = Answered(answer: in answer);
        });

        return result;
    }
}
