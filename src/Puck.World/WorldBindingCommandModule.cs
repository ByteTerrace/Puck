using System.Text;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The rebind console surface — the assist layer beside the chord-first binding UI. <c>player.bind</c> live-remaps
/// one source OR declares a chord row for a seat (its unsaved SESSION layer, recomposed and hot-reloaded at once);
/// <c>player.bindings</c> echoes a seat's composed ACTIVE mapping (resting-page entries plus every chord row's
/// meaning); <c>player.signal</c> synthesizes a raw input signal into the router (the scripted twin of a physical
/// pad, so an agent can drive chords over the pipe); <c>profile.save</c> folds a seat's session rebinds into its
/// selected profile's durable <c>bindings</c> section through the server-owned player document (a
/// <c>SetPlayerSection</c> submission gated on the Edit capability), then empties the session layer. A SEPARATE
/// module from the profile/settings surface to keep each class under its analyzer ceilings.
/// </summary>
/// <remarks>Live rebinding changes the input→command mapping mid-run — deliberately breaking replay-stable command
/// streams (Puck.World is not determinism-gated). <c>player.bind</c>/<c>player.signal</c>/<c>profile.save</c>
/// route Simulation so the stdin barrier serializes a following <c>player.bindings</c> read-after-write;
/// <c>player.bindings</c> is an Immediate read.</remarks>
internal sealed class WorldBindingCommandModule(PlayerRoster roster, WorldSeatBindings seatBindings, IServerLink link, Func<InputRouter> router, IInputClock clock) : ICommandModule {
    private readonly PlayerRoster m_roster = roster;
    private readonly WorldSeatBindings m_seatBindings = seatBindings;
    private readonly IServerLink m_link = link;
    // LAZY by necessity: the router's construction consumes the CommandRegistry, which aggregates this module — a
    // direct constructor dependency would cycle the container. Resolved on the first player.signal.
    private readonly Func<InputRouter> m_router = router;
    private readonly IInputClock m_clock = clock;

