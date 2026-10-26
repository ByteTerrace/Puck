using System.Globalization;
using System.Numerics;

namespace Puck.World.Server;

public sealed partial class WorldTick {
    // What the last deal of one template read: the template row, its resolved offsets, and a copy of the dealt and
    // variant rows' cells. A tick on which none of it moved skips the template without reading its children.
    private sealed class DealMemo {
        public WorldDistribution? Distribution;
        public Vector3[] Offsets = [];
        public ulong RowVersion;
        public ulong VariantVersion;
        public StateArena? Arena;
        public bool Swept;
        public WorldPlacement? Template;
        public ulong WorldSeed;
    }
    // What one template's deal moved this tick, narrated once the tick's single deal mutation has landed or failed.
    private readonly record struct DealReport(string Template, string Row, int Cells, int Offsets, int Added, int Removed, int Replaced, int Overflow);

    private readonly Dictionary<string, DealMemo> m_dealMemos = new(comparer: StringComparer.Ordinal);
    private readonly List<WorldPlacement> m_dealChildren = [];
    private readonly List<int> m_dealChildSlots = [];
    private readonly List<bool> m_dealChildKept = [];
    private readonly List<int> m_dealPendingCells = [];
    private readonly List<(string Id, WorldPlacement? Placement)> m_dealChanges = [];
    private readonly List<CellName> m_dealtKeys = [];
    private readonly List<DealReport> m_dealReports = [];
    private bool[] m_dealSlotTaken = [];



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
    // this tick deals on this tick's sweep. Nothing here runs, and nothing allocates, on a tick where the installed
    // document is the one the last sweep left — and a template whose dealt row, variant row, and own row are
    // unchanged is skipped before its children are read. Every template's children are dealt against the document
    // the sweep opened on (no two templates share a child), and the tick's whole deal applies as one mutation: one
    // composition, one whole-document validation, one journal entry, landing or refused as a unit.
    private void SweepPlacementDeals(ulong tick) {
        var placements = Host.Document.Definition.Placements;
        var worldSeed = (Host.Document.Definition.Generation?.WorldSeed ?? 0UL);
        var templates = 0;

        m_dealChanges.Clear();
        m_dealReports.Clear();

        for (var index = 0; (index < placements.Count); index++) {
            var template = placements[index];

            if (template.Deal is not { } deal) {
                continue;
            }

            templates++;

            var arena = Host.Arena;
            var rowOrdinal = arena.Catalog.TryResolve(StateLane.Document, deal.Row, out var rowHandle) ? rowHandle.Ordinal : -1;
            var variantOrdinal = deal.Variants is { } variants && arena.Catalog.TryResolve(StateLane.Document, variants.Row, out var variantHandle) ? variantHandle.Ordinal : -1;
            var rowVersion = rowOrdinal < 0 ? 0UL : arena.RowVersion(rowOrdinal);
            var variantVersion = variantOrdinal < 0 ? 0UL : arena.RowVersion(variantOrdinal);
            if (!m_dealMemos.TryGetValue(
                key: template.Id,
                value: out var memo
            )) {
                memo = new DealMemo();
                m_dealMemos[template.Id] = memo;
            }

            if (
                memo.Swept &&
                (memo.WorldSeed == worldSeed) &&
                (ReferenceEquals(
                objA: memo.Template,
                objB: template
            ) || template.Equals(other: memo.Template)) &&
                ReferenceEquals(memo.Arena, arena) && memo.RowVersion == rowVersion && memo.VariantVersion == variantVersion
            ) {
                continue;
            }

            Deal(
                deal: deal,
                memo: memo,
                rowOrdinal: rowOrdinal,
                template: template,
                variantOrdinal: variantOrdinal,
                worldSeed: worldSeed
            );
            memo.Arena = arena; memo.RowVersion = rowVersion; memo.VariantVersion = variantVersion;
        }

        if (m_dealMemos.Count > templates) {
            foreach (var templateId in m_dealMemos.Keys) {
                if (WorldDefinitionRows.FindPlacement(
                    id: templateId,
                    placements: Host.Document.Definition.Placements
                ) is not { Deal: not null }) {
                    _ = m_dealMemos.Remove(key: templateId);
                }
            }
        }

        var applied = true;
        Host.Document.InstallPlacementProjection(changes: m_dealChanges);

        if (Host.Output.HasNarrationSink) {
            var total = m_dealChanges.Count;

            foreach (var report in m_dealReports) {
                Host.Output.Narrate(
                    channel: "world.deal",
                    text: (applied
                    ? $"[world.deal: '{report.Template}' dealt {report.Row}: {report.Cells} cell(s) over {report.Offsets} offset(s) — {report.Added} added, {report.Removed} removed, {report.Replaced} replaced{((report.Overflow > 0)
                        ? $", {report.Overflow} without a free offset"
                        : string.Empty)}]"
                    : $"[world.deal: '{report.Template}' dealt {report.Row}: the tick's {total} child mutation(s) were refused as one; the children stay as they were]")
                );
            }
        }


    }
    private void Deal(WorldPlacement template, WorldPlacementDeal deal, int rowOrdinal, int variantOrdinal, DealMemo memo, ulong worldSeed) {
        if (
            !memo.Swept ||
            (memo.WorldSeed != worldSeed) ||
            !ReferenceEquals(
            objA: memo.Distribution,
            objB: template.Distribution
        )
        ) {
            var fixedOffsets = WorldPlacementDeal.Offsets(
                template: template,
                worldSeed: worldSeed
            );

            memo.Offsets = new Vector3[fixedOffsets.Length];

            for (var index = 0; (index < fixedOffsets.Length); index++) {
                memo.Offsets[index] = fixedOffsets[index].ToVector3();
            }

            memo.Distribution = template.Distribution;
            memo.WorldSeed = worldSeed;
        }

        var offsets = memo.Offsets;

        if (m_dealSlotTaken.Length < offsets.Length) {
            m_dealSlotTaken = new bool[offsets.Length];
        }

        Array.Clear(
            array: m_dealSlotTaken,
            index: 0,
            length: offsets.Length
        );
        m_dealChildren.Clear();
        m_dealChildSlots.Clear();
        m_dealChildKept.Clear();
        m_dealPendingCells.Clear();

        var firstMutation = m_dealChanges.Count;

        foreach (var candidate in Host.Document.Definition.Placements) {
            if (!WorldPlacementDeal.IsChild(
                parent: template,
                placement: candidate
            )) {
                continue;
            }

            var slot = (candidate.DealSlot ?? -1);

            if (((uint)slot) >= ((uint)offsets.Length)) { slot = -1; }

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
        if (rowOrdinal >= 0) { while (Host.Arena.TryNextCell(rowOrdinal, ref cursor, out var cellKey)) { m_dealtKeys.Add(Host.Arena.Keys[cellKey]); } }
        var cells = m_dealtKeys;
        var replaced = 0;

        for (var cellIndex = 0; (cellIndex < cells.Count); cellIndex++) {
            var key = cells[cellIndex].Value;
            var childIndex = FindChild(
                template: template.Id,
                key: key
            );

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
            var prototype = ResolveDealPrototype(deal: deal, variantOrdinal: variantOrdinal,
                key: key,
                templatePrototype: template.PrototypeId
            );

            var reconciled = ReconcileChild(
                child: child,
                template: template,
                prototype: prototype,
                offset: offsets[slot],
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
            var slot = FirstFreeSlot(count: offsets.Length);

            if (slot < 0) {
                overflow++;

                continue;
            }

            m_dealSlotTaken[slot] = true;

            var existing = FindChild(
                template: template.Id,
                key: key
            );

            if (existing >= 0) {
                replaced++;
            } else {
                added++;
            }

            var nextChild = ((existing >= 0)
                ? ReconcileChild(
                        m_dealChildren[existing],
                        template,
                        ResolveDealPrototype(deal: deal, variantOrdinal: variantOrdinal,
                            key: key,
                            templatePrototype: template.PrototypeId
            ),
                        offsets[slot],
                        slot
                    )
                : BuildChild(
                        id: ((existing >= 0)
                    ? m_dealChildren[existing].Id
                    : WorldPlacementDeal.ChildId(
                                template: template.Id,
                                key: key
                            )),
                        offset: offsets[slot],
                        slot: slot,
                        prototype: ResolveDealPrototype(deal: deal, variantOrdinal: variantOrdinal,
                            key: key,
                            templatePrototype: template.PrototypeId
            ),
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
                Offsets: offsets.Length,
                Added: added,
                Removed: removed,
                Replaced: replaced,
                Overflow: overflow
            ));
        }

        memo.Template = template;
        memo.Swept = true;
    }
    private string ResolveDealPrototype(WorldPlacementDeal deal, int variantOrdinal, string key, string templatePrototype) {
        if (variantOrdinal < 0 || deal.Variants?.Map is not { } map || !CellName.TryParse(key, out var name, out _) ||
            !Host.Arena.Keys.TryResolve(name, out var cell) || !Host.Arena.TryRead(variantOrdinal, cell, out var value)) { return templatePrototype; }
        if (value.Kind == CellKind.Text) { return map.TryGetValue(value.AsText, out var selected) ? selected : templatePrototype; }
        foreach (var (spelling, prototype) in map) {
            if (long.TryParse(spelling, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw) && raw == value.Raw) { return prototype; }
        }
        return templatePrototype;
    }
    private int FindChild(string template, string key) {
        for (var index = 0; (index < m_dealChildren.Count); index++) {
            if (WorldPlacementDeal.IsChildOf(
                id: m_dealChildren[index].Id,
                template: template,
                key: key.AsSpan()
            )) {
                return index;
            }
        }

        return -1;
    }
    private int FirstFreeSlot(int count) {
        for (var slot = 0; (slot < count); slot++) {
            if (!m_dealSlotTaken[slot]) {
                return slot;
            }
        }

        return -1;
    }
    private static WorldPlacement ReconcileChild(WorldPlacement child, WorldPlacement template, string prototype, Vector3 offset, int slot) {
        var seed = BuildChild(
            template: template,
            id: child.Id,
            prototype: prototype,
            offset: offset,
            slot: slot
        );
        var preserve = template.Deal?.Preserve;
        var result = ((preserve?.Facets == true)
            ? child
            : seed
        );

        return result with {
            Parent = template.Id,
            DealSlot = slot,
            Deal = null,
            Position = ((preserve?.Transform == true)
            ? child.Position
            : seed.Position),
            YawDegrees = ((preserve?.Transform == true)
            ? child.YawDegrees
            : seed.YawDegrees),
            Scale = ((preserve?.Transform == true)
            ? child.Scale
            : seed.Scale),
            PrototypeId = ((preserve?.Prototype == true)
            ? child.PrototypeId
            : prototype),
        };
    }
    private static WorldPlacement BuildChild(WorldPlacement template, string id, string prototype, Vector3 offset, int slot) => new(
        Id: id,
        PrototypeId: prototype,
        Position: offset,
        YawDegrees: 0f,
        Scale: 1f,
        Emission: template.Emission,
        Solid: template.Solid,
        Region: template.Region,
        Grip: template.Grip,
        Parent: template.Id,
        DealSlot: slot,
        Spatial: template.Spatial
    );
}
