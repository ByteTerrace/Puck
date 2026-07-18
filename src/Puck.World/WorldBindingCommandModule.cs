using System.Text;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The rebind console surface — the assist layer over the chord-first binding UI to come. <c>player.bind</c> live-remaps
/// one source for a seat (its unsaved SESSION layer, recomposed and hot-reloaded at once); <c>player.bindings</c> echoes
/// a seat's composed ACTIVE mapping; <c>profile.save</c> folds a seat's session rebinds into its selected profile's
/// durable <c>bindings</c> section through the server-owned player document (a <c>SetPlayerSection</c> submission gated
/// on the Edit capability), then empties the session layer. A SEPARATE module from the profile/settings surface to keep
/// each class under its analyzer ceilings.
/// </summary>
/// <remarks>Live rebinding changes the input→command mapping mid-run — deliberately breaking replay-stable command
/// streams (Puck.World is not determinism-gated, §2.4). <c>player.bind</c>/<c>profile.save</c> route Simulation so the
/// stdin barrier serializes a following <c>player.bindings</c> read-after-write; <c>player.bindings</c> is an Immediate
/// read.</remarks>
internal sealed class WorldBindingCommandModule(PlayerRoster roster, WorldSeatBindings seatBindings, IServerLink link) : ICommandModule {
    private readonly PlayerRoster m_roster = roster;
    private readonly WorldSeatBindings m_seatBindings = seatBindings;
    private readonly IServerLink m_link = link;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: "player.bind",
            description: "Live-remaps one input source for a seat's SESSION layer (unsaved until profile.save): player.bind <seat> <source> <command> — <seat> 1..4, <source> a provider-neutral input source id (e.g. keyboard.e, gamepad.buttonEast), <command> the player.* command it fires. Recomposes and hot-reloads that seat's mapping at once; a later bind of the same source replaces it. This changes the input→command mapping mid-run (replay streams shift — World is not determinism-gated).",
            handler: BindHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "player.bindings",
            description: "Echoes a seat's composed ACTIVE mapping — the no-modifier page's source→command entries after the engine default ⊕ world overlays ⊕ profile bindings ⊕ live session rebinds merge: player.bindings [seat] (optional seat 1..4, default 1).",
            handler: BindingsHandler
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "profile.doc",
            description: "Echoes the whole server-owned player document (puck.world.player.v1) as JSON — the read-back an editor/agent pulls before editing a profile section (GetPlayerDocument): profile.doc. Immediate.",
            handler: (_, args) => (args.Length > 0)
                ? Error(output: "[profile.doc: expected no arguments]")
                : new CommandResult(Output: m_link.Query(query: new WorldQuery.PlayerDocument()).Text)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "profile.save",
            description: "Folds a seat's live SESSION rebinds into its selected profile's durable bindings section and persists (through the server-owned player document, gated on the Edit capability): profile.save [seat] (optional seat 1..4, default 1). The session layer then empties. A friendly no-op when the seat has no unsaved rebinds.",
            handler: SaveHandler,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "profile.section",
            description: "Durably edits ONE section of a profile through the player-document protocol — the raw SetPlayerSection reflection an editor/agent drives (the typed profile.set/profile.save are sugar over the same wire): profile.section <profile-id> <identity|motion|bindings|preferences> <compact-json>. identity = {\"name\":…,\"color\":\"#RRGGBB\"}; motion = {\"moveSpeed\":…,\"turnSpeed\":…,\"invertLookX\":…}; bindings = a BindingProfileDocument (or null to inherit the engine default); preferences = a JSON object (or null to clear the bag). The payload is one compact (whitespace-free) JSON token. The server validates the candidate document through the thick gate, updates the live handle, bumps the revision, and persists; a malformed payload or a validation failure rejects loudly. Gated on the Edit capability.",
            handler: SectionHandler,
            routing: CommandRouting.Simulation
        );
    }

    private CommandResult BindHandler(CommandContext context, string[] args) {
        if (args.Length != 3) {
            return Error(output: "[player.bind: expected <seat> <source> <command> — seat 1..4]");
        }

        if (!WorldArgs.TryParseIndex(args: args, at: 0, min: 1, max: PlayerRoster.MaxSlots, fallback: null, value: out var seat)) {
            return Error(output: $"[player.bind: <seat> must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        var source = args[1];
        var command = args[2];

        if (string.IsNullOrWhiteSpace(value: source) || string.IsNullOrWhiteSpace(value: command)) {
            return Error(output: "[player.bind: <source> and <command> must be non-empty]");
        }

        var slot = PlayerRoster.SlotFromDisplay(number: seat);
        var rebind = UpsertRebind(current: m_seatBindings.SessionRebind(slot: slot), source: source, command: command);

        // Verify the composed result compiles before installing it, so the echo is truthful; SetSessionRebind's own
        // recompose is the belt-and-braces.
        try {
            _ = BindingProfile.Compile(document: WorldBindingComposer.Compose(WorldDefaultBindings.BuildDocument(), rebind));
        } catch (ArgumentException exception) {
            return Error(output: $"[player.bind: '{source}' → '{command}' does not compile ({exception.Message.ReplaceLineEndings(replacementText: " ")})]");
        }

        m_seatBindings.SetSessionRebind(slot: slot, rebinds: rebind);

        return new CommandResult(Output: $"[player.bind: seat {seat} '{source}' → '{command}' (unsaved — profile.save to persist)]");
    }

    private CommandResult BindingsHandler(CommandContext context, string[] args) {
        if (args.Length > 1) {
            return Error(output: "[player.bindings: expected at most 1 value — an optional seat index]");
        }

        if (!WorldArgs.TryParseIndex(args: args, at: 0, min: 1, max: PlayerRoster.MaxSlots, fallback: 1, value: out var seat)) {
            return Error(output: $"[player.bindings: seat must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        var slot = PlayerRoster.SlotFromDisplay(number: seat);
        var document = m_seatBindings.ComposedDocument(slot: slot);
        var builder = new StringBuilder(value: $"[player.bindings: seat {seat}");
        var seen = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var any = false;

        foreach (var page in (document.Pages ?? [])) {
            if (page.Chord is { Count: > 0 }) {
                continue;
            }

            foreach (var entry in (page.Entries ?? [])) {
                // One line per distinct source→command pair (a hold/release pair collapses to one).
                if (seen.Add(item: $"{entry.Source}\0{entry.Command}")) {
                    _ = builder.Append(value: any ? " | " : " ").Append(value: entry.Source).Append(value: "→").Append(value: entry.Command);
                    any = true;
                }
            }
        }

        if (!any) {
            _ = builder.Append(value: " (none)");
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }

    private CommandResult SaveHandler(CommandContext context, string[] args) {
        if (args.Length > 1) {
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
            return new CommandResult(Output: $"[profile.save: seat {seat} has no unsaved rebinds]");
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
        // couch co-op seat sharing it) off the now-durable handle — the one live-refresh path (§CR-5).
        m_seatBindings.SetSessionRebind(slot: slot, rebinds: null);
        RefreshSeatsBoundTo(profileId: profile.Id);

        return new CommandResult(Output: $"[profile.save: seat {seat} → profile '{profile.Name}' bindings saved]");
    }

    private CommandResult SectionHandler(CommandContext context, string[] args) {
        if (args.Length < 3) {
            return Error(output: "[profile.section: expected <profile-id> <section> <compact-json> — section is identity|motion|bindings|preferences]");
        }

        if (!TryParseSection(token: args[1], section: out var section)) {
            return Error(output: $"[profile.section: unknown section '{args[1]}' — identity|motion|bindings|preferences]");
        }

        // The raw JSON after the id and section tokens — reconstructed from the submitted line so inline-JSON quotes
        // survive the console tokenizer (the same discipline the world.*.set mutation verbs use).
        var payload = RawPayloadAfter(context: context, args: args, argTokens: 2);

        if (string.IsNullOrWhiteSpace(value: payload)) {
            return Error(output: "[profile.section: expected a compact (whitespace-free) JSON payload]");
        }

        var reply = m_link.SubmitSession(request: new SessionRequest.SetPlayerSection(
            Principal: WorldPrincipal.Console,
            ProfileId: args[0],
            Section: section,
            Payload: payload
        ));

        if (!reply.Accepted) {
            return Error(output: $"[profile.section: {reply.Reason}]");
        }

        // A durable BINDINGS edit must reach seated players LIVE (no reseat) — mirror profile.save's fold-refresh and
        // re-derive the profile-bindings layer for every active seat on this profile (§CR-5). Identity/motion/preferences
        // carry no input mapping, so they need no seat recompose (color already refreshes server-side).
        if (section == WorldPlayerSection.Bindings) {
            RefreshSeatsBoundTo(profileId: args[0]);
        }

        return new CommandResult(Output: $"[profile.section: {args[0]} {section.ToString().ToLowerInvariant()} applied]");
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

    private static bool TryParseSection(string token, out WorldPlayerSection section) {
        switch (token.ToLowerInvariant()) {
            case "identity":
                section = WorldPlayerSection.Identity;

                return true;
            case "motion":
                section = WorldPlayerSection.Motion;

                return true;
            case "bindings":
                section = WorldPlayerSection.Bindings;

                return true;
            case "preferences":
                section = WorldPlayerSection.Preferences;

                return true;
            default:
                section = WorldPlayerSection.Identity;

                return false;
        }
    }

    // The raw text after the verb token and the first argTokens argument tokens — the payload with quotes intact. The
    // split-args join is a defensive fallback for the (compact-JSON) case where no whitespace splits the token.
    private static string RawPayloadAfter(CommandContext context, string[] args, int argTokens) {
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

        return ((args.Length > argTokens) ? string.Join(separator: ' ', values: args[argTokens..]) : string.Empty);
    }

    // Build the seat's session-rebind base-page document: keep every prior rebind for OTHER sources, replace this
    // source's entry with the new source→command (AnyModifiers, so the remap keeps working with an incidental modifier).
    private static BindingProfileDocument UpsertRebind(BindingProfileDocument? current, string source, string command) {
        var entries = new List<BindingPageEntryDefinition>();

        foreach (var page in (current?.Pages ?? [])) {
            if (page.Chord is { Count: > 0 }) {
                continue;
            }

            foreach (var entry in (page.Entries ?? [])) {
                if (!string.Equals(a: entry.Source, b: source, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    entries.Add(item: entry);
                }
            }
        }

        entries.Add(item: new BindingPageEntryDefinition(Source: source, Command: command, AnyModifiers: true));

        return new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Pages: [
                new BindingPageDefinition(Id: WorldDefaultBindings.BasePageId, Chord: [], Entries: entries),
            ]
        );
    }

    private static CommandResult Error(string output) => new(Output: output) {
        IsError = true,
    };
}
