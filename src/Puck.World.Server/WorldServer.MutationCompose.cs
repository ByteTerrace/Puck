using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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
    // A batch is classified by its members: it affects whatever any member affects.
    private static bool AnyMember(WorldMutation mutation, Func<WorldMutation, bool> affects) {
        if (mutation is not WorldMutation.Batch batch) {
            return false;
        }
        foreach (var member in batch.Mutations) {
            if (affects(member)) {
                return true;
            }
        }
        return false;
    }
    private static bool AffectsPopulation(WorldMutation mutation) => AnyMember(mutation, AffectsPopulation) || (mutation is
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
        WorldMutation.SetPopulationDefaults or WorldMutation.SetPopulationDistribution or WorldMutation.SetPopulationCensus or
        // A placement row can change the census (Arc 7's Inhabit facet: a placement contributes driven bodies), and an
        // inhabited row's kit resolution reads the creation's Locomotion, so a creation swap can move a body between
        // kits — all must trigger Rebuild + ReconcileInhabitants. (R13: the third and last edit to this switch.)
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement or
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation or
        // A dynamics row retune must recompile every fixed-point table that resolved it (a kit's planar shaping
        // among them), the same live-recompile rule a motion/collision edit already rides.
        WorldMutation.UpsertDynamics or WorldMutation.RemoveDynamics or
        // A curves row retune must recompile the population's curve table index a body-motion producer's curve
        // target source resolves by ordinal, the same live-recompile rule a dynamics retune already rides.
        WorldMutation.UpsertCurve or WorldMutation.RemoveCurve);
    // Whether a mutation touches the addons section — the only door WorldAddonRow row content (or document order)
    // moves through OUTSIDE a whole-document rebuild (ApplyRebuild carries its own unconditional prepare, which
    // also covers a channel-table change by restaging the whole host), so a per-row structural diff gated on JUST
    // these two kinds is the whole trigger a live mutation needs — see IWorldAddonHost.TryPrepare's own remarks.
    private static bool AffectsAddons(WorldMutation mutation) => AnyMember(mutation, AffectsAddons) || (mutation is
        WorldMutation.UpsertAddon or WorldMutation.RemoveAddon);
    // Whether a mutation touches the screens section — the transactional prepare/commit gate for screen machines.
    private static bool AffectsScreens(WorldMutation mutation) => AnyMember(mutation, AffectsScreens) || (mutation is
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen);
    // Machine declarations change runtime ownership, independently of render or population capacity.
    private static bool AffectsMachines(WorldMutation mutation) => AnyMember(mutation, AffectsMachines) || (mutation is
        WorldMutation.UpsertMachine or WorldMutation.RemoveMachine);
    // Whether a mutation can grow the SDF program past the probed render envelope (screen slabs / creation stamps — an
    // UpsertCreation re-shapes every live placement of it, so it measures too).
    private static bool AffectsRenderEnvelope(WorldMutation mutation) => AnyMember(mutation, AffectsRenderEnvelope) || (mutation is
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen or
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation or
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement or
        // A creation look will change the emitted program word count (a body worn as a stamp) once creation-look
        // rendering lands (Arc 7); catalog looks add zero words today, so this arm is honest groundwork — all three look
        // mutations already ride the envelope gate so the loud capacity rejection will fire at apply time, not at a later
        // GPU allocation, the moment creation stamps render.
        WorldMutation.UpsertLook or WorldMutation.RemoveLook or WorldMutation.SetLookAssignment);
    // Whether a mutation can change the SDF contact field: the collision tuning, every solid-bearing section
    // (screens, creations that reshape a stamp, placements), and the inputs of the field's contact band — the kit
    // colliders and the bodies row naming the scale row. Coarse by section, matching AffectsPopulation/
    // AffectsRenderEnvelope; a whole-row upsert of the scale row itself is not listed, since every state-row edit
    // would then rebuild the population.
    private static bool AffectsSolidField(WorldMutation mutation) => AnyMember(mutation, AffectsSolidField) || (mutation is
        WorldMutation.SetCollision or
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen or
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation or
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement or
        WorldMutation.UpsertKit or WorldMutation.RemoveKit or WorldMutation.SetPopulationDefaults);
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
    private static bool IsDocumentDefaults(WorldMutation mutation) => AnyMember(mutation, IsDocumentDefaults) || (mutation is
        WorldMutation.SetRenderDefaults or WorldMutation.SetPopulationDefaults or WorldMutation.SetPopulationDistribution or WorldMutation.SetPopulationCensus or WorldMutation.SetHostDefaults);
    // An EXPLICIT write to a cell carrying StateAdvance or StateDynamics — a whole-row UpsertStateRow
    // (which re-bases the row's OWN slot trait AND every keyed cell's own trait, since it re-declares the whole
    // row), or an UpsertStateCell (which re-bases ONLY the one cell it names — the row's slot trait when that cell IS
    // the slot key, or that cell's own trait otherwise) — re-bases the trait to `tick`,
    // unconditionally overwriting whatever epoch the write's own payload carried. An Advance trait's base becomes
    // exactly the value the write installed (see StateAdvance's remarks); a Dynamics trait's Y0/V0 become the
    // eased value/velocity the OLD trait would report at this tick plus a Retarget kick for the target's own jump
    // (see RebaseDynamics) — never the raw write, so the follower keeps chasing from wherever it actually was. Runs
    // AFTER TryCompose so it sees the row/cell TryCompose just installed, and BEFORE validation/journal so a rebased
    // trait is what gets journaled, replayed by world.undo, and read back. `original` is the document the mutation
    // composed against (before this mutation applied) — a Dynamics rebase needs it to evaluate the OLD trait/target
    // at `tick`. A no-op for every other mutation kind, and for a cell (row-level or per-cell) that carries neither
    // trait.
    private static WorldDefinition RebaseCellTraits(WorldDefinition original, WorldDefinition candidate, WorldMutation mutation, ulong tick) {
        string? rowName;
        string? cellKey; // null on a whole-row write (every trait-bearing cell re-bases); the named key on a per-cell write.

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

        var rebasedRow = RebaseCellTraits(
            cellKey: cellKey,
            original: original,
            originalRow: WorldDefinitionRows.FindStateRow(
                rows: original.State,
                name: rowName
            ),
            row: row,
            tick: tick
        );

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
    // The row-level rebase the mutation-level overload and a batch's workspace share: `row` is the written row as
    // composed, `originalRow` the same row before the write (null when the write declared it), `cellKey` null for
    // a whole-row write. Returns `row` itself when nothing it carries needed re-basing.
    private static WorldStateRow RebaseCellTraits(WorldDefinition original, WorldStateRow? originalRow, WorldStateRow row, string? cellKey, ulong tick) {
        var epoch = unchecked((long)tick);
        var rebasedRow = row;
        var addressesSlot = ((cellKey is null) || string.Equals(
            a: cellKey,
            b: WorldStateRow.SlotKey,
            comparisonType: StringComparison.Ordinal
        ));

        if (addressesSlot) {
            if (row.Advance is { } rowAdvance) {
                rebasedRow = (rebasedRow with { Advance = (rowAdvance with { EpochTick = epoch }) });
            }

            if (row.Dynamics is { } rowDynamics) {
                rebasedRow = (rebasedRow with {
                    Dynamics = RebaseDynamics(
                    candidateTrait: rowDynamics,
                    newTarget: (StateRows.FindCell(
                        cells: row.Cells,
                        key: WorldStateRow.SlotKey
                    )?.Value ?? 0L),
                    original: original,
                    originalCell: StateRows.FindCell(
                        cells: originalRow?.Cells,
                        key: WorldStateRow.SlotKey
                    ),
                    originalRow: originalRow,
                    tick: tick
                ),
                });
            }
        }

        var cells = (rebasedRow.Cells ?? []);
        List<StateCell>? rebasedCells = null;

        for (var index = 0; (index < cells.Count); index++) {
            var cell = cells[index];

            if (
                (cellKey is not null) &&
                !string.Equals(
                a: cell.Key.Value,
                b: cellKey,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                continue;
            }

            if (RebaseOneCell(
                cell: cell,
                definition: original,
                row: originalRow,
                tick: tick
            ) is not { } rebased) {
                continue;
            }

            rebasedCells ??= new List<StateCell>(collection: cells);
            rebasedCells[index] = rebased;
        }

        if (rebasedCells is not null) {
            rebasedRow = (rebasedRow with { Cells = rebasedCells });
        }

        return rebasedRow;
    }
    // Rebases ONE cell's Advance/Dynamics trait to `tick`, against the PRE-write `row`/`definition` — the shared
    // body RebaseCellTraits' per-cell loop and RebaseKeyedCellTraits' single-cell arm both perform. Returns null
    // for a cell that carries neither trait, so a caller's own loop can skip an untouched cell in one check.
    private static StateCell? RebaseOneCell(StateCell cell, WorldStateRow? row, WorldDefinition definition, ulong tick) {
        var advance = cell.Advance;
        var dynamics = cell.Dynamics;
        var changed = false;

        if (advance is { } cellAdvance) {
            advance = (cellAdvance with { EpochTick = unchecked((long)tick) });
            changed = true;
        }

        if (dynamics is { } cellDynamics) {
            dynamics = RebaseDynamics(
                candidateTrait: cellDynamics,
                newTarget: cell.Value,
                original: definition,
                originalCell: StateRows.FindCell(
                    cells: row?.Cells,
                    key: cell.Key
                ),
                originalRow: row,
                tick: tick
            );
            changed = true;
        }

        return (changed ? (cell with { Advance = advance, Dynamics = dynamics }) : null);
    }
    // The write-side counterpart of WorldStateReader.TryEvaluateDynamics: a cell's StateDynamics trait is
    // rebased, never replaced wholesale, by an explicit write to its own truth value. The trait's Y0/V0 become the
    // eased sample the OLD trait (against original/originalCell's own PRE-write target) would report AT `tick` —
    // never the raw write, so the follower keeps chasing from wherever it actually was — plus a Retarget velocity
    // kick for the target's own jump from the old truth to `newTarget`, both computed through the SAME dynamics row
    // that produced the sample. A cell that had no PRIOR active trait (originalRow/originalCell absent, or its own
    // trait unresolvable) keeps whatever Y0/V0 candidateTrait's own payload carries — there is nothing yet to ease
    // from — and only re-bases the epoch, mirroring StateAdvance's own rebase rule.
    private static StateDynamics? RebaseDynamics(
        StateDynamics? candidateTrait,
        WorldDefinition original,
        WorldStateRow? originalRow,
        StateCell? originalCell,
        long newTarget,
        ulong tick
    ) {
        if (candidateTrait is null) {
            return null;
        }

        var epoch = unchecked((long)tick);

        if (
            (originalRow is null) ||
            (originalCell is null) ||
            !WorldStateReader.TryEvaluateDynamics(
            cell: originalCell,
            definition: original,
            row: originalRow,
            sample: out var sample,
            tick: tick,
            trait: out var originalTrait
        ) ||
            (StateRows.FindDynamics(
            dynamics: original.Dynamics,
            name: originalTrait.Row
        ) is not { } dynamicsRow)
        ) {
            return (candidateTrait with { EpochTick = epoch });
        }

        var kicked = dynamicsRow.Compiled.Retarget(
            current: sample,
            newTarget: StateReader.DynamicsRowRawToFixed(
                raw: newTarget,
                row: originalRow
            ),
            oldTarget: StateReader.DynamicsRowRawToFixed(
                row: originalRow,
                raw: originalCell.Value
            )
        );

        return new StateDynamics(
            Row: candidateTrait.Row,
            Y0: StateReader.DynamicsFixedToTraitRaw(value: sample.Value),
            V0: StateReader.DynamicsFixedToTraitRaw(value: kicked.Velocity),
            EpochTick: epoch
        );
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
        WorldMutation.UpsertMachine or WorldMutation.RemoveMachine => WorldSection.Machines,
        WorldMutation.UpsertCamera or WorldMutation.RemoveCamera => WorldSection.Cameras,
        WorldMutation.SetSpawns => WorldSection.Spawns,
        WorldMutation.SetMotion => WorldSection.Motion,
        WorldMutation.SetPopulationDefaults or WorldMutation.SetPopulationDistribution or WorldMutation.SetPopulationCensus => WorldSection.Population,
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
        WorldMutation.SetViewDefaults or WorldMutation.SetViewSeatRig or WorldMutation.SetViewSeatControl or WorldMutation.UpsertViewLayout or WorldMutation.RemoveViewLayout
            or WorldMutation.UpsertViewStudy or WorldMutation.RemoveViewStudy => WorldSection.Views,
        WorldMutation.SetPlayerDefaults or WorldMutation.SetPlayerSeatLook => WorldSection.PlayerDefaults,
        WorldMutation.UpsertLook or WorldMutation.RemoveLook or WorldMutation.SetLookAssignment => WorldSection.Looks,
        WorldMutation.UpsertDynamics or WorldMutation.RemoveDynamics => WorldSection.Dynamics,
        WorldMutation.UpsertCurve or WorldMutation.RemoveCurve => WorldSection.Curves,
        WorldMutation.UpsertGrant or WorldMutation.RemoveGrant => WorldSection.Grants,
        WorldMutation.UpsertHudPanel or WorldMutation.RemoveHudPanel or WorldMutation.UpsertHudElement or WorldMutation.RemoveHudElement or WorldMutation.SetHudDefaults => WorldSection.Hud,
        // Generate's OBSERVABLE effect is a state write, so it shares the state section's coarse hold; its narrower
        // authority is the SAME row-scoped Edit/state:<row> hold every other state write takes, never a second
        // section.
        WorldMutation.UpsertStateRow or WorldMutation.RemoveStateRow or WorldMutation.UpsertStateCell or WorldMutation.RemoveStateCell or WorldMutation.Generate or WorldMutation.TransformState => WorldSection.State,
        WorldMutation.SetInputHold => WorldSection.InputHold,
        WorldMutation.Batch => WorldSection.State,
        WorldMutation.UpsertWorldRule or WorldMutation.RemoveWorldRule => WorldSection.Rules,
        WorldMutation.UpsertGroupKind or WorldMutation.RemoveGroupKind or WorldMutation.FormGroup or WorldMutation.JoinGroup or WorldMutation.LeaveGroup or WorldMutation.KickMember
            or WorldMutation.OfferOwnership or WorldMutation.SettleOwnership => WorldSection.Groups,
        WorldMutation.SetProperty => WorldSection.Properties,
        WorldMutation.UpsertInteraction or WorldMutation.RemoveInteraction => WorldSection.Interactions,
        // No silent fallback: a new mutation kind added without its own arm would otherwise inherit Kits authority. A
        // missing arm throws the first time that kind is mapped — surfaced loudly at runtime rather than mis-authorized.
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(mutation),
        actualValue: mutation,
        message: $"no WorldSection arm for mutation kind '{mutation.GetType().Name}' — every kind must map to its authorizing section."
    ),
    };
    // A submitted row whose document carries `state.` references, resolved against the current definition's state
    // before anything in its compose arm reads a bound value. The copy is private (the row's own JSON round trip
    // through the same JsonTypeInfo the console and the wire parse it with): a submitter's row can share value
    // holders with the installed document — a sculpt carries unchanged shapes forward by reference — and resolving
    // those in place against a candidate that is then rejected would leak the rejected state into the live world,
    // the same reason WorldStateDocumentValues.TryRehydrate copies a whole definition. A row carrying no reference
    // is handed back untouched.
    private static bool TryResolveSubmittedRow<TRow>(WorldDefinition current, TRow row, JsonTypeInfo<TRow> typeInfo, string kind, string id, out TRow resolved, out string reason) where TRow : class {
        if (!WorldStateDocumentValues.HasReference(graph: row)) {
            resolved = row;
            reason = string.Empty;

            return true;
        }

        TRow copy;

        try {
            copy = (JsonSerializer.Deserialize(
                jsonTypeInfo: typeInfo,
                utf8Json: JsonSerializer.SerializeToUtf8Bytes(
                    jsonTypeInfo: typeInfo,
                    value: row
                )
            ) ?? throw new InvalidOperationException(message: $"{kind} '{id}' deserialized to null."));
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            resolved = row;
            reason = $"{kind} '{id}': {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (!WorldStateDocumentValues.TryResolveGraph(
            graph: copy,
            reason: out var resolveReason,
            source: current
        )) {
            resolved = row;
            reason = $"{kind} '{id}': {resolveReason}";

            return false;
        }

        resolved = copy;
        reason = string.Empty;

        return true;
    }
    /// <summary>Owns canonical document validation and authored-hash matching at the mutation composition boundary.</summary>
    // `hash` is the row's AUTHORED hash lane (a creation's HashRaw, a tune/patch's stored Hash) — never a computed
    // property, whose canonicalize-on-read would throw on a hostile document in the caller's own argument list,
    // before this boundary's refusal could run. Null means the submitter carried none: the canonical hash is adopted
    // (an absent hash is trivially self-consistent — WorldPrototype.Hash's own rule), while a CARRIED hash must equal
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
        } catch (Exception exception) when ((exception is Puck.Assets.Documents.DocumentValidationException or InvalidOperationException)) {
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

    /// <summary>Owns load + canonical hash verification for a name/source/hash reference row at the mutation
    /// composition boundary (<see cref="WorldTune"/>/<see cref="WorldPatch"/>) — the referenced twin of <see
    /// cref="TryCanonicalizeDocument{TDocument}"/>: the row carries no document to write back, so a successful
    /// verify only proves the row is admissible, never returns a payload.</summary>
    private delegate bool AssetRowLoader<in TRow, TDocument>(TRow row, out TDocument? document, out string? error);

    private static bool TryVerifyReferencedAsset<TRow, TDocument>(
        TRow row,
        string id,
        string hash,
        string kind,
        AssetRowLoader<TRow, TDocument> tryLoad,
        Func<TDocument, string, Puck.Assets.Documents.CanonicalDocument<TDocument>> canonicalize,
        out string reason) where TDocument : class {
        if (!tryLoad(row, out var document, out var loadError)) {
            reason = $"{kind} '{id}': {loadError}";

            return false;
        }

        Puck.Assets.Documents.CanonicalDocument<TDocument> canonical;

        try {
            canonical = canonicalize(
                arg1: document!,
                arg2: id
            );
        } catch (Exception exception) when ((exception is Puck.Assets.Documents.DocumentValidationException or InvalidOperationException)) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }

        if (!string.Equals(
            a: hash,
            b: canonical.Hash,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"{kind} '{id}' hash '{hash}' does not match the canonical sha256 '{canonical.Hash}' — a hash must come from the canonicalize pipeline";

            return false;
        }

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
    private static bool TryCompose(WorldDefinition current, WorldMutation mutation, ulong tick, string instanceIdentity, out WorldDefinition candidate, out string reason, out CellName? evictedKey, CompiledPatterns? patterns = null) {
        if (!TryComposeCore(
            candidate: out candidate,
            current: current,
            evictedKey: out evictedKey,
            instanceIdentity: instanceIdentity,
            mutation: mutation,
            reason: out reason,
            tick: tick,
            patterns: patterns
        )) {
            return false;
        }

        var stateRow = StateRowOf(mutation: mutation);

        if (stateRow is not null) {
            if (!WorldStateDocumentValues.TryRefresh(
                definition: candidate,
                reason: out reason,
                refreshed: out candidate,
                rowName: stateRow
            )) {
                return false;
            }
        } else if (
            // The mirror of the state-write refresh above: a row write that introduces a bound value (a creation
            // whose shapes read `state.` cells, a placement whose position names one) resolves it against the
            // candidate's own state through the same whole-candidate rehydration, so validation and every derived
            // rebuild read a resolved holder. A mutation carrying no reference pays one cached-shape walk over its
            // own payload and nothing more.
            WorldStateDocumentValues.HasReference(graph: mutation) &&
            !WorldStateDocumentValues.TryRehydrate(
                definition: candidate,
                reason: out reason,
                refreshed: out candidate
            )
        ) {
            return false;
        }

        // Every accepted mutation recomposes a derived board's cells from its tokens/codes rows' CURRENT values, so
        // the candidate this call hands back — the one validation checks and the journal records — already carries
        // them; a hypothetical evaluation's own frame recomputes the identical answer incrementally instead (see
        // StateFrame), never through this whole-document pass.
        candidate = RecomposeDerivedBoards(definition: candidate, previous: current);

        return true;
    }
    // The one recompute a document-level derived board gets: for every CellsOf row declaring Inverse, its cells
    // become exactly DerivedBoards.Compose's answer over the candidate's OWN current tokens/codes rows — so a
    // mutation that moves a token, and one that never touches either row, both leave every derived board correct.
    private static WorldDefinition RecomposeDerivedBoards(WorldDefinition definition, WorldDefinition? previous = null) {
        var rows = definition.State;
        List<WorldStateRow>? recomposed = null;

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];

            if ((row.EffectiveDomain is not StateDomain.CellsOf board) || (row.Inverse is not { } inverse)) {
                continue;
            }
            // A compose that left both source rows as the very objects the installed document holds cannot have
            // changed the derivation; only an install with no prior document recomputes unconditionally.
            if ((previous is not null) && SameSourceRows(previous: previous.State, current: rows, inverse: inverse)) {
                continue;
            }
            if (WorldTopologyCompilation.Find(definition, board.Topology) is not { } topology) {
                continue;
            }

            var derived = DerivedBoards.Compose(rows: rows, inverse: inverse, topology: topology);

            if (SameCells(left: row.Cells, right: derived)) {
                continue;
            }

            recomposed ??= new List<WorldStateRow>(collection: rows);
            recomposed[index] = (row with { Cells = derived });
        }

        return ((recomposed is null) ? definition : definition.WithWorldState(rows: recomposed));
    }
    private static bool SameSourceRows(IReadOnlyList<WorldStateRow> previous, IReadOnlyList<WorldStateRow> current, StateInverse inverse) =>
        ReferenceEquals(objA: WorldDefinitionRows.FindStateRow(rows: previous, name: inverse.Tokens.Value), objB: WorldDefinitionRows.FindStateRow(rows: current, name: inverse.Tokens.Value)) &&
        ReferenceEquals(objA: WorldDefinitionRows.FindStateRow(rows: previous, name: inverse.Codes.Value), objB: WorldDefinitionRows.FindStateRow(rows: current, name: inverse.Codes.Value));
    private static bool SameCells(IReadOnlyList<StateCell>? left, IReadOnlyList<StateCell> right) {
        var leftCells = (left ?? []);

        if (leftCells.Count != right.Count) {
            return false;
        }

        for (var index = 0; (index < leftCells.Count); index++) {
            if ((leftCells[index].Key != right[index].Key) || (leftCells[index].Value != right[index].Value)) {
                return false;
            }
        }

        return true;
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
    private bool RefreshesLookAssignment(WorldMutation mutation, WorldDefinition candidate) {
        if (StateRowOf(mutation: mutation) is { } row) {
            return WorldStateDocumentValues.ReferencesRow(definition: candidate, graph: candidate.LookAssignment, rowName: row);
        }

        // A transform, a draw, or a batch touches the rows it names; any one of them bound into the look graph
        // refreshes it. One walk collects what the look graph binds; the touched rows are then set lookups.
        m_touchedRows.Clear();

        if (!TryCollectStateMutationRowNames(mutation: mutation, names: m_touchedRows, reason: out _) || (m_touchedRows.Count == 0)) {
            return false;
        }

        m_lookReferencedRows.Clear();
        WorldStateDocumentValues.CollectReferencedRows(graph: candidate.LookAssignment, rows: m_lookReferencedRows);

        if (m_lookReferencedRows.Count == 0) {
            return false;
        }

        foreach (var name in m_touchedRows) {
            if (m_lookReferencedRows.Contains(item: name)) {
                return true;
            }
        }

        return false;
    }
    // Scratch for the rows the look graph binds; the step is single-threaded, so one set serves every door.
    private readonly HashSet<string> m_lookReferencedRows = new(comparer: StringComparer.Ordinal);
    private static bool TryComposeCore(WorldDefinition current, WorldMutation mutation, ulong tick, string instanceIdentity, out WorldDefinition candidate, out string reason, out CellName? evictedKey, CompiledPatterns? patterns = null) {
        reason = string.Empty;
        evictedKey = null;

        switch (mutation) {
            case WorldMutation.UpsertKit m:
                candidate = (current with {
                    KitRowsRaw = Upsert(
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

                candidate = (current with { KitRowsRaw = kits });

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
            case WorldMutation.UpsertMachine m:
                candidate = current with {
                    MachinesRaw = Upsert(current.Machines, m.Machine, static row => row.Name),
                };
                return true;
            case WorldMutation.RemoveMachine m:
                if (!Remove(current.Machines, m.Name, static row => row.Name, out var machines)) {
                    candidate = current;
                    reason = $"no machine '{m.Name}'";
                    return false;
                }
                candidate = current with { MachinesRaw = machines };
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
            case WorldMutation.Batch batch:
                return TryComposeBatch(
                    batch: batch,
                    candidate: out candidate,
                    current: current,
                    evictedKey: out evictedKey,
                    instanceIdentity: instanceIdentity,
                    reason: out reason,
                    tick: tick,
                    patterns: patterns
                );
            // The field-scoped population and views edits compose against the row AS IT STANDS HERE — the pending
            // candidate — so two console verbs queued in one tick each keep the other's field.
            case WorldMutation.SetPopulationDistribution m:
                candidate = (current with { PopulationRaw = (current.Population with { DistributionRaw = m.Distribution }) });

                return true;
            case WorldMutation.SetPopulationCensus m:
                candidate = (current with { PopulationRaw = (current.Population with { SeatActivationRaw = m.SeatActivation, NetworkPlayers = m.NetworkPlayers }) });

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
                    // The canonicalizer reads bound values (a shape's pose, a driver's cadence) ahead of the
                    // whole-candidate rehydration TryCompose runs after this arm, so a submitted document carrying
                    // `state.` references resolves against the current definition's state here, on a private copy.
                    if (!TryResolveSubmittedRow(
                        current: current,
                        id: m.Creation.Id,
                        kind: "creation",
                        reason: out reason,
                        resolved: out var creation,
                        row: m.Creation,
                        typeInfo: WorldJsonContext.Default.WorldPrototype
                    )) {
                        candidate = current;

                        return false;
                    }

                    if (!TryCanonicalizeDocument(
                        document: creation.Document,
                        id: creation.Id,
                        hash: creation.HashRaw,
                        kind: "creation",
                        canonicalize: static (document, source) => Puck.World.Authoring.CreationCanonicalizer.Canonicalize(
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
                        item: (creation with { Document = canonicalDocument }),
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
                            a: placement.PrototypeId,
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

                    candidate = (current with { PlacementRowsRaw = placements });

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
                    if (!TryVerifyReferencedAsset<WorldTune, Puck.Assets.Documents.AudioDocument>(
                        row: m.Tune,
                        id: m.Tune.Name,
                        hash: m.Tune.Hash,
                        kind: "tune",
                        tryLoad: WorldAssetRowLoader.TryLoadTune,
                        canonicalize: static (document, source) => Puck.Assets.Documents.AudioCanonicalizer.Canonicalize(
                            document: document,
                            source: source
                        ),
                        reason: out reason
                    )) {
                        candidate = current;

                        return false;
                    }

                    candidate = (current with {
                        TunesRaw = Upsert(
                        list: current.Tunes,
                        item: m.Tune,
                        keyOf: static tune => tune.Name
                    ),
                    });

                    return true;
                }
            case WorldMutation.RemoveTune m: {
                    if (DescribeSpeakersSourcing(
                        speakers: current.Speakers,
                        matches: source => ((source is WorldSpeakerSource.Tune tune) && string.Equals(
                            a: tune.TuneId,
                            b: m.Name,
                            comparisonType: StringComparison.Ordinal
                        ))
                    ) is { } dependents) {
                        candidate = current;
                        reason = $"tune '{m.Name}' feeds speaker(s) {dependents} — remove or re-source them first";

                        return false;
                    }

                    if (!Remove(
                        list: current.Tunes,
                        key: m.Name,
                        keyOf: static tune => tune.Name,
                        result: out var tunes
                    )) {
                        candidate = current;
                        reason = $"no tune named '{m.Name}'";

                        return false;
                    }

                    candidate = (current with { TunesRaw = tunes });

                    return true;
                }
            case WorldMutation.UpsertPatch m: {
                    if (!TryVerifyReferencedAsset<WorldPatch, Puck.Assets.Documents.SynthPatchDocument>(
                        row: m.Patch,
                        id: m.Patch.Name,
                        hash: m.Patch.Hash,
                        kind: "patch",
                        tryLoad: WorldAssetRowLoader.TryLoadPatch,
                        canonicalize: static (document, source) => Puck.Assets.Documents.SynthPatchCanonicalizer.Canonicalize(
                            document: document,
                            source: source
                        ),
                        reason: out reason
                    )) {
                        candidate = current;

                        return false;
                    }

                    candidate = (current with {
                        PatchesRaw = Upsert(
                        list: current.Patches,
                        item: m.Patch,
                        keyOf: static patch => patch.Name
                    ),
                    });

                    return true;
                }
            case WorldMutation.RemovePatch m: {
                    if (DescribePatchDependents(
                        current: current,
                        patchId: m.Name
                    ) is { } dependents) {
                        candidate = current;
                        reason = $"patch '{m.Name}' is referenced by {dependents} — remove or re-source them first";

                        return false;
                    }

                    if (!Remove(
                        list: current.Patches,
                        key: m.Name,
                        keyOf: static patch => patch.Name,
                        result: out var patches
                    )) {
                        candidate = current;
                        reason = $"no patch named '{m.Name}'";

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
            case WorldMutation.SetViewSeatRig m:
                candidate = (current with { ViewsRaw = (current.Views with { SeatRig = m.SeatRig }) });

                return true;
            case WorldMutation.SetViewSeatControl m:
                candidate = (current with { ViewsRaw = (current.Views with { SeatControl = m.SeatControl }) });

                return true;
            case WorldMutation.SetPlayerDefaults m:
                candidate = (current with { PlayerDefaultsRaw = m.Defaults });

                return true;
            case WorldMutation.SetPlayerSeatLook m:
                candidate = (current with { PlayerDefaultsRaw = (current.PlayerDefaults with { SeatLookRaw = m.SeatLook }) });

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
            case WorldMutation.UpsertViewStudy m: {
                    var views = current.Views;

                    candidate = (current with {
                        ViewsRaw = (views with {
                            Studies = Upsert(
                        list: views.Studies,
                        item: m.Study,
                        keyOf: static study => study.Name
                    ),
                        }),
                    });

                    return true;
                }
            case WorldMutation.RemoveViewStudy m: {
                    var views = current.Views;

                    if (!Remove(
                        list: views.Studies,
                        key: m.Name,
                        keyOf: static study => study.Name,
                        result: out var studies
                    )) {
                        candidate = current;
                        reason = $"no views.studies row named '{m.Name}'";

                        return false;
                    }

                    candidate = (current with { ViewsRaw = (views with { Studies = studies }) });

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
                    LookRowsRaw = Upsert(
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

                candidate = (current with { LookRowsRaw = looks });

                return true;
            case WorldMutation.SetLookAssignment m:
                candidate = (current with { LookAssignmentRaw = m.Assignment });

                return true;
            case WorldMutation.UpsertDynamics m:
                candidate = (current with {
                    DynamicsRaw = Upsert(
                    list: current.Dynamics,
                    item: m.Row,
                    keyOf: static row => row.Name
                ),
                });

                return true;
            case WorldMutation.RemoveDynamics m:
                if (!Remove(
                    list: current.Dynamics,
                    key: m.Name,
                    keyOf: static row => row.Name,
                    result: out var dynamics
                )) {
                    candidate = current;
                    reason = $"no dynamics row named '{m.Name}'";

                    return false;
                }

                candidate = (current with { DynamicsRaw = dynamics });

                return true;
            case WorldMutation.UpsertCurve m:
                candidate = (current with {
                    CurvesRaw = Upsert(
                    list: current.Curves,
                    item: m.Row,
                    keyOf: static row => row.Name
                ),
                });

                return true;
            case WorldMutation.RemoveCurve m:
                if (!Remove(
                    list: current.Curves,
                    key: m.Name,
                    keyOf: static row => row.Name,
                    result: out var curves
                )) {
                    candidate = current;
                    reason = $"no curves row named '{m.Name}'";

                    return false;
                }

                candidate = (current with { CurvesRaw = curves });

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
            case WorldMutation.TransformState m:
                if (m.Principal != WorldPrincipal.World && WorldStateTransforms.Subjects(m.Transform).Any(name =>
                    WorldDefinitionRows.FindStateRow(current.State, name)?.PhaseOf is { } required && m.Guard?.Row != required)) {
                    candidate = current;
                    reason = "operation requires its declared phase guard";
                    return false;
                }
                if (m.Guard is { } guard && !WorldStateTransforms.CanAct(current, guard, m.Principal)) {
                    candidate = current;
                    reason = "phase admission refused";
                    return false;
                }
                if (!WorldStateTransforms.TryApply(current, m.Transform, m.Principal, tick, instanceIdentity, out candidate, out reason, patterns)) {
                    return false;
                }
                // A matching guard both admits and completes: advancing the phase row's generation is the guard's
                // whole job now that turn order, rounds, and readiness are ordinary rows a world's rules author.
                if (m.Guard is { } applied) {
                    candidate = WorldStateTransforms.Advance(candidate, applied.Row);
                }
                return true;
            case WorldMutation.UpsertStateRow m:
                if (WorldDefinitionRows.FindStateRow(rows: current.State, name: m.Row.Name.Value) is { Inverse: { } existingInverse }) {
                    candidate = current;
                    reason = $"state row '{m.Row.Name}' is a derived board (inverse names '{existingInverse.Tokens}'/'{existingInverse.Codes}') — write those rows instead; the engine recomputes '{m.Row.Name}' on install";

                    return false;
                }

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
            case WorldMutation.UpsertStateCell m:
                if (!TryComposeCellUpsert(
                    composed: out var upsertedRow,
                    rows: current.State,
                    evictedKey: out evictedKey,
                    mutation: m,
                    reason: out reason,
                    tick: tick
                )) {
                    candidate = current;

                    return false;
                }

                candidate = current.WithWorldState(rows: Upsert(
                    list: current.State,
                    item: upsertedRow,
                    keyOf: static row => row.Name
                ));

                return true;
            case WorldMutation.RemoveStateCell m:
                if (!TryComposeCellRemove(
                    composed: out var trimmedRow,
                    rows: current.State,
                    mutation: m,
                    reason: out reason
                )) {
                    candidate = current;

                    return false;
                }

                candidate = current.WithWorldState(rows: Upsert(
                    list: current.State,
                    item: trimmedRow,
                    keyOf: static row => row.Name
                ));

                return true;
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

                    // The earliest door a LIVE-minted group id crosses (SafeName's own doctrine — see
                    // WorldGroup.Id's remarks): a document-authored id already crossed this door at JSON parse, but
                    // FormGroup mints one at RUNTIME, so this mutation's own apply site IS that door for it. Refused by
                    // name rather than let an unsafe id reach WorldGroup.Id, which the id-to-instance-name composition
                    // (WorldSessionResolver.MintInstanceName) depends on staying safe for every group id, live-formed or
                    // authored alike.
                    if (!SafeName.TryParse(
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
            default:
                candidate = current;
                reason = "unknown mutation kind";

                return false;
        }
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
