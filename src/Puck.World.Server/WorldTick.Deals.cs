using System.Globalization;
using Puck.Abstractions.Memory;

namespace Puck.World.Server;

public sealed partial class WorldTick {
    // What the last deal read: its template, region, arena and input rows, and the children it left.
    private sealed class DealMemo {
        public WorldDistribution? Distribution;
        public int Slots;
        public ulong RowVersion;
        public ulong VariantVersion;
        public StateArena? Arena;
        public bool Swept;
        public WorldPlacement? Template;
        public ulong WorldSeed;
        // The dealt and variant rows' ordinals in the catalog IndexDealTemplates resolved them against, -1 when absent.
        public int RowOrdinal;
        public int VariantOrdinal;
        // The first row carrying this template's id in the indexed list, -1 when none carries a deal there.
        public int Row = -1;
        // The rows of its dealt children in the indexed list, in placement order.
        public readonly List<int> Children = [];
        // Its children as the last sweep left them, by reference in placement order.
        public readonly List<WorldPlacement> Left = [];
    }
    // The dealt templates of the placement list the sweep last indexed, in placement order, each with its memo, and
    // the list and catalog their indices, children and ordinals were resolved against.
    private readonly List<(int Index, DealMemo Memo)> m_dealTemplates = [];
    private IReadOnlyList<WorldPlacement>? m_dealTemplatesOf;
    private StateCatalog? m_dealTemplatesCatalog;

    // Indexes a placement list's templates and groups its dealt children under them in one pass over the rows, once per
    // list and catalog, and drops the memo of a template the list no longer deals from. Returns whether it indexed.
    private bool IndexDealTemplates(IReadOnlyList<WorldPlacement> placements, StateCatalog catalog) {
        if (ReferenceEquals(objA: m_dealTemplatesOf, objB: placements) && ReferenceEquals(objA: m_dealTemplatesCatalog, objB: catalog)) { return false; }
        m_dealTemplatesOf = placements;
        m_dealTemplatesCatalog = catalog;
        m_dealTemplates.Clear();
        foreach (var memo in m_dealMemos.Values) {
            memo.Row = -1;
            memo.Children.Clear();
        }
        for (var index = 0; (index < placements.Count); index++) {
            if (placements[index] is not { Deal: { } deal } template) { continue; }
            if (!m_dealMemos.TryGetValue(key: template.Id, value: out var memo)) {
                memo = new DealMemo();
                m_dealMemos[template.Id] = memo;
            }
            if (memo.Row < 0) { memo.Row = index; }
            memo.RowOrdinal = (catalog.TryResolve(StateLane.Document, deal.Row, out var rowHandle) ? rowHandle.Ordinal : -1);
            memo.VariantOrdinal = (((deal.Variants is { } variants) && catalog.TryResolve(StateLane.Document, variants.Row, out var variantHandle)) ? variantHandle.Ordinal : -1);
            m_dealTemplates.Add(item: (index, memo));
        }
        foreach (var (templateId, memo) in m_dealMemos) {
            if (memo.Row < 0) { _ = m_dealMemos.Remove(key: templateId); }
        }
        for (var index = 0; (index < placements.Count); index++) {
            if (
                (placements[index] is { Parent: { } parent } row) &&
                m_dealMemos.TryGetValue(key: parent, value: out var memo) &&
                WorldPlacementDeal.IsChild(placement: row, parent: placements[memo.Row])
            ) {
                memo.Children.Add(item: index);
            }
        }
        return true;
    }
    // Whether a template's children in the indexed list are the rows the last sweep left, in the same order.
    private static bool ChildrenStand(IReadOnlyList<WorldPlacement> placements, DealMemo memo) {
        if (memo.Children.Count != memo.Left.Count) { return false; }
        for (var child = 0; (child < memo.Children.Count); child++) {
            if (!ReferenceEquals(objA: placements[memo.Children[child]], objB: memo.Left[child])) { return false; }
        }
        return true;
    }
    // Records every template's children in the indexed list as the ones this sweep leaves.
    private void LeaveDealChildren(IReadOnlyList<WorldPlacement> placements) {
        foreach (var memo in m_dealMemos.Values) {
            memo.Left.Clear();
            foreach (var row in memo.Children) { memo.Left.Add(item: placements[row]); }
        }
    }
    // What one template projection moved this tick, narrated after installation.
    private readonly record struct DealReport(string Template, string Row, int Cells, int Offsets, int Added, int Removed, int Replaced, int Overflow, string? Refused = null);

