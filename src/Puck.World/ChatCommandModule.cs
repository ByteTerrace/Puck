using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>
/// Personal chat: a player is a world, and personal chat history is keyed text rows in that player's owned identity
/// document — a bounded, evicting <c>chat-log</c> row this player appends to, and a bounded, evicting
/// <c>chat-inbox</c> row a granted sender may deliver into cross-document. This module is the inbox-grant door: the
/// one in-session surface that lets a recipient declare its own inbox row and grant/revoke a sender's write access
/// to it, on the recipient's own owned identity document — closing the gap <see cref="IdentityCommandModule"/>'s own
/// remarks name (<c>identity.create</c> seeds <c>grants: []</c> with no verb to author it live).
/// </summary>
/// <remarks>
/// <para><b>The owner-only constraint, enforced at one door.</b> Every mutating verb here resolves its target
/// identity from a <c>player</c> argument (the same 1-based convention <see cref="IdentityCommandModule"/> uses,
/// via <see cref="PlayerRoster.ProfileAt(int)"/>) and then checks that <c>context.ActingPrincipal()</c> — never the
/// verb's own arguments — holds <see cref="WorldCapability.Drive"/> over that player's body: the same primitive
/// <c>player.identity</c>'s own authorization already uses (<c>Server.WorldServer</c>'s <c>SessionRequest.SetIdentity</c>
/// arm) to decide who may administer a seat's identity. A principal that does not hold Drive over the seat cannot
/// author that seat's inbox, grants, or log — full stop, regardless of what player index the caller typed.</para>
/// <para><b>Two argument conventions, deliberately different.</b> <see cref="Log"/>/<see cref="Whisper"/> carry
/// free-form text, so <c>player</c> is a required leading token there (never a trailing optional one) — a trailing
/// optional index is fundamentally ambiguous against a message that happens to end in a digit, so this module never
/// authors that ambiguity. Every other verb here takes no free text, so <c>player</c> stays a trailing optional
/// token (default 1), exactly like <c>identity.motion</c>/<c>identity.hud</c>.</para>
/// <para><b>Personal log auto-declare policy.</b> <see cref="Log"/> refuses by name with the remedy
/// (<c>chat.inbox</c> first) rather than silently auto-declaring — the same refuse-with-remedy doctrine
/// <c>Server.WorldOwnedWorlds.Decide</c> already applies to an undeclared cross-document delivery target,
/// restated here for a self-write so the two never disagree about what "undeclared" means.</para>
/// </remarks>
internal sealed class ChatCommandModule(WorldOwnedWorlds worlds, PlayerRoster roster, WorldServer server, WorldOverlayFacts facts) : ICommandModule {
    /// <summary>The bounded, evicting row every log/inbox declares — small enough to prove eviction with a short
    /// script, large enough to be a plausible chat window.</summary>
    private const int ChatCapacity = 8;

    private static readonly CellName LogRowName = CellName.Parse(candidate: "chat-log");
    private static readonly CellName InboxRowName = CellName.Parse(candidate: "chat-inbox");
    private readonly WorldOwnedWorlds m_worlds = worlds;
    private readonly PlayerRoster m_roster = roster;
    private readonly WorldServer m_server = server;
    private readonly WorldOverlayFacts m_facts = facts;

