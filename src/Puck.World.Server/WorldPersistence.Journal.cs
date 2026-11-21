using System.Collections.Immutable;
using Puck.Commands;
using Puck.SignedDistance;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldPersistence {
    // Undoes the acting principal's last `count` edits, or with `redo` takes back the first `count` of the edits its
    // undos removed (the count clamps to at least 1). An edit is an entry the principal authored (WorldJournalEntry.Authored
    // and Author); an entry the World made itself is document history every replay keeps. An undo marks its entries
    // Undone where they stand and a redo clears the mark, so both are one computation: restore the base and replay every
    // entry not undone, in order, through compose. Every entry ahead of the first one the step toggles recomposes exactly
    // the document it composed over when it applied. The document the step installs is validated once, against the live
    // document, by the install door it takes (the narrow install a reload's edit takes, or the full install); no
    // intermediate replay document is installed, so none is validated. The step is ALL-OR-NOTHING: a compose that fails,
    // a later edit by another principal on a row the step would change, or the install door's refusal leaves the journal
    // and the live document as they stood.
    //
    // Authority is the edits' own. The step changes only the rows its toggled entries change (every other entry replays
    // to the document it already made, and a later edit on one of those rows refuses the step), so the principal needs
    // what submitting each toggled edit again would need: a mutation passes the one admission door every mutation
    // passes (WorldDocument.TryAdmitCompleteMutation), and a source edit, which can rebuild any section, needs the
    // operator and Mutate over every section, as the reload that made it did. A seat therefore undoes the edits it may
    // still make and no others, and what a principal who is not the operator is told names nothing of another's edits.
    internal bool ApplyUndo(int count, Principal principal, int connectionId, long correlationId, bool redo = false) {
        var verb = (redo ? "redo" : "undo");
        var isOperator = Host.Grants.IsOperator(principal: principal);
        var journal = Host.Document.Journal;
        // The principal's edits the step can reach, oldest first: the ones not undone for an undo, the undone ones for a
        // redo. A principal's undone edits are always the last of its edits (its next edit discards them,
        // WorldDocument.AppendJournal), so an undo takes the last it has not undone and a redo the first it has.
        var reachable = new List<int>();

        for (var index = 0; (index < journal.Count); index++) {
            if (journal[index] is { Authored: true } entry && (entry.Author == principal) && (entry.Undone == redo)) { reachable.Add(item: index); }
        }
        if (reachable.Count == 0) {
            var foreign = journal.Count(entry => !entry.Authored || (entry.Author != principal));

            return RefuseUndo(
                connectionId: connectionId,
                correlationId: correlationId,
                refusal: (((foreign == 0) || !isOperator)
                    ? $"{verb} refused: nothing to {verb}"
                    : $"{verb} refused: nothing to {verb} — the journal's other {foreign} entries are the World's own or other principals' edits, which are not {principal.Describe()}'s to {verb}")
            );
        }

        // A bounded journal (host.journalDepth > 0) has already folded what lies past its horizon into the base, so a
        // request past what remains cannot be satisfied by clamping to fewer without silently doing less than asked. An
        // unbounded journal (0) keeps the clamp: every entry is always still there to reach.
        var journalDepth = Host.Document.Definition.Host.JournalDepth;

        if (
            (journalDepth > 0) &&
            (count > reachable.Count)
        ) {
            return RefuseUndo(
                connectionId: connectionId,
                correlationId: correlationId,
                refusal: $"{verb} refused ({nameof(WorldUndoRefusal.PastHorizon)}): {count} requested, but host.journalDepth {journalDepth} bounds the horizon to the {reachable.Count} {(redo ? "undone " : string.Empty)}edits of {principal.Describe()}'s still in the journal — earlier edits have already compacted into the base"
            );
        }

        var take = Math.Clamp(
            value: count,
            min: 1,
            max: reachable.Count
        );
        var toggled = (redo ? reachable[..take] : reachable[^take..]);

        for (var position = 0; (position < toggled.Count); position++) {
            var entry = journal[toggled[position]];

            if (entry.ReloadPatch is null) {
                if (!Host.Document.TryAdmitCompleteMutation(mutation: entry.Mutation, preMetered: (position > 0), admission: out var admission)) {
                    Host.Document.DenyGrantTable(
                        denial: $"{principal.Describe()} {admission.Describe()} — world.{verb} of {entry.Description} dropped",
                        connectionId: connectionId,
                        correlationId: correlationId,
                        echoKind: WorldEditEchoKind.Mutation
                    );

                    return false;
                }

                continue;
            }
            if (!isOperator) {
                return RefuseUndo(connectionId: connectionId, correlationId: correlationId, refusal: $"world.{verb} of a source edit requires the operator");
            }
            if (!Host.GrantTable.AllowsAllSections(
                capability: WorldCapability.Mutate,
                denial: out var undoVerdict,
                deniedSection: out var undoSection,
                principal: principal
            )) {
                Host.Document.DenyGrantTable(
                    denial: $"{principal.Describe()} cannot mutate every section (section:{undoSection.ToString().ToLowerInvariant()} — {undoVerdict.DescribeDenial()}) — world.{verb} of a source edit dropped",
                    connectionId: connectionId,
                    correlationId: correlationId,
                    echoKind: WorldEditEchoKind.Mutation
                );

                return false;
            }
        }

        var next = journal;

        foreach (var index in toggled) { next = next.SetItem(index: index, value: (journal[index] with { Undone = !redo })); }

        // The replay, recording each later edit another principal made, for the conflict rule below.
        var candidate = Host.Tick.ProjectDealsForEdit(Host.Document.Base, previous: null);
        var prefix = candidate;
        List<(int Index, WorldDefinition Before, WorldDefinition After)>? others = null;

        for (var index = 0; (index < next.Count); index++) {
            var entry = next[index];

            if (index == toggled[0]) { prefix = candidate; }
            if (entry.Undone) { continue; }
            if (!entry.TryCompose(Host, candidate, out var composed, out var composeReason)) {
                return RefuseUndo(
                    connectionId: connectionId,
                    correlationId: correlationId,
                    refusal: ((isOperator || (entry.Authored && (entry.Author == principal)))
                        ? $"{verb} refused: replay failed at journal entry {index} ({entry.Description}) — {composeReason}"
                        : $"{verb} refused: a later edit no longer applies without it; nothing was {(redo ? "redone" : "undone")}")
                );
            }

            var after = Host.Tick.ProjectDealsForEdit(composed, previous: candidate);

            if ((index > toggled[0]) && entry.Authored && (entry.Author != principal)) { (others ??= []).Add(item: (index, candidate, after)); }
            candidate = after;
        }

        // What the toggled entries change: composed alone again over the replay's document at the first of them (each
        // passed its gates when it applied). An undo moves the document from that composition back to the prefix, a redo
        // from the prefix to it.
        var with = prefix;

        foreach (var index in toggled) {
            if (!journal[index].TryCompose(Host, with, out var composed, out var composeReason)) {
                return RefuseUndo(
                    connectionId: connectionId,
                    correlationId: correlationId,
                    refusal: $"{verb} refused: replay failed at journal entry {index} ({journal[index].Description}) — {composeReason}"
                );
            }
            with = Host.Tick.ProjectDealsForEdit(composed, previous: with);
        }

        // An undo or redo is refused, naming the row and the principal, when a later edit by another principal changed a
        // row it would change: that edit was made over this one, and replaying it over the other side would drop or
        // bury what its author saw.
        if (others is not null) {
            var moved = new HashSet<string>(collection: WorldDocumentWalker.ChangedRows(before: prefix, after: with), comparer: StringComparer.Ordinal);

            foreach (var (index, before, after) in others) {
                if (WorldDocumentWalker.ChangedRows(before: before, after: after).FirstOrDefault(predicate: moved.Contains) is { } row) {
                    return RefuseUndo(
                        connectionId: connectionId,
                        correlationId: correlationId,
                        refusal: (isOperator
                            ? $"{verb} refused: {row} was changed later by {next[index].Author.Describe()} (journal entry {index}, {next[index].Description}); nothing was {(redo ? "redone" : "undone")}"
                            : $"{verb} refused: {row} was changed later by another principal; nothing was {(redo ? "redone" : "undone")}")
                    );
                }
            }
        }

        var named = $"{(redo ? "redid" : "undid")} {take} ({string.Join(separator: ", ", values: toggled.Select(selector: index => journal[index].Description))})";
        var live = Host.Document.Definition;
        var changed = WorldDocument.DescribeChanged(changed: ToggledEdit(live: live, candidate: candidate, from: (redo ? prefix : with), to: (redo ? with : prefix), restored: out var restored));
        var restoredRows = (IReadOnlyList<string>)[];

        // A step whose toggled entries changed only state values or what presentation reads installs as that edit, the
        // way a reload of the same change does (ReloadInstallKindOf names the kind, TryInstallEdit validates and installs
        // it against the live document), and keeps the session's arena, deals and draws.
        if ((restored is not null) &&
            (WorldDocument.ReloadInstallKindOf(candidate: restored, live: live, rows: out restoredRows) is var restoredKind) &&
            (restoredKind != WorldInstallKind.Full)) {
            if (!Host.Document.TryInstallEdit(candidate: restored, detail: out var detail, kind: restoredKind, principal: principal, reason: out var editReason, rows: restoredRows, validated: false)) {
                return RefuseUndo(connectionId: connectionId, correlationId: correlationId, refusal: $"{verb} refused: {editReason}");
            }
            WorldBootWork.Count(kind: WorldBootWork.UndoNarrowInstalls);
            return AcceptUndo(connectionId: connectionId, correlationId: correlationId, detail: (detail + changed), named: named, journal: next, principal: principal);
        }

        // Every unit the replayed document holds differently from the live one is judged, once.
        if (!WorldDefinitionValidator.TryValidateChange(before: live, after: candidate, compilation: out _, reason: out var invalid)) {
            return RefuseUndo(connectionId: connectionId, correlationId: correlationId, refusal: $"{verb} refused: the replayed document is invalid — {invalid}");
        }

        Host.DrawKeys.AdvanceGeneration();
        if (WorldPrivateDrawKeys.Positions(candidate) is { } positions) {
            if (!WorldDrawBootResolver.TryResolve(candidate, Host.InstanceIdentity, out candidate, out var why, Host.DrawKeys, restore: positions)) {
                return RefuseUndo($"{verb} refused: {why}", connectionId, correlationId);
            }
        }

        // The restored document installs through the one install door, as a load does: its state whole, into an arena
        // prepared before anything changes, its products (render envelope, solid field, addons) built and checked there.
        // It keeps the live field lattices and rule latches, as a mutation does. A step that refuses leaves the live
        // document as it stood.
        if (
            !Host.Document.TryPrepareWholeArena(candidate: candidate, arena: out var arena, reason: out var stageReason, settled: out var restoredDocument) ||
            !Host.Document.TryStageInstall(
                request: new WorldDocument.InstallRequest(Candidate: restoredDocument, Current: live, Kind: WorldInstallKind.Full, Principal: principal,
                    Products: Host.Document.PlanProducts(candidate: candidate, previous: live)) { Arena = arena },
                reason: out stageReason,
                stage: out var stage
            )
        ) {
            return RefuseUndo(connectionId: connectionId, correlationId: correlationId, refusal: $"{verb} refused: {stageReason}");
        }
        Host.Document.CommitInstall(stage: stage);
        // The restored document already carries the deal projection its replay made.
        Host.Tick.PrimeDealProjection();
        return AcceptUndo(connectionId: connectionId, correlationId: correlationId, detail: (", full install" + changed), named: named, journal: next, principal: principal, stage: stage);
    }

    // What a step's toggled entries changed, read from the replay's document at the first of them (`from` for an undo is
    // that document with the toggled entries composed over it, `to` the document without them; a redo swaps the two) and
    // named as the parts the two hold as different instances: the World's own entries are replayed on both sides of the
    // step, so they are no part of what it moves. When the toggled entries changed only state values, or only creations
    // in what shading reads and shade programs, `restored` is the live document with exactly those rows set to the full
    // replay's (`candidate`): every other row stays the live instance, so the session's arena, deals and draws carry over
    // and the install delivers only the rows the step moved. Otherwise `restored` is null and the step installs the
    // replayed document whole.
    private static WorldDocumentInputs ToggledEdit(WorldDefinition live, WorldDefinition candidate, WorldDefinition from, WorldDefinition to, out WorldDefinition? restored) {
        restored = null;

        var changed = WorldDocumentInputs.Changed(after: to, before: from);

        if (WorldDocument.ReloadInstallKindOf(candidate: to, live: from, rows: out var valueRows) is not (WorldInstallKind.Nothing or WorldInstallKind.Presentation or WorldInstallKind.Value)) {
            return changed;
        }

        var creations = live.CreationsRaw;

        if (!ReferenceEquals(objA: from.CreationsRaw, objB: to.CreationsRaw)) {
            if ((live.Creations.Count != to.Creations.Count) || (from.Creations.Count != to.Creations.Count)) { return changed; }

            WorldPrototype[]? rows = null;

            for (var index = 0; (index < to.Creations.Count); index++) {
                var row = to.Creations[index];

                if (ReferenceEquals(objA: row, objB: from.Creations[index])) { continue; }
                if (!live.Creations[index].Id.Equals(row.Id) ||
                    (WorldDefinitionRows.FindCreation(creations: candidate.Creations, id: row.Id.Value) is not { } replayed)) {
                    return changed;
                }
                rows ??= [.. live.Creations];
                rows[index] = replayed;
            }
            creations = (rows ?? creations);
        }

        // ShadesRaw reads the table the definition's uses derive, so it is set only when a program moved: setting it
        // back would replace the stored table the live document carries with another instance.
        restored = (ReferenceEquals(objA: creations, objB: live.CreationsRaw) ? live : (live with { CreationsRaw = creations }));

        var before = from.ShadesRaw;
        var after = to.ShadesRaw;

        if (!ReferenceEquals(objA: before, objB: after)) {
            var held = live.ShadesRaw;
            var replayed = candidate.ShadesRaw;
            Dictionary<string, SdfShadeDefinition>? table = null;

            Dictionary<string, SdfShadeDefinition> Table() => (table ??= ((held is null)
                ? new Dictionary<string, SdfShadeDefinition>(StringComparer.Ordinal)
                : new Dictionary<string, SdfShadeDefinition>(held, StringComparer.Ordinal)));

            foreach (var name in (before?.Keys ?? []).Union(second: (after?.Keys ?? []), comparer: StringComparer.Ordinal)) {
                if (ReferenceEquals(objA: ((before?.TryGetValue(key: name, value: out var was) == true) ? was : null), objB: ((after?.TryGetValue(key: name, value: out var now) == true) ? now : null))) { continue; }
                if (replayed?.TryGetValue(key: name, value: out var restoring) == true) { Table()[name] = restoring; }
                else if (held?.ContainsKey(key: name) == true) { _ = Table().Remove(key: name); }
            }
            if (table is not null) { restored = (restored with { ShadesRaw = (((replayed is null) && (table.Count == 0)) ? null : table) }); }
        }
        if (valueRows.Count > 0) {
            // The live section's rows in the live order, each row the toggled entries moved replaced by the replayed one;
            // WithStateSection keeps the compiled catalog the rows still fit.
            var section = restored.StateRaw;
            var replays = candidate.StateRaw?.World;

            if ((section?.World is not { } liveRows) || (replays is null)) { restored = null; return changed; }

            var moved = new HashSet<string>(collection: valueRows, comparer: StringComparer.Ordinal);
            var rows = new WorldStateRow[liveRows.Count];

            for (var index = 0; (index < rows.Length); index++) {
                var row = liveRows[index];

                if (moved.Contains(item: row.Name.Value)) {
                    if (WorldDefinitionRows.FindStateRow(rows: replays, name: row.Name.Value) is not { } replayed) { restored = null; return changed; }
                    row = replayed;
                }
                rows[index] = row;
            }
            restored = restored.WithStateSection(state: (section with { World = rows }));
        }
        return changed;
    }

    // Keeps the stepped journal, finishes what a full install committed, and answers the submitting line the way a
    // refusal does (RefuseUndo), through the same tap; that answer is the step's one report, so nothing narrates it
    // beside. It names the acting principal's undo and redo depths after the step.
    private bool AcceptUndo(string named, ImmutableList<WorldJournalEntry> journal, Principal principal, string detail, int connectionId, long correlationId, WorldDocument.DocumentStage? stage = null) {
        Host.Document.SetJournal(journal: journal);
        if (stage is not null) { Host.Document.FinishInstall(stage: stage); }

        var (undoable, redoable) = Host.Document.JournalDepths(principal: principal);

        Host.EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: $"{named}, {undoable} left to undo, {redoable} to redo{detail}",
            Rejected: false,
            Kind: WorldEditEchoKind.Mutation,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));

        return true;
    }

    // Every ApplyUndo gate refuses identically: echoed to the same tap a rejected live mutation reaches, whose answer
    // is the refusal's one report, and false to the caller.
    private bool RefuseUndo(string refusal, int connectionId, long correlationId) {
        Host.EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: refusal,
            Rejected: true,
            Kind: WorldEditEchoKind.Mutation,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));

        return false;
    }

    /// <summary>Buffers a journal undo of the acting principal's last <paramref name="count"/> edits, or with
    /// <paramref name="redo"/> a redo of the first <paramref name="count"/> its undos removed, for the next
    /// <see cref="WorldTick.Step"/>. Retains the submitting envelope's connection/correlation identity — see
    /// <see cref="WorldDocument.EnqueueMutation"/>'s own remarks.</summary>
    /// <param name="count">How many of the principal's edits to undo or redo (clamped to at least 1 and at most the
    /// edits it can reach).</param>
    /// <param name="principal">The acting identity, whose edits the step reaches and which it is checked against.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    /// <param name="redo">Whether the step redoes the principal's undone edits.</param>
    internal void EnqueueUndo(int count, Principal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0, bool redo = false) {
        Host.Document.Pending.Enqueue(item: new WorldPendingOp.Undo(
            ConnectionId: connectionId,
            CorrelationId: correlationId,
            Count: count,
            Principal: principal,
            Redo: redo
        ));
    }

    /// <summary>Folds the journal's oldest entries into the base it keeps, once per completed tick: the entries past the
    /// <c>host.journalDepth</c> horizon (an explicit 0 keeps every entry), and the World's own entries ahead of the first
    /// edit a principal authored that the fold leaves, which no undo or redo reaches. A journal that holds no edit keeps
    /// its World entries until an edit arrives or the horizon takes them, so the base, which a history keyframe
    /// serializes, moves once per edit rather than once per World write. An undone entry the fold reaches is discarded
    /// with every later undone entry of its author's (a redo takes them back oldest first, so none stays reachable). The
    /// folded entries compose forward in order, the same compose <see cref="ApplyUndo"/>'s replay runs, so the fold reads
    /// only the entries it folds and a checkpoint captured after it restores to the identical live definition one captured
    /// before it would have.</summary>
    internal void FoldJournal() {
        lock (Host.AuthorityGate) {
            var journal = Host.Document.Journal;
            var depth = Host.Document.Definition.Host.JournalDepth;
            var fold = ((depth > 0) ? Math.Max(val1: 0, val2: (journal.Count - depth)) : 0);
            var firstEdit = ((fold < journal.Count) ? journal.FindIndex(startIndex: fold, match: static entry => entry.Authored) : -1);

            if (firstEdit > fold) { fold = firstEdit; }
            if (fold == 0) { return; }

            var candidate = Host.Tick.ProjectDealsForEdit(Host.Document.Base, previous: null);
            HashSet<Principal>? discarded = null;

            for (var index = 0; (index < fold); index++) {
                var entry = journal[index];

                if (entry.Undone) {
                    (discarded ??= []).Add(item: entry.Author);
                    continue;
                }
                // Every entry here already applied live once, against this exact base-and-prefix, so recomposing it
                // is expected to succeed; if it somehow does not, leave the journal exactly as it stood rather than
                // fold onto a candidate that failed to build.
                if (!entry.TryCompose(Host, candidate, out var next, out _)) {
                    return;
                }

                candidate = Host.Tick.ProjectDealsForEdit(next, previous: candidate);
            }

            var kept = journal.RemoveRange(index: 0, count: fold);

            if (discarded is not null) { kept = kept.RemoveAll(match: entry => (entry.Undone && discarded.Contains(item: entry.Author))); }
            Host.Document.AdoptBase(
                definition: candidate,
                origin: new WorldBaseOrigin(
                    Depth: depth,
                    Kind: WorldBaseOriginKind.JournalFold
                )
            );
            Host.Document.SetJournal(journal: kept);
        }
    }
}
