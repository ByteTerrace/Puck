using System.Globalization;
using System.Numerics;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // What the last deal of one template read: the template row, its resolved offsets, and a copy of the dealt and
    // variant rows' cells. A tick on which none of it moved skips the template without reading its children.
    private sealed class DealMemo {
        public WorldPlacement? Template;
        public WorldDistribution? Distribution;
        public ulong WorldSeed;
        public Vector3[] Offsets = [];
        public WorldStateRow? Row;
        public string[] Keys = [];
        public string?[] Texts = [];
        public long[] Values = [];
        public WorldStateRow? VariantRow;
        public string[] VariantKeys = [];
        public string?[] VariantTexts = [];
        public long[] VariantValues = [];
        public bool Swept;
    }

    private readonly Dictionary<string, DealMemo> m_dealMemos = new(comparer: StringComparer.Ordinal);
    private readonly List<WorldPlacement> m_dealChildren = [];
    private readonly List<int> m_dealChildSlots = [];
    private readonly List<bool> m_dealChildKept = [];
    private readonly List<int> m_dealPendingCells = [];
    private readonly List<WorldMutation> m_dealMutations = [];
    private bool[] m_dealSlotTaken = [];
    private WorldDefinition? m_dealSweptDefinition;

    /// <summary>Describes every placement row: its prototype, resolved transform, parent, facets, and — for a dealt
    /// template — the row it deals from with the children present against the row's capacity, or — for a dealt
    /// child — the template that dealt it.</summary>
    public string DescribePlacements() {
        var placements = m_definition.Placements;

        if (placements.Count == 0) {
            return "[world.placements: none declared]";
        }

        var lines = new List<string>(capacity: placements.Count);

        foreach (var placement in placements) {
            var frame = WorldDefinitionRows.ResolvedFrame(definition: m_definition, placement: placement);
            var line = string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"'{placement.Id}' prototype={placement.PrototypeId} at ({frame.Position.X:0.##}, {frame.Position.Y:0.##}, {frame.Position.Z:0.##}) yaw {frame.YawDegrees:0.#} scale {placement.Scale:0.##}"
            );

            if (placement.Parent is { } parent) {
                line += $" parent='{parent}'";
            }

            line += DescribeFacets(placement: placement);

            if (placement.Deal is { } deal) {
                var capacity = ((WorldDefinitionRows.FindStateRow(rows: m_definition.State, name: deal.Row) is { } row)
                    ? row.CellCeiling
                    : 0
                );

                line += $" dealt from {deal.Row} ({CountDealtChildren(template: placement)} of {capacity})";
            } else if (
                (placement.Parent is { } templateId) &&
                WorldPlacementDeal.IsChild(placement: placement, parent: WorldDefinitionRows.FindPlacement(id: templateId, placements: placements))
            ) {
                line += $" dealt by {templateId}";
            }

            lines.Add(item: line);
        }

        return $"[world.placements: {placements.Count} row(s); {string.Join(separator: "; ", values: lines)}]";
    }
    private static string DescribeFacets(WorldPlacement placement) {
        var facets = string.Empty;
        if (placement.DealSlot is { } slot) { facets += $" dealSlot={slot}"; }
        if (placement.Footprint is { } footprint) {
            facets += string.Create(CultureInfo.InvariantCulture,
                $" footprint=({footprint.HalfWidth:0.###},{footprint.HalfDepth:0.###}) clearance={footprint.Clearance:0.###} pinned={footprint.Pinned}");
        }
        if (placement.Deal?.Preserve is { } preserve) {
            facets += $" preserve=(transform:{preserve.Transform},prototype:{preserve.Prototype},facets:{preserve.Facets})";
        }
        if (placement.Deal?.Reflow is { } reflow) {
            facets += $" reflow=(work:{reflow.CandidateBudget},costPerMove:{reflow.CostPerMove},payer:{reflow.CostRow ?? "none"})";
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
            facets += (grip.Holdable ? " grip=holdable" : " grip=unholdable");
        }

        if (placement.Region is { } region) {
            facets += string.Create(provider: CultureInfo.InvariantCulture, handler: $" region={region.Radius:0.##}");
        }

        if (placement.Emission is { } emission) {
            facets += $" emission={emission.PatchId}";
        }

        if (placement.Inhabit is { } inhabit) {
            facets += $" inhabit={inhabit.Count}";
        }

        if (placement.Attach is { } attach) {
            facets += $" attach=body:{attach.BodyIndex}";
        }

        if (placement.Respond is { Count: > 0 } respond) {
            facets += $" respond={respond.Count}";
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
    private int CountDealtChildren(WorldPlacement template) {
        var count = 0;

        foreach (var placement in m_definition.Placements) {
            if (WorldPlacementDeal.IsChild(placement: placement, parent: template)) {
                count++;
            }
        }

        return count;
    }

    // Runs once per tick right after SweepPlacementResponses: the rule frame has folded, so a cell a rule wrote
    // this tick deals on this tick's sweep. Nothing here runs, and nothing allocates, on a tick where the installed
    // document is the one the last sweep left — and a template whose dealt row, variant row, and own row are
    // unchanged is skipped before its children are read.
    private void SweepPlacementDeals(ulong tick) {
        if (ReferenceEquals(objA: m_definition, objB: m_dealSweptDefinition)) {
            return;
        }

        var placements = m_definition.Placements;
        var worldSeed = (m_definition.Generation?.WorldSeed ?? 0UL);
        var templates = 0;

        for (var index = 0; (index < placements.Count); index++) {
            var template = placements[index];

            if (template.Deal is not { } deal) {
                continue;
            }

            templates++;

            var row = WorldDefinitionRows.FindStateRow(rows: m_definition.State, name: deal.Row);
            var variantRow = ((deal.Variants is { } variants)
                ? WorldDefinitionRows.FindStateRow(rows: m_definition.State, name: variants.Row)
                : null
            );

            if (!m_dealMemos.TryGetValue(key: template.Id, value: out var memo)) {
                memo = new DealMemo();
                m_dealMemos[template.Id] = memo;
            }

            if (
                memo.Swept &&
                (memo.WorldSeed == worldSeed) &&
                (ReferenceEquals(objA: memo.Template, objB: template) || template.Equals(other: memo.Template)) &&
                RowUnchanged(row: row, memoRow: memo.Row, keys: memo.Keys, texts: memo.Texts, values: memo.Values) &&
                RowUnchanged(row: variantRow, memoRow: memo.VariantRow, keys: memo.VariantKeys, texts: memo.VariantTexts, values: memo.VariantValues)
            ) {
                continue;
            }

            Deal(
                deal: deal,
                memo: memo,
                row: row,
                template: template,
                tick: tick,
                variantRow: variantRow,
                worldSeed: worldSeed
            );
        }

        if (m_dealMemos.Count > templates) {
            foreach (var templateId in m_dealMemos.Keys) {
                if (WorldDefinitionRows.FindPlacement(id: templateId, placements: m_definition.Placements) is not { Deal: not null }) {
                    _ = m_dealMemos.Remove(key: templateId);
                }
            }
        }

        m_dealSweptDefinition = m_definition;
    }
    private static bool RowUnchanged(WorldStateRow? row, WorldStateRow? memoRow, string[] keys, string?[] texts, long[] values) {
        if (ReferenceEquals(objA: row, objB: memoRow)) {
            return true;
        }

        if ((row is null) || (memoRow is null)) {
            return false;
        }

        var cells = (row.Cells ?? []);

        if (cells.Count != keys.Length) {
            return false;
        }

        for (var index = 0; (index < keys.Length); index++) {
            var cell = cells[index];

            if (
                !string.Equals(a: cell.Key.Value, b: keys[index], comparisonType: StringComparison.Ordinal) ||
                !string.Equals(a: cell.Text, b: texts[index], comparisonType: StringComparison.Ordinal) ||
                (cell.Value != values[index])
            ) {
                return false;
            }
        }

        return true;
    }
    private static void Remember(WorldStateRow? row, ref string[] keys, ref string?[] texts, ref long[] values) {
        var cells = (row?.Cells ?? []);

        if (keys.Length != cells.Count) {
            keys = new string[cells.Count];
            texts = new string?[cells.Count];
            values = new long[cells.Count];
        }

        for (var index = 0; (index < cells.Count); index++) {
            keys[index] = cells[index].Key.Value;
            texts[index] = cells[index].Text;
            values[index] = cells[index].Value;
        }
    }
    private void Deal(WorldPlacement template, WorldPlacementDeal deal, WorldStateRow? row, WorldStateRow? variantRow, DealMemo memo, ulong worldSeed, ulong tick) {
        if (
            !memo.Swept ||
            (memo.WorldSeed != worldSeed) ||
            !ReferenceEquals(objA: memo.Distribution, objB: template.Distribution)
        ) {
            var fixedOffsets = WorldPlacementDeal.Offsets(template: template, worldSeed: worldSeed);

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

        Array.Clear(array: m_dealSlotTaken, index: 0, length: offsets.Length);
        m_dealChildren.Clear();
        m_dealChildSlots.Clear();
        m_dealChildKept.Clear();
        m_dealPendingCells.Clear();
        m_dealMutations.Clear();

        foreach (var candidate in m_definition.Placements) {
            if (!WorldPlacementDeal.IsChild(placement: candidate, parent: template)) {
                continue;
            }

            var slot = candidate.DealSlot ?? -1;
            if ((uint)slot >= (uint)offsets.Length) { slot = -1; }

            if ((slot >= 0) && m_dealSlotTaken[slot]) {
                slot = -1;
            }

            if (slot >= 0) {
                m_dealSlotTaken[slot] = true;
            }

            m_dealChildren.Add(item: candidate);
            m_dealChildSlots.Add(item: slot);
            m_dealChildKept.Add(item: false);
        }

        var cells = (row?.Cells ?? []);
        var replaced = 0;

        for (var cellIndex = 0; (cellIndex < cells.Count); cellIndex++) {
            var key = cells[cellIndex].Key.Value;
            var childIndex = FindChild(template: template.Id, key: key);

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
            var prototype = ResolvePrototype(template: template, deal: deal, variantRow: variantRow, key: key);

            var reconciled = ReconcileChild(child: child, template: template, prototype: prototype, offset: offsets[slot], slot: slot);
            if (child == reconciled) {
                continue;
            }

            replaced++;
            m_dealMutations.Add(item: new WorldMutation.UpsertPlacement(
                Placement: reconciled,
                Principal: WorldPrincipal.World
            ));
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
            m_dealMutations.Add(item: new WorldMutation.RemovePlacement(
                Id: m_dealChildren[childIndex].Id,
                Principal: WorldPrincipal.World
            ));
        }

        var added = 0;
        var overflow = 0;

        for (var pending = 0; (pending < m_dealPendingCells.Count); pending++) {
            var key = cells[m_dealPendingCells[pending]].Key.Value;
            var slot = FirstFreeSlot(count: offsets.Length);

            if (slot < 0) {
                overflow++;

                continue;
            }

            m_dealSlotTaken[slot] = true;

            var existing = FindChild(template: template.Id, key: key);

            if (existing >= 0) {
                replaced++;
            } else {
                added++;
            }

            m_dealMutations.Add(item: new WorldMutation.UpsertPlacement(
                Placement: existing >= 0 ? ReconcileChild(m_dealChildren[existing], template,
                    ResolvePrototype(template, deal, variantRow, key), offsets[slot], slot) : BuildChild(
                    id: ((existing >= 0) ? m_dealChildren[existing].Id : WorldPlacementDeal.ChildId(template: template.Id, key: key)),
                    offset: offsets[slot],
                    slot: slot,
                    prototype: ResolvePrototype(template: template, deal: deal, variantRow: variantRow, key: key),
                    template: template
                ),
                Principal: WorldPrincipal.World
            ));
        }

        var applied = true;

        if (m_dealMutations.Count > 0) {
            // WorldPrincipal.World, the same structural-exemption door the response sweep uses, folded into one
            // batch so a deal lands or fails as a unit and undoes as one journal entry.
            var mutation = ((m_dealMutations.Count == 1)
                ? m_dealMutations[0]
                : new WorldMutation.Batch(Principal: WorldPrincipal.World, Mutations: [.. m_dealMutations])
            );

            applied = TryApplyMutation(
                connectionId: SubmissionEnvelope.LocalConnectionId,
                correlationId: 0,
                mutation: mutation,
                preMetered: false,
                tick: tick
            );
        }

        if (m_output.HasNarrationSink && ((m_dealMutations.Count > 0) || (overflow > 0))) {
            var dealId = template.Id;
            var dealRow = deal.Row;
            var dealCells = cells.Count;
            var dealOffsets = offsets.Length;
            var dealAdded = added;
            var dealRemoved = removed;
            var dealReplaced = replaced;
            var dealOverflow = overflow;
            var dealApplied = applied;

            m_output.Narrate(
                channel: "world.deal",
                text: (dealApplied
                    ? $"[world.deal: '{dealId}' dealt {dealRow}: {dealCells} cell(s) over {dealOffsets} offset(s) — {dealAdded} added, {dealRemoved} removed, {dealReplaced} replaced{((dealOverflow > 0) ? $", {dealOverflow} without a free offset" : string.Empty)}]"
                    : $"[world.deal: '{dealId}' dealt {dealRow}: the {dealAdded + dealRemoved + dealReplaced} child mutation(s) were refused as one; the children stay as they were]"
                )
            );
        }

        memo.Template = template;
        memo.Row = row;
        memo.VariantRow = variantRow;
        memo.Swept = true;
        Remember(row: row, keys: ref memo.Keys, texts: ref memo.Texts, values: ref memo.Values);
        Remember(row: variantRow, keys: ref memo.VariantKeys, texts: ref memo.VariantTexts, values: ref memo.VariantValues);
    }
    private int FindChild(string template, string key) {
        for (var index = 0; (index < m_dealChildren.Count); index++) {
            if (WorldPlacementDeal.IsChildOf(id: m_dealChildren[index].Id, template: template, key: key.AsSpan())) {
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
    // The variant row's same-keyed cell selects from the map by its text, or by its integer value spelled as text;
    // no cell, or no entry, deals the template's own prototype.
    private static string ResolvePrototype(WorldPlacement template, WorldPlacementDeal deal, WorldStateRow? variantRow, string key) {
        if (
            (deal.Variants is not { Map: { } map }) ||
            (variantRow?.Cells is not { } cells)
        ) {
            return template.PrototypeId;
        }

        for (var index = 0; (index < cells.Count); index++) {
            var cell = cells[index];

            if (!string.Equals(a: cell.Key.Value, b: key, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            if (cell.Text is { } text) {
                return (map.TryGetValue(key: text, value: out var byText) ? byText : template.PrototypeId);
            }

            foreach (var (spelled, prototype) in map) {
                if (
                    long.TryParse(s: spelled, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var spelledValue) &&
                    (spelledValue == cell.Value)
                ) {
                    return prototype;
                }
            }

            return template.PrototypeId;
        }

        return template.PrototypeId;
    }
    private static WorldPlacement ReconcileChild(WorldPlacement child, WorldPlacement template, string prototype, Vector3 offset, int slot) {
        var seed = BuildChild(template: template, id: child.Id, prototype: prototype, offset: offset, slot: slot);
        var preserve = template.Deal?.Preserve;
        var result = (preserve?.Facets == true ? child : seed);
        return result with {
            Parent = template.Id, DealSlot = slot, Deal = null,
            Position = (preserve?.Transform == true ? child.Position : seed.Position),
            YawDegrees = (preserve?.Transform == true ? child.YawDegrees : seed.YawDegrees),
            Scale = (preserve?.Transform == true ? child.Scale : seed.Scale),
            PrototypeId = (preserve?.Prototype == true ? child.PrototypeId : prototype),
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
        Footprint: template.Footprint
    );
}