    private CommandResult Allow(CommandContext context, WireArgs args) {
        if (args.Count is not (1 or 2)) {
            return CommandResult.Error(output: "[chat.allow: expected <sender-id> [player]]");
        }
        if (!SafeName.TryParse(
            candidate: args[0].ToString(),
            name: out var senderId,
            reason: out var nameReason
        )) {
            return CommandResult.Error(output: $"[chat.allow: refused — '{args[0]}' {nameReason}]");
        }
        if (!TryAuthorizedIdentity(
            args: in args,
            context: context,
            error: out var error,
            identity: out var identity,
            optionalAt: 1,
            player: out var player,
            verb: "chat.allow"
        )) {
            return CommandResult.Error(output: error);
        }
        if (identity!.Document is not { } document) {
            return CommandResult.Error(output: $"[chat.allow: p{player} world:{identity.Id} has no owned document to persist into]");
        }
        if (WorldDefinitionRows.FindStateRow(
            rows: document.State,
            name: InboxRowName
        ) is null) {
            return CommandResult.Error(output: $"[chat.allow: p{player} world:{identity.Id} has no '{InboxRowName}' row — declare it first with chat.inbox]");
        }

        var principal = WorldPrincipal.Document(id: senderId);
        var subject = GrantSubject.State(name: InboxRowName);
        var grant = new WorldGrant(
            Principal: principal,
            Capability: WorldCapability.Mutate,
            Subject: subject,
            Exclusive: false,
            WriteMask: DocumentWriteMask.Empty.With(kind: WorldDocumentWriteKind.Set)
        );
        var candidate = (document with {
            GrantsRaw = [.. WithoutGrantRow(
                grants: document.Grants,
                principal: principal,
                subject: subject
            ), grant],
        });

        // No neighbour resolver: an owned identity's own document edit (a state/grant row), not a document load.
        if (!WorldDefinitionValidator.TryValidate(
            definition: candidate,
            neighbours: null,
            reason: out var reason
        )) {
            return CommandResult.Error(output: $"[chat.allow: refused — {reason}]");
        }

        identity.ReplaceDocument(document: candidate);
        m_worlds.Save();

        return new CommandResult(Output: $"[chat.allow: p{player} world:{identity.Id} allows document:{senderId} mutate state:{InboxRowName}]");
    }
    private CommandResult Block(CommandContext context, WireArgs args) {
        if (args.Count is not (1 or 2)) {
            return CommandResult.Error(output: "[chat.block: expected <sender-id> [player]]");
        }
        if (!SafeName.TryParse(
            candidate: args[0].ToString(),
            name: out var senderId,
            reason: out var nameReason
        )) {
            return CommandResult.Error(output: $"[chat.block: refused — '{args[0]}' {nameReason}]");
        }
        if (!TryAuthorizedIdentity(
            args: in args,
            context: context,
            error: out var error,
            identity: out var identity,
            optionalAt: 1,
            player: out var player,
            verb: "chat.block"
        )) {
            return CommandResult.Error(output: error);
        }
        if (identity!.Document is not { } document) {
            return CommandResult.Error(output: $"[chat.block: p{player} world:{identity.Id} has no owned document to persist into]");
        }

        var principal = WorldPrincipal.Document(id: senderId);
        var subject = GrantSubject.State(name: InboxRowName);
        var without = WithoutGrantRow(
            grants: document.Grants,
            principal: principal,
            subject: subject
        );
        var removed = (without.Count != document.Grants.Count);
        var candidate = (document with { GrantsRaw = without });

        // No neighbour resolver: an owned identity's own document edit (a state/grant row), not a document load.
        if (!WorldDefinitionValidator.TryValidate(
            definition: candidate,
            neighbours: null,
            reason: out var reason
        )) {
            return CommandResult.Error(output: $"[chat.block: refused — {reason}]");
        }

        identity.ReplaceDocument(document: candidate);
        m_worlds.Save();

        return new CommandResult(Output: $"[chat.block: p{player} world:{identity.Id} blocks document:{senderId} on state:{InboxRowName} removed={(removed
            ? "true"
            : "false")}]");
    }
    private static string DescribeRow(WorldIdentity identity, CellName rowName) {
        if (
            !identity.TryReadState(
            name: rowName,
            row: out var row
        ) ||
            (row.Cells is not { Count: > 0 } cells)
        ) {
            return string.Empty;
        }

        return string.Join(
            separator: ",",
            values: cells.Select(selector: cell => $"{cell.Key}:'{cell.Text}'")
        );
    }
    private CommandResult Inbox(CommandContext context, WireArgs args) {
        if (!TryAuthorizedIdentity(
            args: in args,
            context: context,
            error: out var error,
            identity: out var identity,
            optionalAt: 0,
            player: out var player,
            verb: "chat.inbox"
        )) {
            return CommandResult.Error(output: error);
        }
        if (identity!.Document is not { } document) {
            return CommandResult.Error(output: $"[chat.inbox: p{player} world:{identity.Id} has no owned document to persist into]");
        }

        var hasLog = (WorldDefinitionRows.FindStateRow(
            rows: document.State,
            name: LogRowName
        ) is not null);
        var hasInbox = (WorldDefinitionRows.FindStateRow(
            rows: document.State,
            name: InboxRowName
        ) is not null);

        if (
            hasLog &&
            hasInbox
        ) {
            return new CommandResult(Output: $"[chat.inbox: p{player} world:{identity.Id} already declared (log={LogRowName} inbox={InboxRowName} capacity={ChatCapacity})]");
        }

        var state = document.State.ToList();

        if (!hasLog) {
            state.Add(item: new WorldStateRow(
                Name: LogRowName,
                Kind: CellKind.Text,
                Capacity: ChatCapacity,
                Evicts: true
            ));
        }
        if (!hasInbox) {
            state.Add(item: new WorldStateRow(
                Name: InboxRowName,
                Kind: CellKind.Text,
                Capacity: ChatCapacity,
                Evicts: true
            ));
        }

        var candidate = document.WithWorldState(rows: state);

        // No neighbour resolver: an owned identity's own document edit (a state/grant row), not a document load.
        if (!WorldDefinitionValidator.TryValidate(
            definition: candidate,
            neighbours: null,
            reason: out var reason
        )) {
            return CommandResult.Error(output: $"[chat.inbox: refused — {reason}]");
        }

        identity.ReplaceDocument(document: candidate);
        m_worlds.Save();

        return new CommandResult(Output: $"[chat.inbox: p{player} world:{identity.Id} declared (log={LogRowName} inbox={InboxRowName} capacity={ChatCapacity})]");
    }
    private CommandResult Log(CommandContext context, WireArgs args) {
        if (
            (args.Count < 1) ||
            !args.TryInt(
            index: 0,
            value: out var player
        ) ||
            (player < 1) ||
            (player > PlayerRoster.MaxSlots)
        ) {
            return CommandResult.Error(output: $"[chat.log: expected <player 1..{PlayerRoster.MaxSlots}> <text...>]");
        }
        if (!TryAuthorize(
            context: context,
            error: out var authError,
            player: player,
            verb: "chat.log"
        )) {
            return CommandResult.Error(output: authError);
        }
        if (!TryResolvePlayer(
            error: out var resolveError,
            identity: out var identity,
            player: player,
            verb: "chat.log"
        )) {
            return CommandResult.Error(output: resolveError);
        }
        if (identity!.Document is null) {
            return CommandResult.Error(output: $"[chat.log: p{player} world:{identity.Id} has no owned document to persist into]");
        }

        var text = RawTail(
            args: in args,
            argsTailSkip: 1,
            context: context,
            skipTokensIncludingVerb: 2
        );

        if (!identity.TryAppendEvictingText(
            evictedKey: out var evicted,
            reason: out var appendReason,
            rowName: LogRowName,
            text: text
        )) {
            return CommandResult.Error(output: $"[chat.log: p{player} world:{identity.Id} refused — {appendReason} — declare it first with chat.inbox]");
        }

        m_worlds.Save();
        NoteSpoke(player: player);

        return new CommandResult(Output: $"[chat.log: p{player} world:{identity.Id} appended{((evicted is { } victim)
            ? $" evicted={victim}"
            : string.Empty)}]");
    }
    // A spoken line stamps the speech clock for the body the player drives (its own body when the slot drives
    // nothing), so a Speaking predicate or a RecentSpeaker anchor follows the driven avatar.
    private void NoteSpoke(int player) {
        var slot = PlayerRoster.SlotFromDisplay(number: player);
        var target = m_roster.DriveTarget(slot: slot);

        m_facts.NoteSpoke(bodyIndex: ((((uint)target) < ((uint)WorldClient.EntityCapacity))
            ? target
            : slot
        ));
    }
    // The raw text tail after skipTokensIncludingVerb whitespace-delimited tokens (verb included) — the SAME
    // reconstruction-from-raw-line approach WorldStateCommandModule.RawTextTail/IdentityCommandModule.DeliverTextTail
    // use, so interior spacing in <text...> survives the console tokenizer untouched.
    private static string RawTail(CommandContext context, in WireArgs args, int skipTokensIncludingVerb, int argsTailSkip) {
        if (context.Text is { } text) {
            var span = text.AsSpan().TrimStart();

            for (var skip = 0; (skip < skipTokensIncludingVerb); skip++) {
                var separator = span.IndexOfAny(
                    value0: ' ',
                    value1: '\t'
                );

                if (separator < 0) {
                    return string.Empty;
                }

                span = span[(separator + 1)..].TrimStart();
            }

            return span.Trim().ToString();
        }

        return args.Tail(start: argsTailSkip);
    }
    private CommandResult Read(CommandContext context, WireArgs args) {
        if (!TryAuthorizedIdentity(
            args: in args,
            context: context,
            error: out var error,
            identity: out var identity,
            optionalAt: 0,
            player: out var player,
            verb: "chat.read"
        )) {
            return CommandResult.Error(output: error);
        }

        var log = DescribeRow(
            identity: identity!,
            rowName: LogRowName
        );
        var inbox = DescribeRow(
            identity: identity!,
            rowName: InboxRowName
        );

        return new CommandResult(Output: $"[chat.read: p{player} world:{identity!.Id} log=[{log}] inbox=[{inbox}]]");
    }
    // THE owner-only constraint, at the ONE call site every mutating (and, for privacy, every reading) verb in this
    // module routes through: context.ActingPrincipal() — never a verb argument — must hold Drive over the target
    // player's body, the SAME primitive player.identity's own authorization already checks
    // (Server.WorldServer's SessionRequest.SetIdentity arm: "the ACTOR's Drive over the target body").
    private bool TryAuthorize(CommandContext context, int player, string verb, out string error) {
        var slot = PlayerRoster.SlotFromDisplay(number: player);
        var acting = context.ActingPrincipal();
        var verdict = m_server.Grants.Allows(
            principal: acting,
            capability: WorldCapability.Drive,
            subject: GrantSubject.Body(index: slot)
        );

        if (!verdict.IsAllowed) {
            error = $"[{verb}: {verdict.DescribeRefusal(
                actor: acting,
                subject: $"player {player}'s identity",
                verb: "author"
            )}]";

            return false;
        }

        error = string.Empty;

        return true;
    }
    // Resolves [player] at optionalAt (trailing, default 1) and checks the OWNER-ONLY constraint (ActingPrincipal
    // holds Drive over the player's body) BEFORE resolving the identity itself — a non-owner is refused before this
    // door tells them anything about whether the target is even joined.
    private bool TryAuthorizedIdentity(CommandContext context, in WireArgs args, int optionalAt, string verb, out int player, out WorldIdentity? identity, out string error) {
        identity = null;

        var (slot, seatError) = SeatCommandArgs.ResolveSlot(
            args: in args,
            at: optionalAt,
            context: context,
            defaultSlot: 0,
            verb: verb
        );

        if (seatError is { } refusal) {
            player = 1;
            error = refusal.Output;

            return false;
        }

        player = PlayerRoster.DisplayNumber(slot: slot);

        return (
            TryAuthorize(
            context: context,
            error: out error,
            player: player,
            verb: verb
        ) &&
            TryResolvePlayer(
            error: out error,
            identity: out identity,
            player: player,
            verb: verb
        )
        );
    }
    private bool TryResolvePlayer(int player, string verb, out WorldIdentity? identity, out string error) {
        identity = m_roster.ProfileAt(slot: PlayerRoster.SlotFromDisplay(number: player));

        if (identity is null) {
            error = $"[{verb}: player {player} is not joined]";

            return false;
        }

        error = string.Empty;

        return true;
    }
    private CommandResult Whisper(CommandContext context, WireArgs args) {
        if (args.Count < 2) {
            return CommandResult.Error(output: $"[chat.whisper: expected <player 1..{PlayerRoster.MaxSlots}> <recipient-id> <text...>]");
        }

        var (whisperSlot, whisperSeatError) = SeatCommandArgs.ResolveSlot(
            args: in args,
            at: 0,
            context: context,
            verb: "chat.whisper"
        );

        if (whisperSeatError is not null) {
            return CommandResult.Error(output: $"[chat.whisper: expected <player 1..{PlayerRoster.MaxSlots}> <recipient-id> <text...>]");
        }

        var player = PlayerRoster.DisplayNumber(slot: whisperSlot);

        if (!SafeName.TryParse(
            candidate: args[1].ToString(),
            name: out var recipientId,
            reason: out var nameReason
        )) {
            return CommandResult.Error(output: $"[chat.whisper: refused — '{args[1]}' {nameReason}]");
        }
        if (!TryAuthorize(
            context: context,
            error: out var authError,
            player: player,
            verb: "chat.whisper"
        )) {
            return CommandResult.Error(output: authError);
        }
        if (!TryResolvePlayer(
            error: out var resolveError,
            identity: out var identity,
            player: player,
            verb: "chat.whisper"
        )) {
            return CommandResult.Error(output: resolveError);
        }

        var text = RawTail(
            args: in args,
            argsTailSkip: 2,
            context: context,
            skipTokensIncludingVerb: 3
        );
        var submission = new WorldDocumentSubmission(
            SourceDocumentId: identity!.Id,
            OwnerDocumentId: recipientId,
            Tick: m_server.NextInputTick,
            Slot: InboxRowName,
            Kind: WorldDocumentWriteKind.Set,
            StorageKind: ActionStateKind.Counter,
            Value: 0L,
            Text: text
        );
        var receipt = m_worlds.Submit(submission: submission);

        if (receipt.Accepted) {
            NoteSpoke(player: player);
        }

        return new CommandResult(Output: $"[chat.whisper: from=world:{identity.Id} to=world:{recipientId} verdict={(receipt.Accepted
            ? "accepted"
            : "refused")} reason={receipt.Reason}]");
    }
    // The document-authored grant row set with any row matching (principal, Mutate, subject) removed — the
    // upsert-by-key half chat.allow's re-grant and chat.block's revoke both need, since neither goes through the
    // LIVE table's own TryGrant re-grant merge (a document-authored row is edited directly, like identity.hud edits
    // the Hud section directly).
    private static IReadOnlyList<WorldGrant> WithoutGrantRow(IReadOnlyList<WorldGrant> grants, WorldPrincipal principal, GrantSubject subject) =>
        [.. grants.Where(predicate: grant => !((grant.Principal == principal) && (grant.Capability == WorldCapability.Mutate) && (grant.Subject == subject)))];

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "chat.inbox",
            description: "Declares this player's OWN chat rows on their owned identity document — a bounded, evicting 'chat-log' row (appended to by chat.log) and a bounded, evicting 'chat-inbox' row (delivered into by a whisper from a sender this player later allows via chat.allow): chat.inbox [player]. [player] defaults to 1. Idempotent — a row already declared is left untouched. Owner-only: context.ActingPrincipal() must hold Drive over the target player's body (the SAME check player.identity uses) — a non-owner is refused by name.",
            handler: Inbox,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "chat.allow",
            description: "Grants a sender document:<sender-id> Mutate+state:chat-inbox (Set-only) on this player's OWN owned identity document, so that sender's chat.whisper may land in this player's inbox: chat.allow <sender-id> [player]. Requires chat-inbox already declared (chat.inbox first) — refused by name with the remedy otherwise. Idempotent — re-allowing the same sender refreshes the row. Owner-only, identically to chat.inbox.",
            handler: Allow,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "chat.block",
            description: "Revokes a sender's chat-inbox grant on this player's OWN owned identity document — the sender's NEXT chat.whisper is refused by name: chat.block <sender-id> [player]. Idempotent (no error if the sender was never allowed; the echo names whether a row was actually removed). Owner-only, identically to chat.inbox.",
            handler: Block,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "chat.log",
            description: $"Appends one message to the acting player's OWN bounded, evicting chat-log row: chat.log <player 1..{PlayerRoster.MaxSlots}> <text...>. <player> is REQUIRED and LEADING (never a trailing optional index — free text could otherwise be misread as a player token); <text...> is the raw tail, spaces included, capped at {WorldStateCapacity.MaxTextValueLength} UTF-16 code units. Refuses by name with the remedy (declare chat.inbox first) if chat-log is undeclared. Owner-only, identically to chat.inbox. The echo names the evicted key when the write pushed the row past its {ChatCapacity}-entry capacity.",
            handler: Log,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "chat.whisper",
            description: $"Delivers text from the acting player's OWN identity to a recipient's chat-inbox row, through the SAME cross-document text delivery door Server.WorldOwnedWorlds.Decide already gates (the real verb — never the identity.deliver dev harness, and the source id is the acting player's OWN identity, never a caller-supplied string): chat.whisper <player 1..{PlayerRoster.MaxSlots}> <recipient-id> <text...>. Refuses by name when the recipient has not granted this sender (chat.allow), or has since revoked it (chat.block) — that refusal IS the offline/not-allowed boundary. Owner-only over the SENDING player, identically to chat.inbox.",
            handler: Whisper,
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "chat.read",
            description: "Reads back the acting player's OWN chat-log and chat-inbox rows, oldest entry first: chat.read [player]. [player] defaults to 1. Owner-only, identically to chat.inbox — chat content is private, so an operator needs the same Drive hold to READ a player's chat that every other chat.* verb requires to author it.",
            handler: Read,
            routing: CommandRouting.Immediate
        );
    }
}