    // The player.bind chord grammar: chord:<m1>+<m2>[+...] declares a command chord row in the DEFAULT group;
    // chord:<group>:<m1>+<m2> targets an explicit group.
    private const string ChordPrefix = "chord:";

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: "player.bind",
            description: "Live-remaps one binding for a seat's SESSION layer (unsaved until profile.save): player.bind <seat> <source> <command> — <seat> 1..4, <command> the command it fires. <source> is a provider-neutral input source id (e.g. keyboard.e, gamepad.buttonEast) for a resting-page entry, or a CHORD ROW declaration: chord:<m1>+<m2> binds the ordered modifier chord to the command in the default (play) group, chord:<group>:<m1>+<m2> targets an explicit group (modifier ids: lt, rt). Recomposes and hot-reloads that seat's mapping at once; a later bind of the same source or (group, chord) replaces it. This changes the input→command mapping mid-run (replay streams shift — World is not determinism-gated).",
            handler: BindHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.bindings",
            description: "Echoes a seat's composed ACTIVE mapping after the engine default ⊕ world overlays ⊕ profile bindings ⊕ live session rebinds merge: the default group's resting-page source→command entries, then every chord row with its meaning (chord <group>:[m1+m2]→<command> or →page <id>): player.bindings [seat] (optional seat 1..4, default 1).",
            handler: BindingsHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.signal",
            description: "Synthesizes one raw input signal into the router — the scripted twin of a physical control, so chords and bindings are drivable over the pipe: player.signal <source> <press|release|value> — <source> a provider-neutral input source id (e.g. gamepad.leftTrigger, gamepad.buttonSouth); press/release = a digital edge; a number = an analog Active sample (a trigger sweep — 0.9 latches a modifier, 0 releases it through hysteresis). The signal folds into the NEXT simulation tick's snapshot exactly like device input (it rides the seat the device-neutral lane resolves to — seat 1). Replay streams shift, like any live input.",
            handler: SignalHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            name: "profile.doc",
            description: "Echoes the whole server-owned player document (puck.world.player.v1) as JSON — the read-back an editor/agent pulls before editing a profile section (GetPlayerDocument): profile.doc. Immediate.",
            handler: (_, args) => (args.Count > 0)
                ? Error(output: "[profile.doc: expected no arguments]")
                : Answered(answer: m_link.Query(query: new WorldQuery.PlayerDocument()))
        );
        yield return CommandDefinition.WithWireArgs(
            name: "profile.save",
            description: "Folds a seat's live SESSION rebinds into its selected profile's durable bindings section and persists (through the server-owned player document, gated on the Edit capability): profile.save [seat] (optional seat 1..4, default 1). The session layer then empties. A friendly no-op when the seat has no unsaved rebinds.",
            handler: SaveHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            name: "profile.section",
            description: "Durably edits ONE section of a profile through the player-document protocol — the raw SetPlayerSection reflection an editor/agent drives (the typed profile.set/profile.save are sugar over the same wire): profile.section <profile-id> <identity|motion|bindings|preferences> <compact-json>. identity = {\"name\":…,\"color\":\"#RRGGBB\"}; motion = {\"moveSpeed\":…,\"turnSpeed\":…,\"invertLookX\":…}; bindings = a BindingProfileDocument (or null to inherit the engine default); preferences = a JSON object (or null to clear the bag). The payload is one compact (whitespace-free) JSON token. The server validates the candidate document through the thick gate, updates the live handle, bumps the revision, and persists; a malformed payload or a validation failure rejects loudly. Gated on the Edit capability.",
            handler: SectionHandler,
            routing: CommandRouting.Simulation
        );
    }

    private CommandResult BindHandler(CommandContext context, WireArgs args) {
        if (args.Count != 3) {
            return Error(output: "[player.bind: expected <seat> <source> <command> — seat 1..4; <source> may be chord:<m1>+<m2> or chord:<group>:<m1>+<m2>]");
        }

        if (!WorldArgs.TryParseIndex(args: args, at: 0, min: 1, max: PlayerRoster.MaxSlots, fallback: null, value: out var seat)) {
            return Error(output: $"[player.bind: <seat> must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        var source = args[1].ToString();
        var command = args[2].ToString();

        if (string.IsNullOrWhiteSpace(value: source) || string.IsNullOrWhiteSpace(value: command)) {
            return Error(output: "[player.bind: <source> and <command> must be non-empty]");
        }

        var slot = PlayerRoster.SlotFromDisplay(number: seat);
        var current = m_seatBindings.SessionRebind(slot: slot);
        BindingProfileDocument rebind;

        if (source.StartsWith(value: ChordPrefix, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            if (!TryParseChordToken(token: source, group: out var group, members: out var members)) {
                return Error(output: "[player.bind: a chord source must be chord:<m1>+<m2>[+...] or chord:<group>:<m1>+<m2> — e.g. chord:lt+rt]");
            }

            rebind = UpsertChordRebind(current: current, group: group, members: members, command: command);
        } else {
            rebind = UpsertRebind(current: current, source: source, command: command);
        }

        // Verify the composed result compiles before installing it, so the echo is truthful (an undeclared modifier
        // id or a chord that collides with an existing meaning rejects HERE, loudly); SetSessionRebind's own
        // recompose is the belt-and-braces.
        try {
            _ = BindingProfile.Compile(document: WorldBindingComposer.Compose(WorldDefaultBindings.BuildDocument(), rebind));
        } catch (ArgumentException exception) {
            return Error(output: $"[player.bind: '{source}' → '{command}' does not compile ({exception.Message.ReplaceLineEndings(replacementText: " ")})]");
        }

        m_seatBindings.SetSessionRebind(slot: slot, rebinds: rebind);

        return new CommandResult(Output: $"[player.bind: seat {seat} '{source}' → '{command}' (unsaved — profile.save to persist)]");
    }

    private CommandResult SignalHandler(CommandContext context, WireArgs args) {
        if (args.Count != 2) {
            return Error(output: "[player.signal: expected <source> <press|release|value>]");
        }

        var source = args[0].ToString();

        if (string.IsNullOrWhiteSpace(value: source)) {
            return Error(output: "[player.signal: <source> must be non-empty]");
        }

        CommandPhase phase;
        CommandValue value;

        if (args.Is(index: 1, value: "press")) {
            phase = CommandPhase.Started;
            value = CommandValue.Digital(active: true);
        } else if (args.Is(index: 1, value: "release")) {
            phase = CommandPhase.Completed;
            value = CommandValue.Digital(active: false);
        } else if (float.TryParse(s: args[1], style: System.Globalization.NumberStyles.Float, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var sample)) {
            phase = CommandPhase.Active;
            value = CommandValue.Axis(value: sample);
        } else {
            return Error(output: "[player.signal: the second value must be press, release, or a number]");
        }

        m_router().Capture(signal: new InputSignal(
            CaptureTick: m_clock.NowTicks,
            DeviceId: default,
            Phase: phase,
            Source: source,
            Value: value
        ));

        return new CommandResult(Output: $"[player.signal: {source} {args[1].ToString().ToLowerInvariant()}]");
    }

    private CommandResult BindingsHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return Error(output: "[player.bindings: expected at most 1 value — an optional seat index]");
        }

        if (!WorldArgs.TryParseIndex(args: args, at: 0, min: 1, max: PlayerRoster.MaxSlots, fallback: 1, value: out var seat)) {
            return Error(output: $"[player.bindings: seat must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        var slot = PlayerRoster.SlotFromDisplay(number: seat);
        var document = m_seatBindings.ComposedDocument(slot: slot);
        var builder = new StringBuilder(value: $"[player.bindings: seat {seat}");
        var defaultGroup = ((document.Chords is { Count: > 0 } chords) ? chords[0].Group : string.Empty);
        var seen = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var any = false;

        // The default group's resting-page entries first (the classic source→command glance)...
        foreach (var row in (document.Chords ?? [])) {
            if ((row.Chord is { Count: > 0 }) || (row.Page?.Entries is not { } entries) ||
                !string.Equals(a: row.Group, b: defaultGroup, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            foreach (var entry in entries) {
                // One segment per distinct source→command pair (a hold/release pair collapses to one).
                if (seen.Add(item: $"{entry.Source}\0{entry.Command}")) {
                    _ = builder.Append(value: any ? " | " : " ").Append(value: entry.Source).Append(value: "→").Append(value: entry.Command);
                    any = true;
                }
            }
        }

        // ...then every chord row with its meaning: chord <group>:[m1+m2]→<command> or →page <id>.
        foreach (var row in (document.Chords ?? [])) {
            if (row.Chord is not { Count: > 0 } chord) {
                continue;
            }

            _ = builder.Append(value: any ? " | " : " ")
                .Append(value: "chord ").Append(value: row.Group).Append(value: ":[").Append(value: string.Join(separator: '+', values: chord)).Append(value: "]→");
            _ = ((row.Command is { } command)
                ? builder.Append(value: command.Command)
                : builder.Append(value: "page ").Append(value: row.Page?.Id));
            any = true;
        }

        if (!any) {
            _ = builder.Append(value: " (none)");
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }

    private CommandResult SaveHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return Error(output: "[profile.save: expected at most 1 value — an optional seat index]");
        }

        if (!WorldArgs.TryParseIndex(args: args, at: 0, min: 1, max: PlayerRoster.MaxSlots, fallback: 1, value: out var seat)) {
            return Error(output: $"[profile.save: seat must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        var slot = PlayerRoster.SlotFromDisplay(number: seat);

        if (m_roster.ProfileAt(slot: slot) is not { } profile) {
            return Error(output: $"[profile.save: seat {seat} is not joined — see world.players]");
        }

        if (m_seatBindings.SessionRebind(slot: slot) is not { } session) {
            return new CommandResult(Output: $"[profile.save: seat {seat} has no unsaved rebinds]") { IsError = true };
        }

        // Fold the session rebinds into the profile's existing binding layer (or start one), and submit the durable
        // edit to the server, which validates, persists (bumping the document revision), and acknowledges.
        var merged = WorldBindingComposer.Compose(profile.Bindings, session);
        var reply = m_link.SubmitSession(request: new SessionRequest.SetPlayerSection(
            Principal: WorldPrincipal.Seat(slot: slot),
            ProfileId: profile.Id,
            Section: WorldPlayerSection.Bindings,
            Payload: WorldPlayerJson.SerializeBindings(document: merged)
        ));

        if (!reply.Accepted) {
            return Error(output: $"[profile.save: {reply.Reason}]");
        }

        // Acknowledged: the server applied the merged bindings to the shared profile handle. Clear this seat's session
        // layer, then re-derive the profile-bindings layer for EVERY active seat on this profile (this seat and any
        // couch co-op seat sharing it) off the now-durable handle — the one live-refresh path.
        m_seatBindings.SetSessionRebind(slot: slot, rebinds: null);
        RefreshSeatsBoundTo(profileId: profile.Id);

        return new CommandResult(Output: $"[profile.save: seat {seat} → profile '{profile.Name}' bindings saved]");
    }

    private CommandResult SectionHandler(CommandContext context, WireArgs args) {
        if (args.Count < 3) {
            return Error(output: "[profile.section: expected <profile-id> <section> <compact-json> — section is identity|motion|bindings|preferences]");
        }

        if (!TryParseSection(args: args, index: 1, section: out var section)) {
            return Error(output: $"[profile.section: unknown section '{args[1].ToString()}' — identity|motion|bindings|preferences]");
        }

        var profileId = args[0].ToString();

        // The raw JSON after the id and section tokens — reconstructed from the submitted line so inline-JSON quotes
        // survive the console tokenizer (the same discipline the world.*.set mutation verbs use).
        var payload = RawPayloadAfter(context: context, args: args, argTokens: 2);

        if (string.IsNullOrWhiteSpace(value: payload)) {
            return Error(output: "[profile.section: expected a compact (whitespace-free) JSON payload]");
        }

        var reply = m_link.SubmitSession(request: new SessionRequest.SetPlayerSection(
            Principal: WorldPrincipal.Console,
            ProfileId: profileId,
            Section: section,
            Payload: payload
        ));

        if (!reply.Accepted) {
            return Error(output: $"[profile.section: {reply.Reason}]");
        }

        // A durable BINDINGS edit must reach seated players LIVE (no reseat) — mirror profile.save's fold-refresh and
        // re-derive the profile-bindings layer for every active seat on this profile. Identity/motion/preferences
        // carry no input mapping, so they need no seat recompose (color already refreshes server-side).
        if (section == WorldPlayerSection.Bindings) {
            RefreshSeatsBoundTo(profileId: profileId);
        }

        return new CommandResult(Output: $"[profile.section: {profileId} {section.ToString().ToLowerInvariant()} applied]");
    }

    // Re-derives the profile-bindings input layer for every ACTIVE seat whose selected profile is <paramref name="profileId"/>,
    // so a durable bindings edit (a folded profile.save or a raw profile.section) recomposes and hot-reloads the couch's
    // mappings at once — the seat handles are the same shared WorldProfile identity the server edited in place, so the
    // new section reads straight off the live handle. The ONE seat-refresh path both durable-edit verbs share.
    private void RefreshSeatsBoundTo(string profileId) {
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if ((m_roster.ProfileAt(slot: slot) is { } profile) && string.Equals(a: profile.Id, b: profileId, comparisonType: StringComparison.Ordinal)) {
                m_seatBindings.SetProfileBindings(slot: slot, bindings: profile.Bindings);
            }
        }
    }

    private static bool TryParseSection(in WireArgs args, int index, out WorldPlayerSection section) {
        if (args.Is(index: index, value: "identity")) {
            section = WorldPlayerSection.Identity;

            return true;
        }

        if (args.Is(index: index, value: "motion")) {
            section = WorldPlayerSection.Motion;

            return true;
        }

        if (args.Is(index: index, value: "bindings")) {
            section = WorldPlayerSection.Bindings;

            return true;
        }

        if (args.Is(index: index, value: "preferences")) {
            section = WorldPlayerSection.Preferences;

            return true;
        }

        section = WorldPlayerSection.Identity;

        return false;
    }

    // The raw text after the verb token and the first argTokens argument tokens — the payload with quotes intact. The
    // split-args join is a defensive fallback for the (compact-JSON) case where no whitespace splits the token.
    private static string RawPayloadAfter(CommandContext context, in WireArgs args, int argTokens) {
        if (context.Text is { } text) {
            var span = text.AsSpan().TrimStart();

            for (var skip = 0; (skip <= argTokens); skip++) {
                var separator = span.IndexOfAny(value0: ' ', value1: '\t');

                if (separator < 0) {
                    return string.Empty;
                }

                span = span[(separator + 1)..].TrimStart();
            }

            return span.Trim().ToString();
        }

        return ((args.Count > argTokens) ? args.Tail(argTokens) : string.Empty);
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
    // filter the target source out of the resting page, append the new source→command (AnyModifiers, so the remap
    // keeps working with an incidental modifier).
    private static BindingProfileDocument UpsertRebind(BindingProfileDocument? current, string source, string command) {
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

        entries.Add(item: new BindingPageEntryDefinition(Source: source, Command: command, AnyModifiers: true));
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
    // (group, ordered chord) — a later bind of the same chord replaces its meaning.
    private static BindingProfileDocument UpsertChordRebind(BindingProfileDocument? current, string group, string[] members, string command) {
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
            Command: new BindingCommandDefinition(Command: command)
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

    private static CommandResult Error(string output) => new(Output: output) {
        IsError = true,
    };

    // A server read-back rendered with its verdict: a refusal (a missing/inactive subject) fails, so it reaches
    // wire.errors rather than scrolling past as data.
    private static CommandResult Answered(in QueryAnswer answer) => new(Output: answer.Text) {
        IsError = answer.Refused,
    };
}
