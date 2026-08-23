using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Adjacency overlap proofs depend on motion envelopes, every kit's collider, and interaction/targeting reach.
    // Those edits need a fresh neighbour proof at the load boundary.
    private static bool AdjacencyProofInputsChanged(WorldDefinition current, WorldDefinition candidate, WorldMutation mutation) => mutation switch {
        WorldMutation.SetMotion => (current.Motion != candidate.Motion),
        WorldMutation.UpsertKit or WorldMutation.RemoveKit => !current.Kits.SequenceEqual(second: candidate.Kits),
        WorldMutation.UpsertInteraction or WorldMutation.RemoveInteraction => (current.Interactions != candidate.Interactions),
        _ => false,
    };
    // Whether a mutation recompiles the population's fixed-point derived state (kit table, kit indices, live bodies'
    // compiled tuning/actions, AND the analytic collider set). A screen/collision edit rebuilds the collider set so a
    // live screens or collision change takes effect on the next tick with no restart.
    private static bool AffectsPopulation(WorldMutation mutation) => (mutation is
        WorldMutation.UpsertKit or WorldMutation.RemoveKit or WorldMutation.SetDefaultSeatKit or
        WorldMutation.SetKitAssignment or WorldMutation.SetMotion or WorldMutation.SetSpawns or
        WorldMutation.SetCollision or
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen or
        // The LOOK mutations re-resolve the population's look table (PRESENTATION-ONLY, but Rebuild is the one path that
        // re-runs ResolveLookIndices and bumps the client's program-rebuild revision).
        WorldMutation.UpsertLook or WorldMutation.RemoveLook or WorldMutation.SetLookAssignment or
        // SetPopulationDefaults carries the distribution and variation rows: Rebuild recompiles their fixed spawn
        // policy so it is LIVE for future activations (the live census count still stays the world.population verb — this
        // Rebuild re-seeds SpawnPosition but never re-activates or teleports a standing body).
        WorldMutation.SetPopulationDefaults or
        // A placement row can change the census (Arc 7's Inhabit facet: a placement contributes driven bodies), and an
        // inhabited row's kit resolution reads the creation's Locomotion, so a creation swap can move a body between
        // kits — all must trigger Rebuild + ReconcileInhabitants. (R13: the third and last edit to this switch.)
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement or
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation);
    // Whether a mutation touches the addons section — the only door WorldAddonRow row content (or document order)
    // moves through OUTSIDE a whole-document rebuild (ApplyRebuild carries its own unconditional prepare, which
    // also covers a channel-table change by restaging the whole host), so a per-row structural diff gated on JUST
    // these two kinds is the whole trigger a live mutation needs — see IWorldAddonHost.TryPrepare's own remarks.
    private static bool AffectsAddons(WorldMutation mutation) => (mutation is
        WorldMutation.UpsertAddon or WorldMutation.RemoveAddon);
    // Whether a mutation can grow the SDF program past the probed render envelope (screen slabs / creation stamps — an
    // UpsertCreation re-shapes every live placement of it, so it measures too).
    private static bool AffectsRenderEnvelope(WorldMutation mutation) => (mutation is
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen or
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation or
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement or
        // A creation look will change the emitted program word count (a body worn as a stamp) once creation-look
        // rendering lands (Arc 7); catalog looks add zero words today, so this arm is honest groundwork — all three look
        // mutations already ride the envelope gate so the loud capacity rejection will fire at apply time, not at a later
        // GPU allocation, the moment creation stamps render.
        WorldMutation.UpsertLook or WorldMutation.RemoveLook or WorldMutation.SetLookAssignment);
    // Whether a mutation can change the SDF contact field: the collision tuning and every solid-bearing section
    // (screens, creations that reshape a stamp, placements). Coarse by section,
    // matching AffectsPopulation/AffectsRenderEnvelope.
    private static bool AffectsSolidField(WorldMutation mutation) => (mutation is
        WorldMutation.SetCollision or
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen or
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation or
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement);
    private static bool ContainsMember(IReadOnlyList<WorldPrincipal> members, WorldPrincipal member) {
        foreach (var existing in members) {
            if (existing == member) {
                return true;
            }
        }

        return false;
    }
    // The boot-frozen derived-face reservation gate, shared by the mutation and rebuild apply paths so the two can
    // never disagree about what the binder can actually show.
    private bool ExceedsBootDerivedFaceReservation(WorldDefinition candidate, out string reason) {
        if (candidate.Authoring.DerivedFaceScreens <= BootDerivedFaceScreens) {
            reason = string.Empty;

            return false;
        }

        reason = $"authoring.derivedFaceScreens {candidate.Authoring.DerivedFaceScreens} exceeds the boot-reserved {BootDerivedFaceScreens} derived-face screen slot(s); the binder registers that band once at boot and the render provider key set is frozen there, so restart the host to load a wider one";

        return true;
    }
    private static WorldGroupKind? FindGroupKind(IReadOnlyList<WorldGroupKind> kinds, string name) {
        foreach (var kind in kinds) {
            if (string.Equals(
                a: kind.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return kind;
            }
        }

        return null;
    }
    private static WorldGroup? FindGroupRow(IReadOnlyList<WorldGroup> groups, string id) {
        foreach (var row in groups) {
            if (string.Equals(
                a: row.Id,
                b: id,
                comparisonType: StringComparison.Ordinal
            )) {
                return row;
            }
        }

        return null;
    }
    // The HUD element mutations' panel lookup — a single-element read-modify-write needs its OWNING panel by id
    // before it can rewrite that panel's Elements list; null when no panel declares that id.
    private static WorldHudPanel? FindHudPanel(IReadOnlyList<WorldHudPanel> panels, string id) {
        foreach (var panel in panels) {
            if (string.Equals(
                a: panel.Id,
                b: id,
                comparisonType: StringComparison.Ordinal
            )) {
                return panel;
            }
        }

        return null;
    }
    // The Ownership section's own find — keyed by the Subject value (a readonly record struct, so structural
    // equality is exact) rather than a name string, since a subject is (kind, id) rather than one bare identifier.
    private static WorldOwnership? FindOwnershipRow(IReadOnlyList<WorldOwnership> ownership, OwnershipSubject subject) {
        foreach (var row in ownership) {
            if (row.Subject == subject) {
                return row;
            }
        }

        return null;
    }
    // Whether a mutation is DOCUMENT-DEFAULTS class (edits the next boot's wake state; live session levers own "now").
    // Everything else, cameras included, applies live on delivery.
    private static bool IsDocumentDefaults(WorldMutation mutation) => (mutation is
        WorldMutation.SetRenderDefaults or WorldMutation.SetPopulationDefaults or WorldMutation.SetHostDefaults);
    // An EXPLICIT write to an advancing cell — a whole-row UpsertStateRow (which re-bases the row's OWN slot advance
    // AND every keyed cell's own advance, since it re-declares the whole row), an UpsertStateCell (which re-bases
    // ONLY the one cell it names — the row's slot advance when that cell IS the slot key, or that cell's own advance
    // otherwise), or a market mutation (which re-bases every (row, key) cell it actually wrote through
    // WriteMarketCell — see MarketCellTouches) — re-bases WorldStateAdvance.EpochTick to `tick`, unconditionally
    // overwriting whatever epoch the write's own payload carried (see WorldStateAdvance's remarks). A market write
    // that skipped this would let a cell's elapsed accrual apply a second time on its very next read: WriteMarketCell
    // preserves the pre-write Advance record verbatim (it is a value move, never a re-mint), so the base it installs
    // already has that accrual baked in — an un-rebased epoch would let the same elapsed span compute again from the
    // old epoch against the new base. Runs AFTER TryCompose so it sees the row/cell TryCompose just installed, and
    // BEFORE validation/journal so a rebased epoch is what gets journaled, replayed by world.undo, and read back.
    // `original` is the document the mutation composed against (before this mutation applied) — market's own touches
    // need it to resolve a listing's pre-write state (its standing bidder, in particular) since `candidate` already
    // reflects the write. A no-op for every other mutation kind, and for a cell (row-level or per-cell) that carries
    // no advance trait at all.
    private static WorldDefinition RebaseAdvanceEpoch(WorldDefinition original, WorldDefinition candidate, WorldMutation mutation, ulong tick) {
        if (MarketCellTouches(
            mutation: mutation,
            original: original
        ) is { } touches) {
            var touchedState = candidate.State;

            foreach (var touch in touches) {
                touchedState = RebaseKeyedCellAdvanceEpoch(
                    rows: touchedState,
                    rowName: touch.Row,
                    key: touch.Key,
                    tick: tick
                );
            }

            return (ReferenceEquals(
                objA: touchedState,
                objB: candidate.State
            )
                ? candidate
                : candidate.WithWorldState(rows: touchedState)
            );
        }

        string? rowName;
        string? cellKey; // null on a whole-row write (every advancing cell re-bases); the named key on a per-cell write.

        switch (mutation) {
            case WorldMutation.UpsertStateRow m:
                rowName = m.Row.Name.Value;
                cellKey = null;
                break;
            case WorldMutation.UpsertStateCell m:
                rowName = m.Row;
                cellKey = m.Key;
                break;
            default:
                return candidate;
        }

        if (WorldDefinitionRows.FindStateRow(
            rows: candidate.State,
            name: rowName
        ) is not { } row) {
            return candidate;
        }

        var epoch = unchecked((long)tick);
        var rebasedRow = row;

        if (
            ((cellKey is null) || string.Equals(
            a: cellKey,
            b: WorldStateRow.SlotKey,
            comparisonType: StringComparison.Ordinal
        )) &&
            (row.Advance is { } rowAdvance)
        ) {
            rebasedRow = (rebasedRow with { Advance = (rowAdvance with { EpochTick = epoch }) });
        }

        var cells = (rebasedRow.Cells ?? []);
        List<WorldStateCell>? rebasedCells = null;

        for (var index = 0; (index < cells.Count); index++) {
            var cell = cells[index];

            if (
                (cell.Advance is not { } cellAdvance) ||
                ((cellKey is not null) && !string.Equals(
                a: cell.Key.Value,
                b: cellKey,
                comparisonType: StringComparison.Ordinal
            ))
            ) {
                continue;
            }

            rebasedCells ??= new List<WorldStateCell>(collection: cells);
            rebasedCells[index] = (cell with { Advance = (cellAdvance with { EpochTick = epoch }) });
        }

        if (rebasedCells is not null) {
            rebasedRow = (rebasedRow with { Cells = rebasedCells });
        }

        return (ReferenceEquals(
            objA: rebasedRow,
            objB: row
        )
            ? candidate
            : candidate.WithWorldState(rows: Upsert(
                list: candidate.State,
                item: rebasedRow,
                keyOf: static (WorldStateRow r) => r.Name
            ))
        );
    }
    // Rebases one keyed cell's WorldStateAdvance.EpochTick to `tick` — the same rebase RebaseAdvanceEpoch's own
    // UpsertStateCell arm performs on a single named cell, factored out so a market write (which may touch several
    // cells across two rows in one mutation) can apply it per touch without duplicating the clamp-free with-expression
    // rebuild. A no-op for a cell that carries no advance trait, or a row/key MarketCellTouches named that this
    // document does not (or no longer) declare.
    private static IReadOnlyList<WorldStateRow> RebaseKeyedCellAdvanceEpoch(IReadOnlyList<WorldStateRow> rows, WorldCellName rowName, string key, ulong tick) {
        if (WorldDefinitionRows.FindStateRow(
            name: rowName,
            rows: rows
        ) is not { } row) {
            return rows;
        }

        var cellKey = WorldCellName.Parse(candidate: key);
        var cells = (row.Cells ?? []);

        for (var index = 0; (index < cells.Count); index++) {
            var cell = cells[index];

            if (
                (cell.Key != cellKey) ||
                (cell.Advance is not { } advance)
            ) {
                continue;
            }

            var rebasedCells = new List<WorldStateCell>(collection: cells);

            rebasedCells[index] = (cell with { Advance = (advance with { EpochTick = unchecked((long)tick) }) });

            return Upsert(
                list: rows,
                item: (row with { Cells = rebasedCells }),
                keyOf: static (WorldStateRow r) => r.Name
            );
        }

        return rows;
    }
    // Drop the first row whose key matches — reports whether a row was actually removed.
    private static bool Remove<T, TKey>(IReadOnlyList<T> list, TKey key, Func<T, TKey> keyOf, out IReadOnlyList<T> result) {
        var kept = new List<T>(capacity: list.Count);
        var removed = false;

        foreach (var existing in list) {
            if (
                !removed &&
                EqualityComparer<TKey>.Default.Equals(
                x: keyOf(arg: existing),
                y: key
            )
            ) {
                removed = true;

                continue;
            }

            kept.Add(item: existing);
        }

        result = kept;

        return removed;
    }
    // The shared LeaveGroup/KickMember(Remove) tail: drop the one member's row, then dissolve the WHOLE group when
    // that empties it and the kind's Lifetime is Ephemeral — checked ONLY when the group HAD at least one member
    // before (forming an empty group never auto-dissolves it). A null kind (defensive — the validator refuses a
    // dangling kindName before this could be reached live) leaves the group Persistent by default.
    private static IReadOnlyList<WorldGroup> RemoveMemberAndMaybeDissolve(IReadOnlyList<WorldGroup> groups, WorldGroup group, WorldGroupKind? kind, WorldPrincipal member) {
        var remaining = new List<WorldPrincipal>(capacity: group.Members.Count);

        foreach (var existing in group.Members) {
            if (existing != member) {
                remaining.Add(item: existing);
            }
        }

        if (
            (remaining.Count == 0) &&
            (group.Members.Count > 0) &&
            (kind?.Lifetime == WorldGroupLifetime.Ephemeral)
        ) {
            _ = Remove(
                key: group.Id,
                keyOf: static (WorldGroup row) => row.Id,
                list: groups,
                result: out var dissolved
            );

            return dissolved;
        }

        return Upsert(
            list: groups,
            item: (group with { Members = remaining }),
            keyOf: static (WorldGroup row) => row.Id
        );
    }
    // Replaces the row naming the SAME subject as `row` — the coarse whole-row upsert every other section rides,
    // specialized because OwnershipSubject (not a bare name) is the key.
    private static IReadOnlyList<WorldOwnership> ReplaceOwnership(IReadOnlyList<WorldOwnership> ownership, WorldOwnership row) =>
        Upsert(
            item: row,
            keyOf: static (WorldOwnership o) => o.Subject,
            list: ownership
        );
    // The row-scoped Edit subject a state-row or state-cell mutation names, for the second authority check — null
    // for every other mutation kind (the check above is a no-op then). Both the whole-row upsert/remove AND the
    // per-cell upsert/remove check the SAME Edit/state:<name> subject now — a slot is a table with one key, so there
    // is one row, one subject, never a separate table:<name> narrowing independent of the whole row's own hold.
    private static GrantSubject? RowScopedEditSubjectOf(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertStateRow m => GrantSubject.State(name: m.Row.Name),
        WorldMutation.RemoveStateRow m => GrantSubject.State(name: m.Name),
        WorldMutation.UpsertStateCell m => GrantSubject.State(name: m.Row),
        WorldMutation.RemoveStateCell m => GrantSubject.State(name: m.Row),
        // The row this mutation WRITES — the identical subject an UpsertStateCell into the same row is checked
        // against, which is what makes `verbs:Generate` on an Edit/state:<row> hold the fire-without-redefine
        // separation. Advancing the GENERATOR row's own cursor is engine bookkeeping intrinsic to firing; the
        // interesting authority over a generator is re-authoring it, which is an UpsertStateRow against ITS row.
        WorldMutation.Generate m => GrantSubject.State(name: m.Row),
        _ => null,
    };
    // The row-scoped Mutate subject a creations/placements mutation names, for gate 1's disjunction — null for every
    // other mutation kind (the section hold is then the only way through). The id is the mutation's own target key,
    // the same key the compose arm upserts/removes by, so a row grant admits exactly the row it names.
    private static GrantSubject? RowScopedMutateSubjectOf(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertCreation m => GrantSubject.Creation(id: m.Creation.Id.Value),
        WorldMutation.RemoveCreation m => GrantSubject.Creation(id: m.Id),
        WorldMutation.UpsertPlacement m => GrantSubject.Placement(id: m.Placement.Id),
        WorldMutation.RemovePlacement m => GrantSubject.Placement(id: m.Id),
        _ => null,
    };
    // The world-document section a mutation targets — the Mutate-capability subject it is checked against. One section
    // per mutation kind (coarse, section-keyed — a genre world adds sections + kinds, never changes this mapping).
    private static WorldSection SectionOf(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertKit or WorldMutation.RemoveKit or WorldMutation.SetDefaultSeatKit or WorldMutation.SetKitAssignment => WorldSection.Kits,
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen => WorldSection.Screens,
        WorldMutation.UpsertCamera or WorldMutation.RemoveCamera => WorldSection.Cameras,
        WorldMutation.SetSpawns => WorldSection.Spawns,
        WorldMutation.SetMotion => WorldSection.Motion,
        WorldMutation.SetPopulationDefaults => WorldSection.Population,
        WorldMutation.SetRenderDefaults => WorldSection.Render,
        WorldMutation.UpsertAddon or WorldMutation.RemoveAddon => WorldSection.Addons,
        WorldMutation.UpsertBindingOverlay or WorldMutation.RemoveBindingOverlay => WorldSection.Bindings,
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation => WorldSection.Creations,
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement => WorldSection.Placements,
        WorldMutation.SetAuthoringDefaults => WorldSection.Authoring,
        WorldMutation.UpsertSpeaker or WorldMutation.RemoveSpeaker => WorldSection.Speakers,
        WorldMutation.UpsertTune or WorldMutation.RemoveTune => WorldSection.Tunes,
        WorldMutation.UpsertPatch or WorldMutation.RemovePatch => WorldSection.Patches,
        WorldMutation.SetAudioDefaults => WorldSection.Audio,
        WorldMutation.SetCollision => WorldSection.Collision,
        WorldMutation.SetHostDefaults => WorldSection.Host,
        WorldMutation.SetViewDefaults or WorldMutation.UpsertViewLayout or WorldMutation.RemoveViewLayout => WorldSection.Views,
        WorldMutation.SetPlayerDefaults => WorldSection.PlayerDefaults,
        WorldMutation.UpsertLook or WorldMutation.RemoveLook or WorldMutation.SetLookAssignment => WorldSection.Looks,
        WorldMutation.UpsertGrant or WorldMutation.RemoveGrant => WorldSection.Grants,
        WorldMutation.UpsertHudPanel or WorldMutation.RemoveHudPanel or WorldMutation.UpsertHudElement or WorldMutation.RemoveHudElement or WorldMutation.SetHudDefaults => WorldSection.Hud,
        // Generate's OBSERVABLE effect is a state write, so it shares the state section's coarse hold; its narrower
        // authority is the SAME row-scoped Edit/state:<row> hold every other state write takes, never a second
        // section.
        WorldMutation.UpsertStateRow or WorldMutation.RemoveStateRow or WorldMutation.UpsertStateCell or WorldMutation.RemoveStateCell or WorldMutation.Generate => WorldSection.State,
        WorldMutation.SetInputHold => WorldSection.InputHold,
        WorldMutation.UpsertWorldRule or WorldMutation.RemoveWorldRule => WorldSection.Rules,
        WorldMutation.UpsertGroupKind or WorldMutation.RemoveGroupKind or WorldMutation.FormGroup or WorldMutation.JoinGroup or WorldMutation.LeaveGroup or WorldMutation.KickMember
            or WorldMutation.OfferOwnership or WorldMutation.SettleOwnership => WorldSection.Groups,
        WorldMutation.SetProperty => WorldSection.Properties,
        WorldMutation.UpsertInteraction or WorldMutation.RemoveInteraction => WorldSection.Interactions,
        WorldMutation.CreateMarketListing or WorldMutation.PlaceMarketBid or WorldMutation.BuyoutMarketListing or WorldMutation.CancelMarketListing or WorldMutation.SettleMarketListing or WorldMutation.PruneMarketListings => WorldSection.Market,
        // No silent fallback: a new mutation kind added without its own arm would otherwise inherit Kits authority. A
        // missing arm throws the first time that kind is mapped — surfaced loudly at runtime rather than mis-authorized.
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(mutation),
        actualValue: mutation,
        message: $"no WorldSection arm for mutation kind '{mutation.GetType().Name}' — every kind must map to its authorizing section."
    ),
    };
    /// <summary>Owns canonical document validation and authored-hash matching at the mutation composition boundary.</summary>
    // `hash` is the row's AUTHORED hash lane (a creation's HashRaw, a tune/patch's stored Hash) — never a computed
    // property, whose canonicalize-on-read would throw on a hostile document in the caller's own argument list,
    // before this boundary's refusal could run. Null means the submitter carried none: the canonical hash is adopted
    // (an absent hash is trivially self-consistent — WorldCreation.Hash's own rule), while a CARRIED hash must equal
    // the one this pipeline computes.
    private static bool TryCanonicalizeDocument<TDocument>(
        TDocument document,
        string id,
        string? hash,
        string kind,
        Func<TDocument, string, Puck.Assets.Documents.CanonicalDocument<TDocument>> canonicalize,
        out TDocument canonicalDocument,
        out string reason) {
        Puck.Assets.Documents.CanonicalDocument<TDocument> canonical;

        try {
            canonical = canonicalize(
                arg1: document,
                arg2: id
            );
        } catch (Exception exception) when (exception is Puck.Assets.Documents.DocumentValidationException or InvalidOperationException) {
            // The canonicalizer refuses a malformed document with DocumentValidationException; an unresolved
            // `state.` reference inside the document surfaces as InvalidOperationException from the value's own
            // read. Both are the submitter's document being inadmissible at this boundary — a loud refusal, never a
            // tick-killing throw (this arm decides submissions from remote travelers and peers).
            canonicalDocument = document;
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }

        if (
            (hash is not null) &&
            !string.Equals(
            a: hash,
            b: canonical.Hash,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            canonicalDocument = document;
            reason = $"{kind} '{id}' hash '{hash}' does not match the canonical sha256 '{canonical.Hash}' — a hash must come from the canonicalize pipeline";

            return false;
        }

        canonicalDocument = canonical.Document;
        reason = string.Empty;

        return true;
    }
    // Compose a candidate definition from the current one and a mutation — a with-expression over the coarse section,
    // whole-row upsert addressed by stable id. A remove of a missing id fails here (before validation) with a reason.
    // `tick` is the tick this mutation APPLIES at — the live tick boundary, or a journal entry's own tick during
    // world.undo's replay. The state-cell arm reads it (an Add against an advancing row resolves its target LIVE);
    // OfferOwnership/SettleOwnership read it too (a deadline is checked against the SAME tick the offer/reclaim
    // applies at, never a wall clock — see their own remarks); it is threaded rather than defaulted so a caller can
    // never silently compose against tick zero. `evictedKey` is non-null only when an UpsertStateCell write against an
    // Evicts row dropped its oldest cell to make room — the same pure function every re-composition (live apply,
    // world.undo's journal replay) runs, so the reported victim and the actually-dropped cell can never disagree.
    private static bool TryCompose(WorldDefinition current, WorldMutation mutation, ulong tick, string instanceIdentity, out WorldDefinition candidate, out string reason, out WorldCellName? evictedKey) {
        if (!TryComposeCore(
            candidate: out candidate,
            current: current,
            evictedKey: out evictedKey,
            instanceIdentity: instanceIdentity,
            mutation: mutation,
            reason: out reason,
            tick: tick
        )) {
            return false;
        }

        var stateRow = StateRowOf(mutation: mutation);

        return (
            (stateRow is null) ||
            WorldStateDocumentValues.TryRefresh(
                definition: candidate,
                rowName: stateRow,
                refreshed: out candidate,
                reason: out reason
            )
        );
    }
    // The state row a state mutation writes, or null for every other kind — the row whose bound document values
    // TryRefresh re-resolves.
    private static string? StateRowOf(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertStateRow value => value.Row.Name.Value,
        WorldMutation.RemoveStateRow value => value.Name,
        WorldMutation.UpsertStateCell value => value.Row,
        WorldMutation.RemoveStateCell value => value.Row,
        _ => null,
    };
    // A state write re-resolves every document value bound to its row; when one of those values is a
    // look-assignment row name, the population's look indices are derived from it and must re-resolve too —
    // KEEP IN SYNC with AffectsPopulation, which lists the mutation KINDS that rebuild.
    private static bool RefreshesLookAssignment(WorldMutation mutation, WorldDefinition candidate) => (
        (StateRowOf(mutation: mutation) is { } row) &&
        WorldStateDocumentValues.ReferencesRow(
            definition: candidate,
            graph: candidate.LookAssignment,
            rowName: row
        )
    );
    private static bool TryComposeCore(WorldDefinition current, WorldMutation mutation, ulong tick, string instanceIdentity, out WorldDefinition candidate, out string reason, out WorldCellName? evictedKey) {
        reason = string.Empty;
        evictedKey = null;

        switch (mutation) {
            case WorldMutation.UpsertKit m:
                candidate = (current with {
                    KitsRaw = Upsert(
                    list: current.Kits,
                    item: m.Kit,
                    keyOf: static kit => kit.Name
                ),
                });

                return true;
            case WorldMutation.RemoveKit m:
                if (!Remove(
                    list: current.Kits,
                    key: m.Name,
                    keyOf: static kit => kit.Name,
                    result: out var kits
                )) {
                    candidate = current;
                    reason = $"no kit row named '{m.Name}'";

                    return false;
                }

                candidate = (current with { KitsRaw = kits });

                return true;
            case WorldMutation.SetDefaultSeatKit m:
                candidate = (current with { DefaultSeatKitRaw = m.Name });

                return true;
            case WorldMutation.SetKitAssignment m:
                candidate = (current with { AssignmentRaw = m.Assignment });

                return true;
            case WorldMutation.UpsertScreen m:
                candidate = (current with {
                    ScreensRaw = Upsert(
                    list: current.Screens,
                    item: m.Screen,
                    keyOf: static screen => screen.Index
                ),
                });

                return true;
            case WorldMutation.RemoveScreen m:
                if (!Remove(
                    list: current.Screens,
                    key: m.Index,
                    keyOf: static screen => screen.Index,
                    result: out var screens
                )) {
                    candidate = current;
                    reason = $"no screen at index {m.Index}";

                    return false;
                }

                candidate = (current with { ScreensRaw = screens });

                return true;
            case WorldMutation.UpsertCamera m:
                candidate = (current with {
                    CamerasRaw = Upsert(
                    list: current.Cameras,
                    item: m.Camera,
                    keyOf: static camera => camera.Name
                ),
                });

                return true;
            case WorldMutation.RemoveCamera m:
                if (!Remove(
                    list: current.Cameras,
                    key: m.Name,
                    keyOf: static camera => camera.Name,
                    result: out var cameras
                )) {
                    candidate = current;
                    reason = $"no camera named '{m.Name}'";

                    return false;
                }

                candidate = (current with { CamerasRaw = cameras });

                return true;
            case WorldMutation.SetSpawns m:
                candidate = (current with { SpawnPointsRaw = m.Spawns });

                return true;
            case WorldMutation.SetMotion m:
                candidate = (current with { MotionRaw = m.Motion });

                return true;
            case WorldMutation.SetPopulationDefaults m:
                candidate = (current with { PopulationRaw = m.Population });

                return true;
            case WorldMutation.SetRenderDefaults m:
                candidate = (current with { RenderRaw = m.Render });

                return true;
            case WorldMutation.UpsertAddon m:
                candidate = (current with {
                    AddonsRaw = Upsert(
                    list: current.Addons,
                    item: m.Addon,
                    keyOf: static addon => addon.Name
                ),
                });

                return true;
            case WorldMutation.RemoveAddon m:
                if (!Remove(
                    list: current.Addons,
                    key: m.Name,
                    keyOf: static addon => addon.Name,
                    result: out var addons
                )) {
                    candidate = current;
                    reason = $"no addon named '{m.Name}'";

                    return false;
                }

                candidate = (current with { AddonsRaw = addons });

                return true;
            case WorldMutation.UpsertCreation m: {
                    if (!TryCanonicalizeDocument(
                        document: m.Creation.Document,
                        id: m.Creation.Id,
                        hash: m.Creation.HashRaw,
                        kind: "creation",
                        canonicalize: static (document, source) => Puck.Forge.Authoring.CreationCanonicalizer.Canonicalize(
                            document: document,
                            source: source
                        ),
                        canonicalDocument: out var canonicalDocument,
                        reason: out reason
                    )) {
                        candidate = current;

                        return false;
                    }

                    candidate = (current with {
                        CreationsRaw = Upsert(
                        list: current.Creations,
                        item: (m.Creation with { Document = canonicalDocument }),
                        keyOf: static creation => creation.Id.Value
                    ),
                    });

                    return true;
                }
            case WorldMutation.RemoveCreation m: {
                    // The conservative no-cascade ruling: a creation with live placements rejects loudly rather than
                    // silently unstamping the world (remove the placements first; undo replay stays order-honest).
                    var referencing = 0;

                    foreach (var placement in current.Placements) {
                        if (string.Equals(
                            a: placement.CreationId,
                            b: m.Id,
                            comparisonType: StringComparison.Ordinal
                        )) {
                            referencing++;
                        }
                    }

                    if (referencing > 0) {
                        candidate = current;
                        reason = $"creation '{m.Id}' has {referencing} live placement(s) — remove them first";

                        return false;
                    }

                    if (!Remove(
                        list: current.Creations,
                        key: m.Id,
                        keyOf: static creation => creation.Id.Value,
                        result: out var creations
                    )) {
                        candidate = current;
                        reason = $"no creation with id '{m.Id}'";

                        return false;
                    }

                    candidate = (current with { CreationsRaw = creations });

                    return true;
                }
            case WorldMutation.UpsertPlacement m:
                return TryComposeUpsertPlacement(
                    candidate: out candidate,
                    current: current,
                    mutation: m,
                    reason: out reason
                );
            case WorldMutation.RemovePlacement m: {
                    // The no-cascade guard: a placement a speaker anchors to rejects loudly naming the dependents, never
                    // silently unanchoring the speaker (full-document revalidation would also catch the dangling anchor,
                    // but the guard names WHO depends rather than echoing a validator path).
                    if (DescribeSpeakersAnchoredTo(
                        speakers: current.Speakers,
                        placementId: m.Id
                    ) is { } anchored) {
                        candidate = current;
                        reason = $"placement '{m.Id}' anchors speaker(s) {anchored} — remove or re-anchor them first";

                        return false;
                    }

                    if (!Remove(
                        list: current.Placements,
                        key: m.Id,
                        keyOf: static placement => placement.Id,
                        result: out var placements
                    )) {
                        candidate = current;
                        reason = $"no placement with id '{m.Id}'";

                        return false;
                    }

                    candidate = (current with { PlacementsRaw = placements });

                    return true;
                }
            case WorldMutation.UpsertSpeaker m:
                candidate = (current with {
                    SpeakersRaw = Upsert(
                    list: current.Speakers,
                    item: m.Speaker,
                    keyOf: static speaker => speaker.Name
                ),
                });

                return true;
            case WorldMutation.RemoveSpeaker m:
                if (!Remove(
                    list: current.Speakers,
                    key: m.Name,
                    keyOf: static speaker => speaker.Name,
                    result: out var speakers
                )) {
                    candidate = current;
                    reason = $"no speaker named '{m.Name}'";

                    return false;
                }

                candidate = (current with { SpeakersRaw = speakers });

                return true;
            case WorldMutation.UpsertTune m: {
                    if (!TryCanonicalizeDocument(
                        document: m.Tune.Document,
                        id: m.Tune.Id,
                        hash: m.Tune.Hash,
                        kind: "tune",
                        canonicalize: static (document, source) => Puck.Forge.Authoring.AudioCanonicalizer.Canonicalize(
                            document: document,
                            source: source
                        ),
                        canonicalDocument: out var canonicalDocument,
                        reason: out reason
                    )) {
                        candidate = current;

                        return false;
                    }

                    candidate = (current with {
                        TunesRaw = Upsert(
                        list: current.Tunes,
                        item: (m.Tune with { Document = canonicalDocument }),
                        keyOf: static tune => tune.Id
                    ),
                    });

                    return true;
                }
            case WorldMutation.RemoveTune m: {
                    if (DescribeSpeakersSourcing(
                        speakers: current.Speakers,
                        matches: source => ((source is WorldSpeakerSource.Tune tune) && string.Equals(
                            a: tune.TuneId,
                            b: m.Id,
                            comparisonType: StringComparison.Ordinal
                        ))
                    ) is { } dependents) {
                        candidate = current;
                        reason = $"tune '{m.Id}' feeds speaker(s) {dependents} — remove or re-source them first";

                        return false;
                    }

                    if (!Remove(
                        list: current.Tunes,
                        key: m.Id,
                        keyOf: static tune => tune.Id,
                        result: out var tunes
                    )) {
                        candidate = current;
                        reason = $"no tune with id '{m.Id}'";

                        return false;
                    }

                    candidate = (current with { TunesRaw = tunes });

                    return true;
                }
            case WorldMutation.UpsertPatch m: {
                    if (!TryCanonicalizeDocument(
                        document: m.Patch.Document,
                        id: m.Patch.Id,
                        hash: m.Patch.Hash,
                        kind: "patch",
                        canonicalize: static (document, source) => Puck.Forge.Authoring.SynthPatchCanonicalizer.Canonicalize(
                            document: document,
                            source: source
                        ),
                        canonicalDocument: out var canonicalDocument,
                        reason: out reason
                    )) {
                        candidate = current;

                        return false;
                    }

                    candidate = (current with {
                        PatchesRaw = Upsert(
                        list: current.Patches,
                        item: (m.Patch with { Document = canonicalDocument }),
                        keyOf: static patch => patch.Id
                    ),
                    });

                    return true;
                }
            case WorldMutation.RemovePatch m: {
                    if (DescribePatchDependents(
                        current: current,
                        patchId: m.Id
                    ) is { } dependents) {
                        candidate = current;
                        reason = $"patch '{m.Id}' is referenced by {dependents} — remove or re-source them first";

                        return false;
                    }

                    if (!Remove(
                        list: current.Patches,
                        key: m.Id,
                        keyOf: static patch => patch.Id,
                        result: out var patches
                    )) {
                        candidate = current;
                        reason = $"no patch with id '{m.Id}'";

                        return false;
                    }

                    candidate = (current with { PatchesRaw = patches });

                    return true;
                }
            case WorldMutation.SetAudioDefaults m:
                candidate = (current with { AudioRaw = m.Audio });

                return true;
            case WorldMutation.UpsertBindingOverlay m:
                candidate = (current with {
                    BindingOverlaysRaw = Upsert(
                    list: current.BindingOverlays,
                    item: m.Overlay,
                    keyOf: static overlay => overlay.Id
                ),
                });

                return true;
            case WorldMutation.SetAuthoringDefaults m:
                candidate = (current with { AuthoringRaw = m.Authoring });

                return true;
            case WorldMutation.SetCollision m:
                candidate = (current with { CollisionRaw = m.Collision });

                return true;
            case WorldMutation.SetHostDefaults m:
                candidate = (current with { HostRaw = m.Host });

                return true;
            case WorldMutation.SetViewDefaults m:
                candidate = (current with { ViewsRaw = m.Views });

                return true;
            case WorldMutation.SetPlayerDefaults m:
                candidate = (current with { PlayerDefaultsRaw = m.Defaults });

                return true;
            case WorldMutation.UpsertViewLayout m: {
                    var views = current.Views;

                    candidate = (current with {
                        ViewsRaw = (views with {
                            Layouts = Upsert(
                        list: views.Layouts,
                        item: m.Layout,
                        keyOf: static layout => layout.Name
                    ),
                        }),
                    });

                    return true;
                }
            case WorldMutation.RemoveViewLayout m: {
                    var views = current.Views;

                    if (!Remove(
                        list: views.Layouts,
                        key: m.Name,
                        keyOf: static layout => layout.Name,
                        result: out var layouts
                    )) {
                        candidate = current;
                        reason = $"no view layout named '{m.Name}'";

                        return false;
                    }

                    candidate = (current with { ViewsRaw = (views with { Layouts = layouts }) });

                    return true;
                }
            case WorldMutation.RemoveBindingOverlay m:
                if (!Remove(
                    list: current.BindingOverlays,
                    key: m.Id,
                    keyOf: static overlay => overlay.Id,
                    result: out var overlays
                )) {
                    candidate = current;
                    reason = $"no binding overlay with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { BindingOverlaysRaw = overlays });

                return true;
            case WorldMutation.UpsertLook m:
                candidate = (current with {
                    LooksRaw = Upsert(
                    list: current.Looks,
                    item: m.Look,
                    keyOf: static look => look.Name.Value
                ),
                });

                return true;
            case WorldMutation.RemoveLook m:
                if (!Remove(
                    list: current.Looks,
                    key: m.Name,
                    keyOf: static look => look.Name.Value,
                    result: out var looks
                )) {
                    candidate = current;
                    reason = $"no look row named '{m.Name}'";

                    return false;
                }

                candidate = (current with { LooksRaw = looks });

                return true;
            case WorldMutation.SetLookAssignment m:
                candidate = (current with { LookAssignmentRaw = m.Assignment });

                return true;
            case WorldMutation.UpsertGrant m:
                candidate = (current with {
                    GrantsRaw = Upsert(
                    list: current.Grants,
                    item: m.Row,
                    keyOf: static grant => (grant.Principal, grant.Capability, grant.Subject)
                ),
                });

                return true;
            case WorldMutation.RemoveGrant m:
                if (!Remove(
                    list: current.Grants,
                    key: (m.Target.Principal, m.Target.Capability, m.Target.Subject),
                    keyOf: static grant => (grant.Principal, grant.Capability, grant.Subject),
                    result: out var grants
                )) {
                    candidate = current;
                    reason = $"no grant row for {m.Target.Principal.Describe()} {m.Target.Capability.ToString().ToLowerInvariant()} {m.Target.Subject.Describe()}";

                    return false;
                }

                candidate = (current with { GrantsRaw = grants });

                return true;
            case WorldMutation.UpsertHudPanel m:
                candidate = (current with {
                    HudRaw = (current.Hud with {
                        Panels = Upsert(
                    list: current.Hud.Panels,
                    item: m.Panel,
                    keyOf: static panel => panel.Id
                ),
                    }),
                });

                return true;
            case WorldMutation.RemoveHudPanel m:
                if (!Remove(
                    list: current.Hud.Panels,
                    key: m.Id,
                    keyOf: static panel => panel.Id,
                    result: out var hudPanels
                )) {
                    candidate = current;
                    reason = $"no hud panel with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { HudRaw = (current.Hud with { Panels = hudPanels }) });

                return true;
            case WorldMutation.UpsertHudElement m: {
                    if (FindHudPanel(
                        panels: current.Hud.Panels,
                        id: m.PanelId
                    ) is not { } panel) {
                        candidate = current;
                        reason = $"no hud panel with id '{m.PanelId}'";

                        return false;
                    }

                    var updatedPanel = (panel with {
                        Elements = Upsert(
                        list: panel.Elements,
                        item: m.Element,
                        keyOf: static element => element.Id
                    ),
                    });

                    candidate = (current with {
                        HudRaw = (current.Hud with {
                            Panels = Upsert(
                        list: current.Hud.Panels,
                        item: updatedPanel,
                        keyOf: static p => p.Id
                    ),
                        }),
                    });

                    return true;
                }
            case WorldMutation.RemoveHudElement m: {
                    if (FindHudPanel(
                        panels: current.Hud.Panels,
                        id: m.PanelId
                    ) is not { } panel) {
                        candidate = current;
                        reason = $"no hud panel with id '{m.PanelId}'";

                        return false;
                    }

                    if (!Remove(
                        list: panel.Elements,
                        key: m.ElementId,
                        keyOf: static element => element.Id,
                        result: out var elements
                    )) {
                        candidate = current;
                        reason = $"no hud element with id '{m.ElementId}' in panel '{m.PanelId}'";

                        return false;
                    }

                    var updatedPanel = (panel with { Elements = elements });

                    candidate = (current with {
                        HudRaw = (current.Hud with {
                            Panels = Upsert(
                        list: current.Hud.Panels,
                        item: updatedPanel,
                        keyOf: static p => p.Id
                    ),
                        }),
                    });

                    return true;
                }
            case WorldMutation.SetHudDefaults m:
                candidate = (current with { HudRaw = (current.Hud with { Defaults = m.Defaults }) });

                return true;
            case WorldMutation.UpsertStateRow m:
                candidate = current.WithWorldState(rows: Upsert(
                    list: current.State,
                    item: m.Row,
                    keyOf: static row => row.Name
                ));

                return true;
            case WorldMutation.RemoveStateRow m:
                if (!Remove(
                    list: current.State,
                    key: m.Name,
                    keyOf: static row => row.Name,
                    result: out var stateRows
                )) {
                    candidate = current;
                    reason = $"no state row named '{m.Name}'";

                    return false;
                }

                candidate = current.WithWorldState(rows: stateRows);

                return true;
            case WorldMutation.UpsertStateCell m: {
                    // The ONE door: every row-existence and row-KIND decision this write depends on is asked here, against
                    // the CANDIDATE this batch has built so far — never at the console verb, which cannot know whether a
                    // same-batch UpsertStateRow ahead of this one has already declared (or redeclared the kind of) the row
                    // it names.
                    if (WorldDefinitionRows.FindStateRow(
                        rows: current.State,
                        name: m.Row
                    ) is not { } row) {
                        candidate = current;
                        reason = $"no state row named '{m.Row}' — declare it first with world.row.set state <json>";

                        return false;
                    }

                    if (!WorldCellName.TryParse(
                        candidate: m.Key,
                        name: out var cellKey,
                        reason: out var keyReason
                    )) {
                        candidate = current;
                        reason = $"state row '{m.Row}' cell key '{m.Key}' {keyReason}";

                        return false;
                    }

                    // Whether THIS write is a text write is a fact of the WRITE, not the row — a text write always
                    // carries a non-null Text (even ""), a numeric one never does. Asking it this way
                    // (rather than switching on row.Kind) is what lets a kind-mismatched write refuse BY NAME instead of
                    // silently composing against the wrong field: a numeric write against a text row would
                    // otherwise fall into this arm with Text null and overwrite the cell with an empty string.
                    var isTextWrite = (m.Text is not null);

                    // A TEXT row's cell carries a literal string, never a numeric operand: world.state.cell.set's text
                    // arm is this shape's ONE ingress, always submitting Kind=Set, so the Add/advance machinery below never applies.
                    // The whole upsert-or-append-plus-eviction composition (including the reserved-key rule — a text row
                    // is never a generator, so its only legitimate reserved key is the slot cell) delegates to
                    // WorldStateCellWriter — the SHARED pure function an owned-identity document write (which has no
                    // ordered mutation domain of its own) also runs, so the two can never disagree about a victim or a
                    // reserved-cell refusal. TryComposeTextCell itself refuses BY NAME when row.Kind is not Text, which is
                    // this arm's ONE check for "a text operand against a numeric/bool row".
                    // A cycle on a TEXT row: the next token after the one the live cell reads, wrapping; the write
                    // is then an ordinary text set of that token.
                    var isTextCycle = ((row.Kind == CellKind.Text) && (m.CycleTokens is { Count: >= 2 }));

                    if (isTextCycle && (m.Kind != WorldDocumentWriteKind.Set)) {
                        candidate = current;
                        reason = $"state row '{m.Row}' cell '{m.Key}' cycle needs a set write";

                        return false;
                    }

                    if (isTextWrite || isTextCycle) {
                        var textToWrite = m.Text!;

                        if (isTextCycle) {
                            _ = WorldStateReader.TryRead(
                                definition: current,
                                rowName: m.Row,
                                key: m.Key,
                                tick: tick,
                                row: out _,
                                rawValue: out _,
                                text: out var currentText
                            );
                            textToWrite = NextInCycle(
                                tokens: m.CycleTokens!,
                                matches: token => string.Equals(a: token, b: currentText, comparisonType: StringComparison.Ordinal)
                            );
                        }

                        if (!WorldStateCellWriter.TryComposeTextCell(
                            row: row,
                            key: cellKey,
                            text: textToWrite,
                            cells: out var textCells,
                            evictedKey: out evictedKey,
                            reason: out var composeTextReason
                        )) {
                            candidate = current;
                            reason = $"state row '{m.Row}' cell '{m.Key}' {composeTextReason}";

                            return false;
                        }

                        candidate = current.WithWorldState(rows: Upsert(
                            list: current.State,
                            item: (row with { Cells = textCells }),
                            keyOf: static row => row.Name
                        ));

                        return true;
                    }

                    // The reverse kind mismatch: a numeric operand against a Text-kind row. This, and the bool+add
                    // refusal below, are the two kind-dependent REFUSALS the console verb used to ask before submitting —
                    // moved here so they see the same candidate row the existence check above just resolved, rather than
                    // whatever the live definition happened to hold at text-submit time.
                    if (row.Kind == CellKind.Text) {
                        candidate = current;
                        reason = $"state row '{m.Row}' cell '{m.Key}' is text-kind and takes a text operand, never a numeric one";

                        return false;
                    }

                    if (
                        (m.Kind == WorldDocumentWriteKind.Add) &&
                        (row.Kind == CellKind.Bool)
                    ) {
                        candidate = current;
                        reason = $"state row '{m.Row}' cell '{m.Key}' — 'add' is refused on a bool-kind row";

                        return false;
                    }

                    if (
                        (m.CycleTokens is not null) &&
                        ((m.CycleTokens.Count < 2) || (m.Kind != WorldDocumentWriteKind.Set))
                    ) {
                        candidate = current;
                        reason = $"state row '{m.Row}' cell '{m.Key}' cycle needs at least two tokens and a set write";

                        return false;
                    }

                    // The honest encoding for a payload whose SHAPE depends on the row's kind: a console write carries
                    // the un-interpreted wire token (RawToken) because it cannot know Fixed-vs-Int-vs-Bool before this
                    // row's kind resolves against the candidate; a caller that already knows the kind (the rule-effect
                    // engine, which reads the destination row itself before submitting) carries the resolved Value
                    // directly. See WorldMutation.UpsertStateCell.RawToken's remarks.
                    long operand;

                    if (m.CycleTokens is { Count: >= 2 } cycleTokens) {
                        // A cycle on a numeric row: every token must parse against THIS row's kind; the operand is the
                        // token after the one the live value equals (wrapping), else the first. The live value is the
                        // same read every gate and binding runs, so an advancing row cycles from what a reader sees.
                        var parsed = new long[cycleTokens.Count];

                        for (var index = 0; (index < parsed.Length); index++) {
                            if (!WorldStateCellWriter.TryParseNumericToken(
                                kind: row.Kind,
                                token: cycleTokens[index],
                                value: out parsed[index],
                                reason: out var cycleReason
                            )) {
                                candidate = current;
                                reason = $"state row '{m.Row}' cell '{m.Key}' {cycleReason}";

                                return false;
                            }
                        }

                        _ = WorldStateReader.TryRead(
                            definition: current,
                            rowName: m.Row,
                            key: m.Key,
                            tick: tick,
                            row: out _,
                            rawValue: out var live,
                            text: out _
                        );
                        var at = Array.IndexOf(
                            array: parsed,
                            value: (live ?? long.MinValue)
                        );

                        operand = parsed[((at < 0)
                            ? 0
                            : ((at + 1) % parsed.Length))];
                    } else if (m.RawToken is { } rawToken) {
                        if (!WorldStateCellWriter.TryParseNumericToken(
                            kind: row.Kind,
                            token: rawToken,
                            value: out operand,
                            reason: out var tokenReason
                        )) {
                            candidate = current;
                            reason = $"state row '{m.Row}' cell '{m.Key}' {tokenReason}";

                            return false;
                        }
                    } else {
                        operand = m.Value;
                    }

                    // The Add operand comes from WorldStateReader — the SAME read every gate, binding and read-back runs —
                    // rather than from the stored cell. On an ORDINARY row the two are the same value, so this arm keeps
                    // the read-modify-write-onto-the-base behaviour it always had. On an ADVANCING row they differ, and
                    // the live value is the right operand: the stored cell there is a BASE the row has been accumulating
                    // away from, so adding to it would silently discard every unit gained since the epoch (a regen row
                    // sitting at a live 41 taking a -10 would land on -10, not 31). Add means "add to what a reader
                    // sees"; RebaseAdvanceEpoch then makes that sum the new base and starts the accumulation again from
                    // this tick, so the row keeps advancing from the value the author just composed.
                    _ = WorldStateReader.TryRead(
                        definition: current,
                        rowName: m.Row,
                        key: m.Key,
                        tick: tick,
                        row: out _,
                        rawValue: out var addend,
                        text: out _
                    );

                    long value;

                    try {
                        value = ((m.Kind == WorldDocumentWriteKind.Add)
                            ? checked(((addend ?? 0L) + operand))
                            : operand
                        );
                    } catch (OverflowException) {
                        candidate = current;
                        reason = $"state row '{m.Row}' cell '{m.Key}' overflowed";

                        return false;
                    }

                    // The engine-minted-cell rule, asked at the VERB so the operator reads why the cell they just typed
                    // was refused rather than a whole-document validation error. Same code, not a second reading: the
                    // document walk (boot, every mutation, every undo-replay entry) calls the identical
                    // WorldStateReservedCells rule, so the two can never disagree about which reserved keys a row mints.
                    if (!WorldStateReservedCells.TryValidateReservedCell(
                        key: cellKey,
                        reason: out var reservedReason,
                        row: row
                    )) {
                        candidate = current;
                        reason = $"state row '{m.Row}' cell '{m.Key}' {reservedReason}";

                        return false;
                    }

                    // UpsertStateCell carries only a scalar VALUE — a cell's own advance RATE is authored only through a
                    // whole-row UpsertStateRow — so a base-value write here preserves whatever the existing cell already
                    // declared rather than silently deleting it; RebaseAdvanceEpoch (below TryCompose) then re-bases its
                    // epoch to this tick, exactly as it already does for a row-level advance's slot cell.
                    var existingAdvance = FindCellAdvance(
                        cells: (row.Cells ?? []),
                        key: cellKey
                    );
                    var isNewKey = !WorldStateCellWriter.ContainsKey(
                        cells: (row.Cells ?? []),
                        key: cellKey
                    );
                    var cells = Upsert(
                        list: (row.Cells ?? []),
                        item: new WorldStateCell(
                            Key: cellKey,
                            Value: value,
                            Advance: existingAdvance
                        ),
                        keyOf: static (WorldStateCell cell) => cell.Key
                    );

                    cells = WorldStateCellWriter.ApplyEviction(
                        addedNewKey: isNewKey,
                        cells: cells,
                        evictedKey: out evictedKey,
                        row: row
                    );
                    candidate = current.WithWorldState(rows: Upsert(
                        list: current.State,
                        item: (row with { Cells = cells }),
                        keyOf: static row => row.Name
                    ));

                    return true;
                }
            case WorldMutation.RemoveStateCell m: {
                    if (WorldDefinitionRows.FindStateRow(
                        rows: current.State,
                        name: m.Row
                    ) is not { } row) {
                        candidate = current;
                        reason = $"no state row named '{m.Row}'";

                        return false;
                    }

                    if (!Remove(
                        list: (row.Cells ?? []),
                        key: m.Key,
                        keyOf: static (WorldStateCell cell) => cell.Key,
                        result: out var cells
                    )) {
                        candidate = current;
                        reason = $"state row '{m.Row}' has no cell keyed '{m.Key}'";

                        return false;
                    }

                    candidate = current.WithWorldState(rows: Upsert(
                        list: current.State,
                        item: (row with { Cells = cells }),
                        keyOf: static row => row.Name
                    ));

                    return true;
                }
            case WorldMutation.SetInputHold m:
                // The mutation's own wire shape is the COMPILED (ticks) form — the addon-mutation ABI's raw-ticks
                // contract, unchanged — but InputHold itself stores the AUTHORED (seconds) shape (see its remarks), so
                // decompile through the candidate's OWN rate before storing. Exact for a row-set verb's compiled
                // seconds (round-trips through the SAME rate it compiled from); the addon ABI's raw ticks are the one
                // narrow exception WorldInputHoldSettings.ToAuthoring's remarks already accept.
                //
                // THE UNIT-GAP REFUSAL (rate-0 self-lock follow-on): a tick-denominated write has no meaning in a
                // world whose simulation.rateHz is the durable stop — there is no tick↔seconds mapping to decompile
                // through, and dividing by the rate would produce Infinity/NaN that later throws unguarded out of
                // Serialize on save/sync/record. Now that the administrative drain applies buffered mutations even
                // while an instance never steps, this path is reachable, not hypothetical, so it is refused HERE, by
                // name, at the apply door — the legible verdict in front of the structural backstop
                // WorldInputHoldSettings.ToAuthoring's own division-by-rate is separately being hardened to refuse
                // rather than divide; this refusal does not rely on catching that exception.
                if (current.SimulationRateHz <= 0) {
                    candidate = current;
                    reason = $"'{nameof(WorldMutation.SetInputHold)}' carries raw engine ticks, which have no seconds mapping in a world whose simulation.rateHz is 0 (the document's own durable stop) — author input-hold seconds directly, or write this while the world's rate is nonzero";

                    return false;
                }

                candidate = (current with { InputHoldRaw = m.Settings.ToAuthoring(ratePerSecond: ((uint)current.SimulationRateHz)) });

                return true;
            case WorldMutation.Generate m:
                return TryComposeGenerate(
                    candidate: out candidate,
                    current: current,
                    instanceIdentity: instanceIdentity,
                    mutation: m,
                    reason: out reason
                );
            case WorldMutation.UpsertWorldRule m:
                candidate = (current with {
                    Rules = Upsert(
                    list: (current.Rules ?? []),
                    item: m.Rule,
                    keyOf: static (WorldRule rule) => rule.Name
                ),
                });

                return true;
            case WorldMutation.RemoveWorldRule m:
                if (!Remove(
                    list: (current.Rules ?? []),
                    key: m.Name,
                    keyOf: static (WorldRule rule) => rule.Name,
                    result: out var rules
                )) {
                    candidate = current;
                    reason = $"no rule named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Rules = rules });

                return true;
            case WorldMutation.UpsertGroupKind m: {
                    var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                    candidate = (current with {
                        Groups = (groupsSection with {
                            Kinds = Upsert(
                        list: groupsSection.Kinds,
                        item: m.Kind,
                        keyOf: static (WorldGroupKind kind) => kind.Name
                    ),
                        }),
                    });

                    return true;
                }
            case WorldMutation.RemoveGroupKind m: {
                    var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);
                    var referencing = 0;

                    foreach (var row in groupsSection.Groups) {
                        if (string.Equals(
                            a: row.KindName,
                            b: m.Name,
                            comparisonType: StringComparison.Ordinal
                        )) {
                            referencing++;
                        }
                    }

                    if (referencing > 0) {
                        candidate = current;
                        reason = $"group kind '{m.Name}' has {referencing} live group row(s) — remove or re-kind them first";

                        return false;
                    }

                    if (!Remove(
                        list: groupsSection.Kinds,
                        key: m.Name,
                        keyOf: static (WorldGroupKind kind) => kind.Name,
                        result: out var kinds
                    )) {
                        candidate = current;
                        reason = $"no group kind named '{m.Name}'";

                        return false;
                    }

                    candidate = (current with { Groups = (groupsSection with { Kinds = kinds }) });

                    return true;
                }
            case WorldMutation.FormGroup m: {
                    var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                    // The earliest door a LIVE-minted group id crosses (WorldSafeName's own doctrine — see
                    // WorldGroup.Id's remarks): a document-authored id already crossed this door at JSON parse, but
                    // FormGroup mints one at RUNTIME, so this mutation's own apply site IS that door for it. Refused by
                    // name rather than let an unsafe id reach WorldGroup.Id, which the id-to-instance-name composition
                    // (WorldSessionResolver.MintInstanceName) depends on staying safe for every group id, live-formed or
                    // authored alike.
                    if (!WorldSafeName.TryParse(
                        candidate: m.Id,
                        name: out var safeId,
                        reason: out var idReason
                    )) {
                        candidate = current;
                        reason = $"group id '{m.Id}' is not a safe name — {idReason}";

                        return false;
                    }

                    if (FindGroupRow(
                        groups: groupsSection.Groups,
                        id: m.Id
                    ) is not null) {
                        candidate = current;
                        reason = $"group '{m.Id}' already exists";

                        return false;
                    }

                    if (FindGroupKind(
                        kinds: groupsSection.Kinds,
                        name: m.KindName
                    ) is null) {
                        candidate = current;
                        reason = $"no declared group kind named '{m.KindName}'";

                        return false;
                    }

                    candidate = (current with {
                        Groups = (groupsSection with {
                            Groups = Upsert(
                        list: groupsSection.Groups,
                        item: new WorldGroup(
                            Id: safeId,
                            KindName: m.KindName,
                            Members: []
                        ),
                        keyOf: static (WorldGroup row) => row.Id
                    ),
                        }),
                    });

                    return true;
                }
            case WorldMutation.JoinGroup m: {
                    var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                    if (FindGroupRow(
                        groups: groupsSection.Groups,
                        id: m.GroupId
                    ) is not { } group) {
                        candidate = current;
                        reason = $"no group named '{m.GroupId}'";

                        return false;
                    }

                    if (ContainsMember(
                        members: group.Members,
                        member: m.Member
                    )) {
                        candidate = current;
                        reason = $"{m.Member.Describe()} already belongs to group '{m.GroupId}'";

                        return false;
                    }

                    var joined = new List<WorldPrincipal>(collection: group.Members) { m.Member };

                    candidate = (current with {
                        Groups = (groupsSection with {
                            Groups = Upsert(
                        list: groupsSection.Groups,
                        item: (group with { Members = joined }),
                        keyOf: static (WorldGroup row) => row.Id
                    ),
                        }),
                    });

                    return true;
                }
            case WorldMutation.LeaveGroup m: {
                    var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                    if (FindGroupRow(
                        groups: groupsSection.Groups,
                        id: m.GroupId
                    ) is not { } group) {
                        candidate = current;
                        reason = $"no group named '{m.GroupId}'";

                        return false;
                    }

                    if (!ContainsMember(
                        members: group.Members,
                        member: m.Member
                    )) {
                        candidate = current;
                        reason = $"{m.Member.Describe()} does not belong to group '{m.GroupId}'";

                        return false;
                    }

                    var kind = FindGroupKind(
                        kinds: groupsSection.Kinds,
                        name: group.KindName
                    );

                    candidate = (current with {
                        Groups = (groupsSection with {
                            Groups = RemoveMemberAndMaybeDissolve(
                        groups: groupsSection.Groups,
                        group: group,
                        kind: kind,
                        member: m.Member
                    ),
                        }),
                    });

                    return true;
                }
            case WorldMutation.KickMember m: {
                    var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                    if (FindGroupRow(
                        groups: groupsSection.Groups,
                        id: m.GroupId
                    ) is not { } group) {
                        candidate = current;
                        reason = $"no group named '{m.GroupId}'";

                        return false;
                    }

                    if (!ContainsMember(
                        members: group.Members,
                        member: m.Member
                    )) {
                        candidate = current;
                        reason = $"{m.Member.Describe()} does not belong to group '{m.GroupId}'";

                        return false;
                    }

                    var kind = FindGroupKind(
                        kinds: groupsSection.Kinds,
                        name: group.KindName
                    );

                    if (kind?.EvictionPolicy == WorldGroupEvictionPolicy.Disband) {
                        _ = Remove(
                            list: groupsSection.Groups,
                            key: m.GroupId,
                            keyOf: static (WorldGroup row) => row.Id,
                            result: out var disbanded
                        );

                        candidate = (current with { Groups = (groupsSection with { Groups = disbanded }) });

                        return true;
                    }

                    candidate = (current with {
                        Groups = (groupsSection with {
                            Groups = RemoveMemberAndMaybeDissolve(
                        groups: groupsSection.Groups,
                        group: group,
                        kind: kind,
                        member: m.Member
                    ),
                        }),
                    });

                    return true;
                }
            // ESCROW/TRANSFER — the refusal obligation this pair upholds, stated verbatim: no sequence of
            // accepted/refused submissions may leave the same item owned by two principals or by none (escrow counts
            // as one). Every arm below only ever REPLACES one WorldOwnership row's whole Owner with a single,
            // fully-populated OwnershipOwner value — never a partial write — and the ordinary compose->validate->swap
            // pipeline revalidates the WHOLE candidate after EVERY one of these mutations, not just at the end of a
            // trade, so the structural half of the invariant (exactly one owner variant populated) holds at every
            // intermediate state, not only the final one.
            case WorldMutation.OfferOwnership m: {
                    var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                    if (FindOwnershipRow(
                        ownership: groupsSection.Ownership,
                        subject: m.Subject
                    ) is not { } row) {
                        candidate = current;
                        reason = $"no ownership row for subject '{m.Subject.Describe()}'";

                        return false;
                    }

                    if (
                        (row.Owner.Kind != OwnershipOwnerKind.Principal) ||
                        (row.Owner.Principal != m.Principal)
                    ) {
                        candidate = current;
                        reason = $"'{m.Subject.Describe()}' is not owned by {m.Principal.Describe()} (owner.kind={row.Owner.Kind}) — only the current owner may offer it, and only a Principal-owned subject may be offered directly";

                        return false;
                    }

                    if (m.Recipient == m.Principal) {
                        candidate = current;
                        reason = "cannot offer a subject to oneself — that is not a trade";

                        return false;
                    }

                    if (m.DeadlineTick <= unchecked((long)tick)) {
                        candidate = current;
                        reason = $"deadlineTick {m.DeadlineTick} does not lie strictly after tick {tick} — an offer needs a real acceptance window";

                        return false;
                    }

                    var escrowed = (row with {
                        Owner = new OwnershipOwner(
                        Kind: OwnershipOwnerKind.Escrow,
                        Escrow: new OwnershipEscrow(
                            Offerer: m.Principal,
                            Recipient: m.Recipient,
                            DeadlineTick: m.DeadlineTick
                        )
                    ),
                    });

                    candidate = (current with {
                        Groups = (groupsSection with {
                            Ownership = ReplaceOwnership(
                        ownership: groupsSection.Ownership,
                        row: escrowed
                    ),
                        }),
                    });

                    return true;
                }
            case WorldMutation.SettleOwnership m: {
                    var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                    if (FindOwnershipRow(
                        ownership: groupsSection.Ownership,
                        subject: m.Subject
                    ) is not { } row) {
                        candidate = current;
                        reason = $"no ownership row for subject '{m.Subject.Describe()}'";

                        return false;
                    }

                    if (
                        (row.Owner.Kind != OwnershipOwnerKind.Escrow) ||
                        (row.Owner.Escrow is not { } escrow)
                    ) {
                        // The structural guard against the naive "flip the owner field directly" two-submission race: a
                        // settle can ONLY resolve a subject that is ALREADY in escrow — there is no arm anywhere in this
                        // catalog that moves a subject straight from one principal to another, so at most one of a racing
                        // accept/reclaim pair (drained in submission order at the same tick boundary) can ever find the
                        // row still escrowed; the other finds it already resolved and refuses here, never double-applies.
                        candidate = current;
                        reason = $"'{m.Subject.Describe()}' is not currently in escrow (owner.kind={row.Owner.Kind}) — settle only resolves an OfferOwnership, it never transfers directly";

                        return false;
                    }

                    WorldOwnership settled;

                    if (m.Reclaim) {
                        // Manual reclaim is the offerer's own remedy; WorldPrincipal.World is the engine's automatic
                        // sweep (ReclaimExpiredEscrows) firing the identical mutation once the deadline passes with no
                        // accept, so recovery needs no operator action. Both paths are gated on the SAME deadline check
                        // below — the sweep does not jump the queue, it just never forgets to ask.
                        if (
                            (m.Principal != escrow.Offerer) &&
                            (m.Principal != WorldPrincipal.World)
                        ) {
                            candidate = current;
                            reason = $"only the offerer {escrow.Offerer.Describe()} (or the engine's own timeout sweep) may reclaim '{m.Subject.Describe()}'";

                            return false;
                        }

                        if (unchecked((long)tick) < escrow.DeadlineTick) {
                            candidate = current;
                            reason = $"'{m.Subject.Describe()}' is not yet reclaimable — tick {tick} has not reached its deadline {escrow.DeadlineTick}";

                            return false;
                        }

                        settled = (row with {
                            Owner = new OwnershipOwner(
                            Kind: OwnershipOwnerKind.Principal,
                            Principal: escrow.Offerer
                        ),
                        });
                    } else {
                        if (m.Principal != escrow.Recipient) {
                            candidate = current;
                            reason = $"'{m.Subject.Describe()}' names recipient {escrow.Recipient.Describe()}, not the acting principal {m.Principal.Describe()}";

                            return false;
                        }

                        settled = (row with {
                            Owner = new OwnershipOwner(
                            Kind: OwnershipOwnerKind.Principal,
                            Principal: escrow.Recipient
                        ),
                        });
                    }

                    candidate = (current with {
                        Groups = (groupsSection with {
                            Ownership = ReplaceOwnership(
                        ownership: groupsSection.Ownership,
                        row: settled
                    ),
                        }),
                    });

                    return true;
                }
            // ONE kind, two shapes (Remove) — see SetProperty's own remarks for why the pair is consolidated onto a
            // single ordinal.
            case WorldMutation.SetProperty m: {
                    var propertiesSection = (current.Properties ?? WorldPropertyRegistrySection.Empty);

                    if (!m.Remove) {
                        candidate = (current with {
                            Properties = (propertiesSection with {
                                Names = Upsert(
                            list: propertiesSection.Names,
                            item: m.Name,
                            keyOf: static (string name) => name
                        ),
                            }),
                        });

                        return true;
                    }

                    if (!propertiesSection.Names.Contains(value: m.Name)) {
                        candidate = current;
                        reason = $"no property named '{m.Name}'";

                        return false;
                    }

                    var referencing = 0;

                    foreach (var interaction in (current.Interactions?.Interactions ?? [])) {
                        if (
                            string.Equals(
                            a: interaction.Left,
                            b: m.Name,
                            comparisonType: StringComparison.Ordinal
                        ) ||
                            ((interaction.CoOccurrence == WorldInteractionCoOccurrence.Distance) && string.Equals(
                            a: interaction.Right,
                            b: m.Name,
                            comparisonType: StringComparison.Ordinal
                        ))
                        ) {
                            referencing++;
                        }
                    }

                    if (referencing > 0) {
                        candidate = current;
                        reason = $"property '{m.Name}' has {referencing} live interaction row(s) referencing it — remove or re-target them first";

                        return false;
                    }

                    _ = Remove(
                        list: propertiesSection.Names,
                        key: m.Name,
                        keyOf: static (string name) => name,
                        result: out var names
                    );

                    candidate = (current with { Properties = (propertiesSection with { Names = names }) });

                    return true;
                }
            case WorldMutation.UpsertInteraction m: {
                    var interactionsSection = (current.Interactions ?? WorldInteractionsSection.Empty);

                    candidate = (current with {
                        Interactions = (interactionsSection with {
                            Interactions = Upsert(
                        list: interactionsSection.Interactions,
                        item: m.Interaction,
                        keyOf: static (WorldInteraction row) => row.Name
                    ),
                        }),
                    });

                    return true;
                }
            case WorldMutation.RemoveInteraction m: {
                    var interactionsSection = (current.Interactions ?? WorldInteractionsSection.Empty);

                    if (!Remove(
                        list: interactionsSection.Interactions,
                        key: m.Name,
                        keyOf: static (WorldInteraction row) => row.Name,
                        result: out var interactions
                    )) {
                        candidate = current;
                        reason = $"no interaction named '{m.Name}'";

                        return false;
                    }

                    candidate = (current with { Interactions = (interactionsSection with { Interactions = interactions }) });

                    return true;
                }
            case WorldMutation.CreateMarketListing m:
                return TryComposeCreateMarketListing(
                    candidate: out candidate,
                    current: current,
                    mutation: m,
                    reason: out reason,
                    tick: tick
                );
            case WorldMutation.PlaceMarketBid m:
                return TryComposePlaceMarketBid(
                    candidate: out candidate,
                    current: current,
                    mutation: m,
                    reason: out reason,
                    tick: tick
                );
            case WorldMutation.BuyoutMarketListing m:
                return TryComposeBuyoutMarketListing(
                    candidate: out candidate,
                    current: current,
                    mutation: m,
                    reason: out reason,
                    tick: tick
                );
            case WorldMutation.CancelMarketListing m:
                return TryComposeCancelMarketListing(
                    candidate: out candidate,
                    current: current,
                    mutation: m,
                    reason: out reason,
                    tick: tick
                );
            case WorldMutation.SettleMarketListing m:
                return TryComposeSettleMarketListing(
                    candidate: out candidate,
                    current: current,
                    mutation: m,
                    reason: out reason,
                    tick: tick
                );
            case WorldMutation.PruneMarketListings m:
                return TryComposePruneMarketListings(
                    candidate: out candidate,
                    current: current,
                    mutation: m,
                    reason: out reason,
                    tick: tick
                );
            default:
                candidate = current;
                reason = "unknown mutation kind";

                return false;
        }
    }
    // Composes Generate as a PURE function of (candidate document, instance identity): the site's source resolves
    // from the document, WorldGeneratorEngine SEEKS the stream to the position the site's own DrawCursor records, and
    // BOTH the drawn value and the advanced cursor/decks land in the SAME candidate. Nothing lives outside the
    // document, which is what makes world.undo rewind a draw bit-identically with no bookkeeping to reconcile. The
    // sampling itself lives in Puck.World.Schema because the BOOT resolver — which runs before this server exists —
    // must reach the identical code.
    private static bool TryComposeGenerate(WorldDefinition current, WorldMutation.Generate mutation, string instanceIdentity, out WorldDefinition candidate, out string reason) {
        candidate = current;

        if (WorldDefinitionRows.FindStateRow(
            rows: current.State,
            name: mutation.Row
        ) is not { } siteRow) {
            reason = $"no state row named '{mutation.Row}'";

            return false;
        }

        if (siteRow.Draw is not { } draw) {
            reason = $"state row '{mutation.Row}' declares no draw — 'generate' redraws a draw site";

            return false;
        }

        if (draw.Timing == WorldDrawTiming.Boot) {
            reason = $"state row '{mutation.Row}' declares timing=boot — it draws once at first fill and is never redrawn";

            return false;
        }

        if (!WorldGeneratorEngine.TryResolveSource(
            generators: current.Generators,
            draw: draw,
            generator: out var generator,
            reason: out var resolveReason
        )) {
            reason = $"state row '{mutation.Row}' {resolveReason}";

            return false;
        }

        var site = WorldDrawSites.StateRow(rowName: siteRow.Name);

        if (!WorldGeneratorEngine.TryFire(
            generator: generator,
            targetKind: siteRow.Kind,
            seedState: WorldGeneratorEngine.ComputeSeedState(
                worldSeed: (current.Generation?.WorldSeed ?? 0UL),
                instanceIdentity: instanceIdentity,
                site: site
            ),
            stream: WorldGeneratorEngine.ComputeStreamId(site: site),
            cursor: siteRow.DrawCursor,
            decks: siteRow.DrawDecks,
            result: out var fired,
            reason: out var fireReason
        )) {
            reason = $"state row '{mutation.Row}' {fireReason}";

            return false;
        }

        if (
            (fired.Text is { } emission) &&
            (emission.Length > WorldStateCapacity.MaxTextValueLength)
        ) {
            reason = $"state row '{mutation.Row}' emission length {emission.Length} exceeds the {WorldStateCapacity.MaxTextValueLength}-unit text bound";

            return false;
        }

        var cell = ((fired.Text is { } text)
            ? new WorldStateCell(
                Key: WorldStateRow.SlotKey,
                Text: text
            )
            : new WorldStateCell(
                Key: WorldStateRow.SlotKey,
                Value: fired.Numeric!.Value
            )
        );
        var state = Upsert(
            list: current.State,
            item: (siteRow with { Cells = [cell], DrawCursor = (siteRow.DrawCursor + fired.Samples), DrawDecks = (fired.Decks ?? siteRow.DrawDecks) }),
            keyOf: static (WorldStateRow row) => row.Name
        );

        candidate = current.WithWorldState(rows: state);
        reason = string.Empty;

        return true;
    }
    // Replace the row whose key matches the item's, or append it — the coarse whole-row upsert.
    private static IReadOnlyList<T> Upsert<T, TKey>(IReadOnlyList<T> list, T item, Func<T, TKey> keyOf) {
        var key = keyOf(arg: item);
        var result = new List<T>(capacity: (list.Count + 1));
        var replaced = false;

        foreach (var existing in list) {
            if (
                !replaced &&
                EqualityComparer<TKey>.Default.Equals(
                x: keyOf(arg: existing),
                y: key
            )
            ) {
                result.Add(item: item);
                replaced = true;
            } else {
                result.Add(item: existing);
            }
        }

        if (!replaced) {
            result.Add(item: item);
        }

        return result;
    }
    // The token after the one <paramref name="matches"/> accepts, wrapping; the first when none matches.
    private static string NextInCycle(IReadOnlyList<string> tokens, Func<string, bool> matches) {
        for (var index = 0; (index < tokens.Count); index++) {
            if (matches(arg: tokens[index])) {
                return tokens[((index + 1) % tokens.Count)];
            }
        }

        return tokens[0];
    }

}
