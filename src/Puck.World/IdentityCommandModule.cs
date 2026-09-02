using System.Globalization;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>The owned-world identity console surface.</summary>
internal sealed class IdentityCommandModule(WorldOwnedWorlds worlds, PlayerRoster roster, WorldServer server) : ICommandModule {
    private readonly WorldOwnedWorlds m_worlds = worlds;
    private readonly PlayerRoster m_roster = roster;
    private readonly WorldServer m_server = server;

    private CommandResult Create(CommandContext context, WireArgs args) {
        if (args.Count is not (1 or 2)) {
            return CommandResult.Error(output: "[identity.create: expected <id> [#RRGGBB]]");
        }
        var id = args[0].ToString();
        var color = ((args.Count == 2)
            ? args[1].ToString()
            : m_worlds.Defaults.NeutralColor
        );

        if (!WorldSafeName.TryParse(
            candidate: id,
            name: out var safeId,
            reason: out var nameReason
        )) {
            return CommandResult.Error(output: $"[identity.create: refused — '{id}' {nameReason}]");
        }

        var identity = m_worlds.Create(
            colorHex: color,
            name: safeId,
            reason: out var reason
        );

        return ((identity is null)
            ? CommandResult.Error(output: $"[identity.create: refused — {reason}]")
            : new CommandResult(Output: $"[identity.create: {identity.Id}:{identity.Name} {identity.ColorHex}]")
        );
    }
    // The minimal dev/test submitter for the text arm of the cross-document write-back door: constructs a
    // WorldDocumentSubmission with a Text operand and calls the SAME m_worlds.Submit the sim's own per-tick numeric
    // outputs call (Server.WorldServer.Step) — no separate door, no separate verdict path. The <set|add> token
    // carries the REQUESTED operation through even though the door refuses Add for text unconditionally — a harness
    // that only ever sent Set could never exercise that refusal, only assert it exists. StorageKind is a meaningless
    // placeholder (ActionStateKind.Counter): Decide ignores it once Text is populated, the SAME asymmetry
    // WorldStateCell.Value/.Text already carries.
    private CommandResult Deliver(CommandContext context, WireArgs args) {
        if (args.Count < 4) {
            return CommandResult.Error(output: "[identity.deliver: expected <source-id> <owner-id> <row> <set|add> <text...>]");
        }

        var sourceId = args[0].ToString();
        var ownerId = args[1].ToString();
        var rowName = args[2].ToString();
        var opToken = args[3].ToString();
        WorldDocumentWriteKind kind;

        if (string.Equals(
            a: opToken,
            b: "set",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            kind = WorldDocumentWriteKind.Set;
        } else if (string.Equals(
            a: opToken,
            b: "add",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            kind = WorldDocumentWriteKind.Add;
        } else {
            return CommandResult.Error(output: $"[identity.deliver: unknown operation '{opToken}' — expected 'set' or 'add']");
        }

        var text = DeliverTextTail(
            args: in args,
            context: context
        );
        var submission = new WorldDocumentSubmission(
            SourceDocumentId: sourceId,
            OwnerDocumentId: ownerId,
            Tick: m_server.NextInputTick,
            Slot: rowName,
            Kind: kind,
            StorageKind: ActionStateKind.Counter,
            Value: 0L,
            Text: text
        );
        var receipt = m_worlds.Submit(submission: submission);

        return new CommandResult(Output: $"[identity.deliver: source=world:{sourceId} owner=world:{ownerId} row={rowName} op={opToken.ToLowerInvariant()} verdict={(receipt.Accepted
            ? "accepted"
            : "refused")} reason={receipt.Reason}]");
    }
    // The raw text tail after the verb, source id, owner id, row, and op tokens — so interior spacing in <text...>
    // survives the console tokenizer untouched.
    private static string DeliverTextTail(CommandContext context, in WireArgs args) =>
        WorldCommandArguments.RawAfter(
            args: in args,
            context: context,
            tokens: 5
        );
    // discarded=/refused= are the boot-time sweep's read-back, and this is the only surface that still names either
    // after the boot lines have scrolled away: discarded= names what was moved out of the directory once, refused=
    // what is still sitting there with its original bytes and will be refused again on the next boot.
    private string Describe() => $"[identity.list: {string.Join(
        separator: ", ",
        values: m_worlds.All.Select(selector: identity => $"{identity.Id}:{identity.Name}:{identity.ColorHex}")
    )} root={m_worlds.FilePath} discarded={((m_worlds.Discarded.Count == 0)
        ? "none"
        : $"{m_worlds.Discarded.Count}:{string.Join(
            separator: ",",
            values: m_worlds.Discarded.Select(selector: entry => entry.FileName)
        )}"
    )} refused={((m_worlds.Refused.Count == 0)
        ? "none"
        : $"{m_worlds.Refused.Count}:{string.Join(
            separator: ",",
            values: m_worlds.Refused.Select(selector: entry => entry.FileName)
        )}"
    )}]";
    // identity.hud's read-back half: identity.show already reports every other identity-owned setting as one
    // space-delimited key=value line, so the panel state joins it in the SAME space-free-value shape (rather than a
    // separate no-arg identity.hud overload, which would collide with identity.hud's own required <panel-json>
    // positional argument) — world.hud seat:<n> remains the per-element, per-line read-back for a JOINED seat.
    private static string DescribeHudSummary(WorldIdentity identity) =>
        ((identity.Hud is { } panel)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{panel.Id}:{panel.Layer.ToString().ToLowerInvariant()}:{panel.Style.ToString().ToLowerInvariant()}:elements={panel.Elements.Count}/{WorldHudCapacity.MaxElementsPerSeatPanel}"
            )
            : "none"
        );
    // identity.state's handler — reads a named row off an owned identity's OWN document, addressed by id rather
    // than by joined seat (WorldIdentity.TryReadState, the SAME reader the cross-document write-back door itself
    // writes through). Mirrors WorldStateCommandModule.DescribeRow's slot-vs-keyed split at a smaller grain: a slot
    // row's one value inline, a keyed row's cell count — no per-key read here since nothing in this door's contract
    // needs one yet.
    private CommandResult DescribeIdentityState(WireArgs args) {
        if (args.Count != 2) {
            return CommandResult.Error(output: "[identity.state: expected <id> <row>]");
        }

        var id = args[0].ToString();
        var rowName = args[1].ToString();

        if (m_worlds.FindById(id: id) is not { } identity) {
            return CommandResult.Error(output: $"[identity.state: no owned identity '{id}']");
        }

        if (
            !identity.TryReadState(
            name: rowName,
            row: out var row
        ) ||
            (row is null)
        ) {
            return CommandResult.Error(output: $"[identity.state {id}: no such row '{rowName}']");
        }

        if (!row.IsSlot) {
            return new CommandResult(Output: $"[identity.state: {id}.{rowName} kind={row.Kind.ToString().ToLowerInvariant()} cells={(row.Cells?.Count ?? 0)}]");
        }

        var cell = row.Cells![0];
        var value = ((row.Kind == CellKind.Text)
            ? $"'{cell.Text}'"
            : cell.Value.ToString(provider: CultureInfo.InvariantCulture)
        );

        return new CommandResult(Output: $"[identity.state: {id}.{rowName} kind={row.Kind.ToString().ToLowerInvariant()} value={value}]");
    }
    private string DescribeWriteback() {
        if (m_worlds.LastReceipt is not { } receipt) {
            return "[identity.writebacks: none]";
        }
        var submission = receipt.Submission;
        var operand = ((submission.Text is { } text)
            ? $"'{text}'"
            : submission.Value.ToString(provider: CultureInfo.InvariantCulture)
        );

        return $"[identity.writebacks: source=world:{submission.SourceDocumentId} owner=world:{submission.OwnerDocumentId} tick={submission.Tick} slot={submission.Slot} operation={submission.Kind.ToString().ToLowerInvariant()} value={operand} verdict={(receipt.Accepted
            ? "accepted"
            : "refused")} reason={receipt.Reason}]";
    }
    // Strips the verb token, then — only when a trailing player token is present (args.Count == 2) — the LAST
    // whitespace-delimited token. Reconstructed from the raw command text rather than args[0] so the JSON's quotes
    // survive the console tokenizer, and delegated to WorldCommandArguments so BOTH ends of that reconstruction
    // split on the rule the registry's own tokenizer split the line with: the hand-rolled `anyOf: [' ', '\t']` scan
    // that used to sit here found no separator in `identity.hud <json>\v<player>`, so the player index stayed glued
    // to a payload the tokenizer had already separated, and the JSON parse refused a line the verb had accepted.
    private static string RawPanelJson(CommandContext context, in WireArgs args) =>
        WorldCommandArguments.RawBetween(
            args: in args,
            context: context,
            leadingTokens: 1,
            trailingTokens: ((args.Count < 2)
                ? 0
                : 1)
        );
    // The identity-owned PRIVATE seat panel: identity.hud <panel-json> [player]. panel-json is required to be one
    // compact (whitespace-free) WorldHudPanel token — the same authoring convention world.row.set hud.panels and the
    // deleted profile.section door both used — so an optional trailing player index (like identity.motion's) can be
    // told apart from the JSON by position alone, with no quote-preserving tokenizer needed for either half.
    private CommandResult SetHud(CommandContext context, WireArgs args) {
        if (args.Count is not (1 or 2)) {
            return CommandResult.Error(output: "[identity.hud: expected <panel-json> [player] — panel-json is one compact (whitespace-free) WorldHudPanel {id, rect, layer, style, elements}]");
        }
        if (!TryPlayer(
            args: in args,
            context: context,
            error: out var error,
            identity: out var identity,
            optionalAt: 1,
            player: out var player,
            verb: "identity.hud"
        )) {
            return CommandResult.Error(output: error);
        }
        if (identity!.Document is not { } document) {
            return CommandResult.Error(output: $"[identity.hud: p{player} world:{identity.Id} has no owned document to persist into]");
        }

        var raw = RawPanelJson(
            args: in args,
            context: context
        );

        if (!WorldJsonPayload.TryParse(
            json: raw,
            info: WorldJsonContext.Default.WorldHudPanel,
            value: out var panel,
            error: out var parseError
        )) {
            return CommandResult.Error(output: $"[identity.hud: {parseError}]");
        }

        // Compose the candidate document with ONLY the Hud section replaced (WorldIdentity.Hud reads Panels[0], so
        // one panel is the whole seat-scope section) and run it through the SAME WorldDefinitionValidator every
        // owned world already loads and saves through — never a hand-rolled check, and never a check that lives
        // only in this verb (WorldDefinitionValidator.ValidateHudCore applies the seat-scope element cap and the
        // Replace refusal to any document carrying an Identity section, including a boot load or a sync pull, not
        // just this door). A refusal leaves both the document and identity.Hud untouched.
        var candidate = document with { HudRaw = document.Hud with { Panels = [panel] } };

        // No neighbour resolver: an owned identity's own document edit (a HUD panel), not a document load.
        if (!WorldDefinitionValidator.TryValidate(
            definition: candidate,
            neighbours: null,
            reason: out var reason
        )) {
            return CommandResult.Error(output: $"[identity.hud: refused — {reason}]");
        }

        // Applies inline over loopback — no WorldMutation, exactly like identity.motion. WorldHudFeed.BuildSeatPanels
        // and world.hud seat:<n> both read roster.ProfileAt(slot).Hud fresh every call, so the panel is live for the
        // NEXT produced frame and for an Immediate read-back with no revision bump or extra propagation seam needed.
        identity.ReplaceDocument(document: candidate);
        identity.Hud = panel;
        m_worlds.Save();

        return new CommandResult(Output: $"[identity.hud: p{player} panel '{panel.Id}' updated in world:{identity.Id}]");
    }
    private CommandResult SetMotion(CommandContext context, WireArgs args) {
        if (
            (args.Count < 2) ||
            (args.Count > 3)
        ) {
            return CommandResult.Error(output: "[identity.motion: expected <speed|turn-speed> <value> [player]]");
        }
        if (!TryPlayer(
            args: in args,
            context: context,
            error: out var error,
            identity: out var identity,
            optionalAt: 2,
            player: out var player,
            verb: "identity.motion"
        )) {
            return CommandResult.Error(output: error);
        }
        var key = args[0].ToString();

        // The write lands on the CATALOG's identity — the object the seated body reads its rates off live — and is
        // mirrored onto the roster's rehydrated copy so client read-backs agree. A seat driving under an identity
        // this catalog does not own (a visiting traveler's) has no document here to persist into, so it refuses.
        if (m_worlds.FindById(id: identity!.Id) is not { } owned) {
            return CommandResult.Error(output: $"[identity.motion: p{player} world:{identity.Id} is not an owned world here]");
        }

        if (
            !args.TryFloat(
            index: 1,
            value: out var value
        ) ||
            !float.IsFinite(f: value) ||
            (value <= 0f)
        ) {
            return CommandResult.Error(output: "[identity.motion: numeric value must be finite and positive]");
        } else if (string.Equals(
            a: key,
            b: "speed",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            owned.SetMoveSpeed(value: value);
            identity.SetMoveSpeed(value: value);
        } else if (string.Equals(
            a: key,
            b: "turn-speed",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            owned.SetTurnSpeed(value: value);
            identity.SetTurnSpeed(value: value);
        } else {
            return CommandResult.Error(output: $"[identity.motion: unknown key '{key}']");
        }
        m_worlds.Save();
        return new CommandResult(Output: $"[identity.motion: p{player} {key} updated in world:{identity.Id}]");
    }
    private CommandResult Show(CommandContext context, WireArgs args) {
        if (!TryPlayer(
            args: in args,
            context: context,
            error: out var error,
            identity: out var identity,
            optionalAt: 0,
            player: out var player,
            verb: "identity.show"
        )) {
            return CommandResult.Error(output: error);
        }
        var hud = DescribeHudSummary(identity: identity!);
        // moveEffective is WorldBody's OWN read-back (EffectiveMoveSpeed), never a re-derivation here, so this line
        // can never disagree with what the body is really doing — grounded or vehicle, WorldBody's per-arm
        // resolve decides which envelope (if any) clamps which base rate. No live body (not yet seated) reports the
        // claimed rate, or "kit" for an identity claiming none.
        var effectiveMoveSpeed = ((m_server.Body(index: PlayerRoster.SlotFromDisplay(number: player)) is { } body)
            ? DescribeRate(rate: body.EffectiveMoveSpeed)
            : DescribeRate(rate: identity!.FixedMoveSpeed)
        );

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[identity.show: p{player} world={identity!.Id} name={identity.Name} color={identity.ColorHex} move={DescribeRate(rate: identity.FixedMoveSpeed)} moveEffective={effectiveMoveSpeed} turn={DescribeRate(rate: identity.FixedTurnSpeed)} hud={hud} path={m_worlds.FilePath}]"
        ));
    }
    // An identity claiming no rate reads "kit": the seat integrates under the kit's own authored rate.
    private static string DescribeRate(FixedQ4816? rate) =>
        ((rate is { } value)
            ? ((double)value).ToString(
                format: "0.####",
                provider: CultureInfo.InvariantCulture
            )
            : "kit"
        );
    private static bool TryBool(ReadOnlySpan<char> token, out bool value) {
        value = (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "on"
        ) || token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "true"
        ));
        return (
            value ||
            token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "off"
        ) ||
            token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "false"
        )
        );
    }
    private bool TryPlayer(CommandContext context, in WireArgs args, int optionalAt, string verb, out int player, out WorldIdentity? identity, out string error) {
        identity = null;
        error = string.Empty;

        var (slot, seatError) = SeatCommandArgs.ResolveSlot(
            args: in args,
            at: optionalAt,
            context: context,
            verb: verb
        );

        if (seatError is { } refusal) {
            player = 0;
            error = refusal.Output;

            return false;
        }

        player = PlayerRoster.DisplayNumber(slot: slot);
        identity = m_roster.ProfileAt(slot: slot);

        if (identity is null) {
            error = $"[{verb}: player {player} is not joined]";

            return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "identity.list",
            description: "Lists owned identity worlds, their paths, any documents this catalog discarded at boot (moved into unloadable/), and any it refused in place (still in the catalog directory with their original bytes).",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: Describe())
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "identity.create",
            description: "Creates an owned identity world: identity.create <id> [#RRGGBB].",
            handler: Create
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "identity.show",
            description: "Shows the identity driving a seat, including its private HUD panel (id/layer/style/rect/element count, or 'none') and its move rate BOTH as claimed and as actually applied: identity.show [player]. move= is the profile's own claimed rate, or 'kit' for an identity claiming none (the kit's authored rate then drives, until identity.motion mints a claim); moveEffective= is what the sim integrates under, arm-aware — under a grounded kit, the claimed-or-kit rate after the kit's own moveSpeedEnvelope clamps it; under a VEHICLE kit, the kit's OWN topSpeed after its topSpeedEnvelope (if authored) clamps it — move= never applies to a vehicle seat, since a kart's speed is the kit's, not the profile's.",
            handler: Show
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "identity.motion",
            description: "Sets identity-owned motion slots: identity.motion <speed|turn-speed> <value> [player]. Camera preferences are authored as playerDefaults.seatLook on the identity document.",
            handler: SetMotion,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "identity.hud",
            description: "Replaces the identity-owned PRIVATE seat panel: identity.hud <panel-json> [player] — panel-json is one compact (whitespace-free) inline WorldHudPanel {id, rect, layer, style, elements}, validated through the SAME document validator every owned world loads through (schema caps, the closed HudBindingVocabulary against this identity's OWN state section, and the seat-panel rules: elements capped at WorldHudCapacity.MaxElementsPerSeatPanel, WorldHudLayer.Replace refused). A rejection leaves the document untouched. Takes effect immediately (WorldHudFeed recomposes seat panels every produced frame off the live identity) and is echoed back by world.hud seat:<n> or identity.show. Fires inline over loopback, no WorldMutation — the owner-side identity door, ungated like identity.motion.",
            handler: SetHud,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "identity.writebacks",
            description: "Shows the latest owner-side cross-document durable-state verdict (numeric value= or text= depending on the submission's operand).",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: DescribeWriteback())
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "identity.deliver",
            description: "DEV/TEST cross-document TEXT delivery — issues one WorldDocumentSubmission carrying a TEXT operand through the SAME write-back door the sim's own per-tick numeric outputs use (Server.WorldOwnedWorlds.Decide): identity.deliver <source-id> <owner-id> <row> <set|add> <text...>. <source-id> is the asking document (the door checks OWNER'S OWN grants section for a Mutate/state:<row> hold naming document:<source-id> with a write mask admitting the requested operation); <owner-id> is the document the row lives in; <row> names an ALREADY-DECLARED text-kind state row in the OWNER's document (declare it there first — there is no spooled delivery for an undeclared row, which refuses by name with the remedy); <set|add> is the requested WorldDocumentWriteKind — carried through even though <add> against a text row ALWAYS refuses by name at the door regardless of what the write mask admits (text is Set-only; this harness carries both so that refusal is exercisable, not merely asserted), which is why it is a required token here rather than assumed Set; <text...> is the raw tail (everything after <set|add>, spaces included, capped at WorldStateCapacity.MaxTextValueLength). Applies inline and echoes the receipt immediately; identity.writebacks re-echoes the same receipt afterward. This is the MINIMAL test harness for the door only — the real, chat-integrated whisper verb (source id derived from the acting player's own identity) is chat.whisper (ChatCommandModule); this dev harness stays for probing the door with an arbitrary source id chat.whisper can never send from its own identity.",
            handler: (context, args) => Deliver(
                args: args,
                context: context
            ),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "identity.state",
            description: "Reads back ONE state row from an owned identity's OWN document, addressed by id — not by joined seat, since the row a cross-document delivery lands in belongs to the OWNER's document regardless of whether that identity is currently seated: identity.state <id> <row>. The same (row, kind, value) grain world.state's one-argument form reports for the running world's own document, over an owned IDENTITY document instead — the shape Server.WorldOwnedWorlds.Decide writes into. Refuses by name if <id> names no owned identity or <row> names no state row there.",
            handler: (_, args) => DescribeIdentityState(args: args),
            routing: CommandRouting.Immediate
        );
    }
}