    private readonly Dictionary<string, DealMemo> m_dealMemos = new(comparer: StringComparer.Ordinal);

    private readonly List<WorldPlacement> m_dealChildren = [];
    private readonly List<int> m_dealChildSlots = [];
    private readonly List<bool> m_dealChildKept = [];
    private readonly List<int> m_dealPendingCells = [];
    private readonly List<(string Id, WorldPlacement? Placement)> m_dealChanges = [];
    private readonly List<CellName> m_dealtKeys = [];
    private readonly List<DealReport> m_dealReports = [];
    private readonly List<WorldPlacement> m_dealInstalled = [];
    private bool[] m_dealSlotTaken = [];
    // One deal's cells by key, and the first child dealt for each cell (by its index in m_dealChildren, -1 for none).
    private readonly Dictionary<string, int> m_dealCellOf = new(comparer: StringComparer.Ordinal);
    private int[] m_dealChildOfCell = [];



    /// <summary>Describes every placement row as one reader's disclosure deals it: its prototype, resolved transform,
    /// parent, facets, and — for a dealt template — the row it deals from with the children present against the row's
    /// capacity, or — for a dealt child — the template that dealt it.</summary>
    /// <param name="view">The reader's view of this server's state; a dealt child read from a cell it withholds is
    /// described as <see cref="WorldStateDisclosure.Disclose"/> re-deals it.</param>
    internal string DescribePlacements(WorldStateReadView view) {
        var definition = view.Definition;
        var placements = definition.Placements;

        if (placements.Count == 0) {
            return "[world.placements: none declared]";
        }

        var lines = new List<string>(capacity: placements.Count);

        foreach (var placement in placements) {
            var frame = WorldDefinitionRows.ResolvedFrame(
                definition: definition,
                placement: placement
            );
            var line = string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"'{placement.Id}' prototype={placement.ShownPrototypeId} at ({frame.Position.X:0.##}, {frame.Position.Y:0.##}, {frame.Position.Z:0.##}) yaw {frame.YawDegrees:0.#} scale {placement.Scale:0.##}"
            );

            if (placement.Parent is { } parent) {
                line += $" parent='{parent}'";
            }

            line += DescribeFacets(placement: placement);

            if (placement.Deal is { } deal) {
                var capacity = ((WorldDefinitionRows.FindStateRow(
                    rows: definition.State,
                    name: deal.Row
                ) is { } row)
                    ? row.CellCeiling
                    : 0
                );

                line += $" dealt from {deal.Row} ({CountDealtChildren(
                    placements: placements,
                    template: placement
                )} of {capacity})";
            } else if (
                (placement.Parent is { } templateId) &&
                WorldPlacementDeal.IsChild(
                placement: placement,
                parent: WorldDefinitionRows.FindPlacement(
                    id: templateId,
                    placements: placements
                )
            )
            ) {
                line += $" dealt by {templateId}";
            }

            lines.Add(item: line);
        }

        return $"[world.placements: {placements.Count} row(s); {string.Join(
            separator: "; ",
            values: lines
        )}]";
    }

    private static string DescribeFacets(WorldPlacement placement) {
        var facets = string.Empty;

        if (placement.DealSlot is { } slot) { facets += $" dealSlot={slot}"; }
        if (placement.Spatial is { Count: > 0 } spatial) {
            facets += $" spatial=({string.Join(
                separator: ",",
                values: spatial.Select(selector: volume => $"{volume.Name}:{volume.Role}{((volume.Channel is { } channel)
                ? $":{channel}"
                : string.Empty)}")
            )})";
        }
        if (placement.Deal?.Preserve is { } preserve) {
            facets += $" preserve=(transform:{preserve.Transform},prototype:{preserve.Prototype},facets:{preserve.Facets})";
        }
        if (placement.Deal?.Reflow is { } reflow) {
            facets += $" reflow=(work:{reflow.CandidateBudget},costPerMove:{reflow.CostPerMove},payer:{(reflow.CostRow ?? "none")})";
        }

        if (placement.Distribution is { } distribution) {
            facets += $" distribution={distribution.Region.GetType().Name.ToLowerInvariant()}";
        }

        if (placement.Mirror is not null) {
            facets += " mirror";
        }

        if (placement.Solid is not null) {
            facets += " solid";
        }

        if (placement.Grip is { } grip) {
            facets += (grip.Holdable
                ? " grip=holdable"
                : " grip=unholdable"
            );
        }

        if (placement.Region is { } region) {
            facets += string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $" region={region.Radius:0.##}"
            );
        }

        if (placement.Emission is { } emission) {
            facets += $" emission={emission.PatchId}";
        }

        if (placement.Inhabit is { } inhabit) {
            facets += $" inhabit={inhabit.ResolvedCount}";
        }

        if (placement.Attach is { } attach) {
            facets += $" attach=body:{attach.BodyIndex}";
        }

        if (placement.Respond is { Count: > 0 } respond) {
            facets += $" respond={respond.Count}";
        }

        if (WorldPlacementResponse.ShownEntry(placement: placement) >= 0) {
            facets += $" authored={placement.PrototypeId}";
        }

        if (placement.Board is not null) {
            facets += " board";
        }

        if (placement.Contribution is not null) {
            facets += " contribution";
        }

        if (placement.FaceSources is { Count: > 0 } faces) {
            facets += $" faces={faces.Count}";
        }

        return facets;
    }
    private static int CountDealtChildren(IReadOnlyList<WorldPlacement> placements, WorldPlacement template) {
        var count = 0;

        foreach (var placement in placements) {
            if (WorldPlacementDeal.IsChild(
                parent: template,
                placement: placement
            )) {
                count++;
            }
        }

        return count;
    }
    // Runs once per tick right after SweepPlacementResponses: the rule frame has folded, so a cell a rule wrote
    // this tick deals on this tick's sweep. A template deals again only when something it deals from moved: its own
    // row, its dealt or variant row, the world seed, or its children. Membership belongs to the source cells, so an
    // edit to a dealt child (a hand edit, a response) re-deals that child's template and no other; the children are
    // compared by reference against those the last sweep left, which is sound because a placement row is replaced,
    // never changed in place, so any change to a child is a new reference (WorldPlacementIndex holds the same
    // invariant). Nothing here runs, and nothing allocates, on a tick where the placement list is the one the last
    // sweep left and every template's rows are unchanged. Every template's children are dealt against the document
    // the sweep opened on (no two templates share a child). Each template's changed children validate as a unit;
    // the tick installs one derived projection without journalling declaration edits.
    private void SweepPlacementDeals(ulong tick) {
        var placements = Host.Document.Definition.Placements;
        var worldSeed = (Host.Document.Definition.Generation?.WorldSeed ?? 0UL);
        var indexed = IndexDealTemplates(placements: placements, catalog: Host.Arena.Catalog);
        var redeals = 0;

        m_dealChanges.Clear();
        m_dealReports.Clear();

        foreach (var (index, memo) in m_dealTemplates) {
            var template = placements[index];
            var deal = template.Deal!;
            var arena = Host.Arena;
            var rowOrdinal = memo.RowOrdinal;
            var variantOrdinal = memo.VariantOrdinal;
            var rowVersion = ((rowOrdinal < 0) ? 0UL : arena.RowVersion(rowOrdinal: rowOrdinal));
            var variantVersion = ((variantOrdinal < 0) ? 0UL : arena.RowVersion(rowOrdinal: variantOrdinal));

            if (
                memo.Swept && (!indexed || ChildrenStand(placements: placements, memo: memo)) &&
                (memo.WorldSeed == worldSeed) &&
                (ReferenceEquals(
                objA: memo.Template,
                objB: template
            ) || template.Equals(other: memo.Template)) &&
                ReferenceEquals(objA: memo.Arena, objB: arena) && (memo.RowVersion == rowVersion) && (memo.VariantVersion == variantVersion)
            ) {
                continue;
            }

            var firstChange = m_dealChanges.Count;
            var firstReport = m_dealReports.Count;

            redeals++;
            Deal(
                placements: placements, arena: arena,
                deal: deal,
                memo: memo,
                rowOrdinal: rowOrdinal,
                template: template,
                variantOrdinal: variantOrdinal,
                worldSeed: worldSeed
            );
            RefuseInvalidDeal(
                definition: Host.Document.Definition,
                firstChange: firstChange,
                firstReport: firstReport,
                template: template
            );
            memo.Arena = arena; memo.RowVersion = rowVersion; memo.VariantVersion = variantVersion;
        }
        WorldTickWork.Process.Add(amount: redeals, kind: WorldTickWork.DealRedeals);

        Host.Document.InstallPlacementProjection(changes: m_dealChanges);
        // The children this sweep leaves: those of the list it installed, else those of the list it read.
        if (m_dealChanges.Count > 0) {
            placements = Host.Document.Definition.Placements;
            indexed |= IndexDealTemplates(placements: placements, catalog: Host.Arena.Catalog);
        }
        if (indexed) { LeaveDealChildren(placements: placements); }

        if (Host.Output.HasNarrationSink) {
            foreach (var report in m_dealReports) {
                Host.Output.Narrate(
                    channel: "world.deal",
                    text: ((report.Refused is { } refusal)
                    ? $"[world.deal: '{report.Template}' dealt {report.Row}: refused as one ({refusal}); its children stay as they were]"
                    : $"[world.deal: '{report.Template}' dealt {report.Row}: {report.Cells} cell(s) over {report.Offsets} offset(s) — {report.Added} added, {report.Removed} removed, {report.Replaced} replaced{((report.Overflow > 0)
                        ? $", {report.Overflow} without a free offset"
                        : string.Empty)}]")
                );
            }
        }
    }
    // A template's deal is one install: when any child it would add or replace is one the document validator would
    // refuse, none of its changes land, so the installed document keeps only rows validation admits. The memo already
    // holds the inputs this deal read, so the refusal repeats only when they move.
    private void RefuseInvalidDeal(WorldDefinition definition, WorldPlacement template, int firstChange, int firstReport) {
        if (m_dealChanges.Count == firstChange) {
            return;
        }

        m_dealInstalled.Clear();

        for (var index = firstChange; (index < m_dealChanges.Count); index++) {
            if (m_dealChanges[index].Placement is { } installed) {
                m_dealInstalled.Add(item: installed);
            }
        }
        if (WorldDefinitionValidator.TryValidateDealtChildren(
            children: m_dealInstalled,
            definition: definition,
            reason: out var reason,
            template: template
        )) {
            return;
        }

        m_dealChanges.RemoveRange(
            count: (m_dealChanges.Count - firstChange),
            index: firstChange
        );
        // Deal reports every template that changed a row, so the report for these changes is the one at firstReport.
        m_dealReports[firstReport] = (m_dealReports[firstReport] with { Refused = reason });
    }
    private void Deal(IReadOnlyList<WorldPlacement> placements, StateArena arena, WorldPlacement template, WorldPlacementDeal deal, int rowOrdinal, int variantOrdinal, DealMemo memo, ulong worldSeed) {
        if (
            !memo.Swept ||
            (memo.WorldSeed != worldSeed) ||
            !ReferenceEquals(
            objA: memo.Distribution,
            objB: template.Distribution
        )
        ) {
            memo.Slots = WorldPlacementDeal.InstanceCount(
                template: template,
                worldSeed: worldSeed
            );
            memo.Distribution = template.Distribution;
            memo.WorldSeed = worldSeed;
        }

        var slots = memo.Slots;

        if (m_dealSlotTaken.Length < slots) {
            m_dealSlotTaken = new bool[slots];
        }

        Array.Clear(
            array: m_dealSlotTaken,
            index: 0,
            length: slots
        );
        m_dealChildren.Clear();
        m_dealChildSlots.Clear();
        m_dealChildKept.Clear();
        m_dealPendingCells.Clear();

        var firstMutation = m_dealChanges.Count;

        foreach (var row in memo.Children) {
            var candidate = placements[row];
            var slot = (candidate.DealSlot ?? -1);

            if (((uint)slot) >= ((uint)slots)) { slot = -1; }

            if (
                (slot >= 0) &&
                m_dealSlotTaken[slot]
            ) {
                slot = -1;
            }

            if (slot >= 0) {
                m_dealSlotTaken[slot] = true;
            }

            m_dealChildren.Add(item: candidate);
            m_dealChildSlots.Add(item: slot);
            m_dealChildKept.Add(item: false);
        }

        m_dealtKeys.Clear();
        var cursor = 0;
        if (rowOrdinal >= 0) { while (arena.TryNextCell(rowOrdinal, ref cursor, out var cellKey)) { m_dealtKeys.Add(arena.Keys[cellKey]); } }
        var cells = m_dealtKeys;
        var replaced = 0;

        MatchChildrenToCells(template: template, cells: cells);
        for (var cellIndex = 0; (cellIndex < cells.Count); cellIndex++) {
            var key = cells[cellIndex].Value;
            var childIndex = m_dealChildOfCell[cellIndex];

            if (childIndex < 0) {
                m_dealPendingCells.Add(item: cellIndex);

                continue;
            }

            m_dealChildKept[childIndex] = true;

            var slot = m_dealChildSlots[childIndex];

            if (slot < 0) {
                m_dealPendingCells.Add(item: cellIndex);

                continue;
            }

            var child = m_dealChildren[childIndex];
            var reconciled = deal.Reconcile(
                child: child,
                template: template,
                prototype: deal.ResolvePrototype(arena: arena, variantOrdinal: variantOrdinal, key: key, templatePrototype: template.PrototypeId),
                slot: slot
            );

            if (child == reconciled) {
                continue;
            }

            replaced++;
            m_dealChanges.Add((reconciled.Id, reconciled));
        }

        var removed = 0;

        for (var childIndex = 0; (childIndex < m_dealChildren.Count); childIndex++) {
            if (m_dealChildKept[childIndex]) {
                continue;
            }

            var slot = m_dealChildSlots[childIndex];

            if (slot >= 0) {
                m_dealSlotTaken[slot] = false;
            }

            removed++;
            m_dealChanges.Add((m_dealChildren[childIndex].Id, null));
        }

        var added = 0;
        var overflow = 0;

        for (var pending = 0; (pending < m_dealPendingCells.Count); pending++) {
            var key = cells[m_dealPendingCells[pending]].Value;
            var slot = FirstFreeSlot(count: slots);

            if (slot < 0) {
                overflow++;

                continue;
            }

            m_dealSlotTaken[slot] = true;

            var existing = m_dealChildOfCell[m_dealPendingCells[pending]];

            if (existing >= 0) {
                replaced++;
            } else {
                added++;
            }

            var prototype = deal.ResolvePrototype(arena: arena, variantOrdinal: variantOrdinal, key: key, templatePrototype: template.PrototypeId);
            var nextChild = ((existing >= 0)
                ? deal.Reconcile(
                    child: m_dealChildren[existing],
                    template: template,
                    prototype: prototype,
                    slot: slot
                )
                : WorldPlacementDeal.Child(
                    id: WorldPlacementDeal.ChildId(
                        template: template.Id,
                        key: key
                    ),
                    slot: slot,
                    prototype: prototype,
                    template: template
                ));
            m_dealChanges.Add((nextChild.Id, nextChild));
        }

        if (
            (m_dealChanges.Count > firstMutation) ||
            (overflow > 0)
        ) {
            m_dealReports.Add(item: new DealReport(
                Template: template.Id,
                Row: deal.Row,
                Cells: cells.Count,
                Offsets: slots,
                Added: added,
                Removed: removed,
                Replaced: replaced,
                Overflow: overflow
            ));
        }

        memo.Template = template;
        memo.Swept = true;
    }
    // Pairs each dealt cell with the first child whose id names it under the template (WorldPlacementDeal.ChildId), by
    // one lookup per child into the cells' keys, so the deal costs its cells and children, never their product.
    private void MatchChildrenToCells(WorldPlacement template, List<CellName> cells) {
        m_dealCellOf.Clear();
        for (var cell = 0; (cell < cells.Count); cell++) { _ = m_dealCellOf.TryAdd(key: cells[cell].Value, value: cell); }
        ArrayCapacity.EnsureScratch(ref m_dealChildOfCell, cells.Count);
        m_dealChildOfCell.AsSpan(start: 0, length: cells.Count).Fill(value: -1);

        var cellOf = m_dealCellOf.GetAlternateLookup<ReadOnlySpan<char>>();
        // Every child's id is its template's id, the separator and its key (WorldPlacementDeal.IsChild).
        var keyStart = (template.Id.Length + 1);

        for (var child = 0; (child < m_dealChildren.Count); child++) {
            if (cellOf.TryGetValue(key: m_dealChildren[child].Id.AsSpan(start: keyStart), value: out var cell) && (m_dealChildOfCell[cell] < 0)) {
                m_dealChildOfCell[cell] = child;
            }
        }
    }
    private int FirstFreeSlot(int count) {
        for (var slot = 0; (slot < count); slot++) {
            if (!m_dealSlotTaken[slot]) {
                return slot;
            }
        }

        return -1;
    }
}
