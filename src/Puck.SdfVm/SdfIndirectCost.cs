using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

/// <summary>One admitted range of a kind's ordered work. Every item holds the same number of admission units in a fixed
/// order, and the range is a contiguous run of those units over the items: it starts at <paramref name="UnitFirst"/>
/// of item <paramref name="ItemFirst"/> and may continue into the following items. A chunk of whole items starts at
/// unit zero and holds a multiple of the item's units. The dispatch runs one workgroup per item the range touches and
/// carries the three values in its pass block, so only the range's units execute.</summary>
/// <param name="ItemFirst">The first item the range touches, counted from the kind's first scheduled item.</param>
/// <param name="UnitFirst">The first admitted unit within that item.</param>
/// <param name="UnitCount">The admitted units, counted across item boundaries.</param>
public sealed record SdfIndirectChunk(int ItemFirst, int UnitFirst, int UnitCount) {
    /// <summary>Gets the number of items the range touches, which is the dispatch's workgroup count.</summary>
    /// <param name="unitsPerItem">The kind's units per item.</param>
    /// <returns>The touched items.</returns>
    public int Items(int unitsPerItem) => checked((int)(((((long)UnitFirst) + UnitCount) + (unitsPerItem - 1)) / unitsPerItem));
    /// <summary>Gets the number of items the range covers only in part.</summary>
    /// <param name="unitsPerItem">The kind's units per item.</param>
    /// <returns>Zero for a chunk of whole items, otherwise one or two.</returns>
    public int PartialItems(int unitsPerItem) {
        var tail = ((((long)UnitFirst) + UnitCount) % unitsPerItem);

        if (Items(unitsPerItem: unitsPerItem) == 1) { return (((UnitFirst != 0) || (tail != 0)) ? 1 : 0); }
        return (((UnitFirst != 0) ? 1 : 0) + ((tail != 0) ? 1 : 0));
    }
    /// <summary>Gets whether the range ends with the last unit of the item it ends in.</summary>
    /// <param name="unitsPerItem">The kind's units per item.</param>
    /// <returns>Whether the range's last item completes in this chunk.</returns>
    public bool EndsItem(int unitsPerItem) => (((((long)UnitFirst) + UnitCount) % unitsPerItem) == 0);
}
/// <summary>A kind's admission unit and its conservative price. The unit is the smallest piece of an item a dispatch can
/// run on its own: a probe's placement, a cell's partition, a ray's trace or a ray's shading.</summary>
/// <param name="UnitsPerItem">The units in one work item, in their fixed order.</param>
/// <param name="QueriesPerUnit">The counted full-field evaluations one unit performs at most.</param>
/// <param name="FixedUnitCost">Bounded record visits per unit that do not run the field interpreter.</param>
/// <param name="FixedItemCost">Bounded record visits per touched item, such as a probe's irradiance reduction.</param>
/// <param name="SplitItemCost">Bounded record visits per item a chunk covers only in part, such as the stored rays a
/// split probe writes and reads back for its reduction.</param>
/// <param name="MeasuredFieldCost">The field instruction visits per unit the device counted for this kind's recent
/// submissions (<see cref="SdfIndirectCache.MeasuredFieldCost"/>), or null before any was counted. It replaces the
/// conservative field price, never exceeding it; the fixed record visits are added either way.</param>
public readonly record struct SdfIndirectUnits(int UnitsPerItem, long QueriesPerUnit, long FixedUnitCost = 0, long FixedItemCost = 0, long SplitItemCost = 0,
    long? MeasuredFieldCost = null) {
    /// <summary>Prices one unit: its measured field visits, or every query against the complete field before a
    /// measurement exists, plus its fixed record visits.</summary>
    /// <param name="instructionCount">The field program's instruction count.</param>
    /// <returns>The unit's instruction-visit estimate.</returns>
    public long UnitCost(int instructionCount) {
        var conservative = SdfIndirectCost.EstimateCost(instructionCount: instructionCount, queries: QueriesPerUnit);
        var field = ((MeasuredFieldCost is { } measured) ? Math.Clamp(value: measured, min: Math.Min(val1: 1L, val2: conservative), max: conservative) : conservative);

        return checked((field + FixedUnitCost));
    }
    /// <summary>Prices one whole item against the complete field.</summary>
    /// <param name="instructionCount">The field program's instruction count.</param>
    /// <returns>The item's instruction-visit estimate.</returns>
    public long ItemCost(int instructionCount) => checked(((UnitsPerItem * UnitCost(instructionCount: instructionCount)) + FixedItemCost));
    /// <summary>Prices one chunk: its units, every item it touches and every item it covers only in part.</summary>
    /// <param name="chunk">The admitted range.</param>
    /// <param name="instructionCount">The field program's instruction count.</param>
    /// <returns>The chunk's instruction-visit estimate.</returns>
    public long CostOf(SdfIndirectChunk chunk, int instructionCount) {
        ArgumentNullException.ThrowIfNull(chunk);
        return checked((((chunk.UnitCount * UnitCost(instructionCount: instructionCount)) + (chunk.Items(unitsPerItem: UnitsPerItem) * FixedItemCost))
            + (chunk.PartialItems(unitsPerItem: UnitsPerItem) * SplitItemCost)));
    }
    /// <summary>Names why this kind cannot run within one submission: a single field query, or a single indivisible
    /// unit with its item's fixed cost, exceeds <see cref="SdfIndirectCost.SubmissionCostLimit"/>.</summary>
    /// <param name="instructionCount">The field program's instruction count.</param>
    /// <returns>The refusal, or null when every unit fits.</returns>
    public string? RefusalOf(int instructionCount) {
        var query = SdfIndirectCost.EstimateCost(instructionCount: instructionCount, queries: 1);

        if ((QueriesPerUnit > 0) && (query > SdfIndirectCost.SubmissionCostLimit)) {
            return $"one field query costs {query} instruction visits, exceeding the {SdfIndirectCost.SubmissionCostLimit} submission budget";
        }
        var unit = checked(((UnitCost(instructionCount: instructionCount) + FixedItemCost) + ((UnitsPerItem > 1) ? SplitItemCost : 0)));

        return ((unit > SdfIndirectCost.SubmissionCostLimit)
            ? $"one indivisible unit of {QueriesPerUnit} queries costs {unit} instruction visits, exceeding the {SdfIndirectCost.SubmissionCostLimit} submission budget"
            : null);
    }
}
/// <summary>Instruction-scaled admission for the existing counted indirect work allowances.</summary>
public static class SdfIndirectCost {
    /// <summary>The conservative instruction-visit allowance per indirect submission. This is an admission estimate,
    /// not a device-independent time guarantee; GPU qualification measures its watchdog margin.</summary>
    public const long SubmissionCostLimit = 67_108_864;
    /// <summary>One stratum's counted field evaluations, including its hit gradient and feedback proof.</summary>
    public const int TraceQueries = IrradianceSchedule.TraceEvaluations;
    /// <summary>One brick's partition evaluations, including every directed cell segment.</summary>
    public const int ClassifyQueries = IrradianceSchedule.ClassifyEvaluations;
    /// <summary>One brick's placement samples and gradients.</summary>
    public const int PlaceQueries = IrradianceSchedule.PlaceEvaluations;
    /// <summary>The shared receiver allowance per admitted receiver: its normal launch, its bin's canonical proof and
    /// its own proof when the canonical record does not reach its point.</summary>
    public const int ReceiverQueries = ((SdfIndirectLayout.LaunchSteps + SdfIndirectLayout.CanonicalProofSteps) + SdfIndirectLayout.ReceiverProofSteps);

    /// <summary>Gets a brick's placement: one unit per probe, each probe's samples and gradients.</summary>
    public static SdfIndirectUnits PlaceUnits { get; } = new(UnitsPerItem: SdfIndirectLayout.ProbesPerBrick, QueriesPerUnit: (PlaceQueries / SdfIndirectLayout.ProbesPerBrick));
    /// <summary>Gets a brick's partition: one unit per cell, each cell's directed segments.</summary>
    public static SdfIndirectUnits ClassifyUnits { get; } = new(UnitsPerItem: SdfIndirectLayout.ProbesPerBrick, QueriesPerUnit: (ClassifyQueries / SdfIndirectLayout.ProbesPerBrick));
    /// <summary>Gets a stratum's trace: one unit per ray, each ray's march, hit gradient and feedback proof.</summary>
    public static SdfIndirectUnits TraceUnits { get; } = new(UnitsPerItem: IrradianceLattice.RaysPerStratum, QueriesPerUnit: (TraceQueries / IrradianceLattice.RaysPerStratum));
    /// <summary>Gets one receiver's launch and proof; a receiver is its own item.</summary>
    public static SdfIndirectUnits ReceiverUnits { get; } = new(UnitsPerItem: 1, QueriesPerUnit: ReceiverQueries);

    /// <summary>Prices counted field evaluations against the entire program, even when masks could skip work.</summary>
    public static long EstimateCost(long queries, int instructionCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(queries);
        ArgumentOutOfRangeException.ThrowIfNegative(instructionCount);
        return checked((queries * Math.Max(val1: 1, val2: instructionCount)));
    }
    /// <summary>Partitions <paramref name="count"/> ordered work items into unit-range chunks that each fit one
    /// submission's instruction-visit cap and together cover every unit from <paramref name="firstUnit"/> to the last
    /// item's last unit exactly once, in order. Items that fit are admitted whole, as many to a chunk as the cap allows,
    /// after the rest of an item already begun; an item over the cap is split into runs of its units, a run continuing
    /// into the next item when the cap leaves room.</summary>
    /// <param name="count">The scheduled items.</param>
    /// <param name="units">The kind's admission unit and price.</param>
    /// <param name="instructionCount">The complete field program's instruction count.</param>
    /// <param name="firstUnit">The first unit still to admit, counted across items; zero admits every item.</param>
    /// <returns>The chunks in submission order; empty when there is nothing to admit.</returns>
    /// <exception cref="SdfIndirectCostRefusedException">A single field query or indivisible unit exceeds the cap.</exception>
    public static IReadOnlyList<SdfIndirectChunk> Admit(int count, SdfIndirectUnits units, int instructionCount, int firstUnit = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(units.UnitsPerItem);
        ArgumentOutOfRangeException.ThrowIfNegative(units.QueriesPerUnit);
        ArgumentOutOfRangeException.ThrowIfNegative(units.FixedUnitCost);
        ArgumentOutOfRangeException.ThrowIfNegative(units.FixedItemCost);
        ArgumentOutOfRangeException.ThrowIfNegative(units.SplitItemCost);
        var size = units.UnitsPerItem;
        var total = checked((count * size));

        ArgumentOutOfRangeException.ThrowIfNegative(firstUnit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstUnit, total);
        if (firstUnit == total) { return []; }
        var unitCost = units.UnitCost(instructionCount: instructionCount);
        var itemCost = units.ItemCost(instructionCount: instructionCount);
        var chunks = new List<SdfIndirectChunk>();
        var position = firstUnit;

        if (itemCost == 0) { return [new SdfIndirectChunk(ItemFirst: (position / size), UnitCount: (total - position), UnitFirst: (position % size))]; }
        var rest = (((position % size) == 0) ? null : new SdfIndirectChunk(ItemFirst: (position / size), UnitCount: (size - (position % size)), UnitFirst: (position % size)));

        if ((itemCost <= SubmissionCostLimit) && ((rest is null) || (units.CostOf(chunk: rest, instructionCount: instructionCount) <= SubmissionCostLimit))) {
            var whole = ((int)(SubmissionCostLimit / itemCost));

            if (rest is not null) {
                chunks.Add(item: rest);
                position += rest.UnitCount;
            }
            for (var item = (position / size); (item < count); item += whole) {
                chunks.Add(item: new SdfIndirectChunk(ItemFirst: item, UnitCount: (Math.Min(val1: whole, val2: (count - item)) * size), UnitFirst: 0));
            }
            return chunks;
        }
        if (units.RefusalOf(instructionCount: instructionCount) is { } refusal) { throw new SdfIndirectCostRefusedException(message: $"Indirect admission refused: {refusal}."); }
        // An item over the cap never fits whole, so a run touches at most its own item and the start of the next.
        while (position < total) {
            var item = (position / size);
            var local = (position % size);
            var room = (size - local);
            var leading = ((local != 0) ? units.SplitItemCost : 0);
            var within = ((((SubmissionCostLimit - units.FixedItemCost) - units.SplitItemCost)) / unitCost);
            var length = ((within < room) ? within
                : Math.Max(val1: room, val2: Math.Min(val1: ((((SubmissionCostLimit - (2 * units.FixedItemCost)) - leading) - units.SplitItemCost) / unitCost), val2: ((room + size) - 1))));
            var admitted = ((int)Math.Min(val1: length, val2: (total - position)));

            chunks.Add(item: new SdfIndirectChunk(ItemFirst: item, UnitCount: admitted, UnitFirst: local));
            position += admitted;
        }
        return chunks;
    }
    /// <summary>Gets the whole items one submission admits: the item count of <see cref="Admit"/>'s first chunk when
    /// items fit, without building the partition.</summary>
    /// <param name="count">The scheduled items.</param>
    /// <param name="units">The kind's admission unit and price.</param>
    /// <param name="instructionCount">The complete field program's instruction count.</param>
    /// <returns>The whole items per chunk, or zero when one item exceeds the cap and is split.</returns>
    public static int WholeItems(int count, SdfIndirectUnits units, int instructionCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var itemCost = units.ItemCost(instructionCount: instructionCount);

        if (itemCost == 0) { return count; }
        return ((itemCost <= SubmissionCostLimit) ? ((int)Math.Min(val1: count, val2: (SubmissionCostLimit / itemCost))) : 0);
    }
    /// <summary>Names why the cache cannot run its tier against this frame's field within one submission, or returns
    /// null. Every kind's chunks are admitted only while a single field query and each indivisible unit fit the cap.</summary>
    /// <param name="layout">The requested tier's layout.</param>
    /// <param name="frame">The frame whose field and lights the cache would price.</param>
    /// <returns>The refusal naming the kind, or null.</returns>
    public static string? RefusalOf(SdfIndirectLayout layout, SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(frame);
        if (layout.Tier == SdfIndirectTier.Off) { return null; }
        var instructions = frame.Program.InstructionCount;

        return (Of(kind: SdfWorldPackage.IndirectPlace, units: PlaceUnits) ?? (Of(kind: SdfWorldPackage.IndirectClassify, units: ClassifyUnits)
            ?? (Of(kind: SdfWorldPackage.IndirectTrace, units: TraceUnits) ?? (Of(kind: "receiver", units: ReceiverUnits)
            ?? Of(kind: SdfWorldPackage.IndirectShade, units: ShadeUnits(frame: frame, layout: layout))))));

        string? Of(string kind, SdfIndirectUnits units) =>
            ((units.RefusalOf(instructionCount: instructions) is { } refusal) ? $"{kind}: {refusal} ({instructions} field instructions)" : null);
    }
    /// <summary>Bounds fallback field evaluations per shaded probe, including duplicate incoming channels.</summary>
    public static int ShadeQueries(SdfIndirectLayout layout, SdfFrame frame) => checked((layout.RaysPerProbe * ShadeRayQueries(frame: frame)));
    /// <summary>Bounds one shaded ray's fallback field evaluations: one bounded visibility march per directional channel.</summary>
    /// <param name="frame">The pinned lighting source.</param>
    /// <returns>Zero when direct light is disabled.</returns>
    public static int ShadeRayQueries(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        if (((frame.IndirectSources & SdfIndirectSources.Direct) == 0) || (frame.IndirectGains.Lights == 0f)) { return 0; }
        return checked((DirectionalChannels(lights: frame.Lights) * SdfIndirectLightLayout.MarchSteps));
    }
    /// <summary>Gets a probe's shading: one unit per ray, with its continuation record visits, the probe's irradiance
    /// reduction once per touched probe, and the stored rays a split probe writes and reads back.</summary>
    /// <param name="layout">The cache tier.</param>
    /// <param name="frame">The pinned lighting source.</param>
    /// <returns>The shade pass's admission unit.</returns>
    public static SdfIndirectUnits ShadeUnits(SdfIndirectLayout layout, SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(layout);
        return new(UnitsPerItem: Math.Max(val1: 1, val2: layout.RaysPerProbe), QueriesPerUnit: ShadeRayQueries(frame: frame),
            FixedUnitCost: (8L * layout.RaysPerProbe), FixedItemCost: (((long)layout.RaysPerProbe) * SdfIndirectLayout.IrradianceTexels),
            SplitItemCost: (2L * layout.RaysPerProbe));
    }
    /// <summary>Counts held and incoming directional channels independently, including duplicates.</summary>
    public static int DirectionalChannels(SdfLights lights) {
        var channels = 0;
        var slots = lights.ShadowSlots;

        for (var slot = 0; (slot < slots.SlotCount); slot++) {
            var index = slots[slot];

            if ((index >= 0) && (index < lights.Count) && (lights[index].Kind == SdfLightKind.Directional)) { channels++; }
        }
        foreach (var handoff in slots.Handoffs) {
            if ((handoff.Incoming >= 0) && (handoff.Incoming < lights.Count) &&
                (lights[handoff.Incoming].Kind == SdfLightKind.Directional)) { channels++; }
        }
        return channels;
    }
    /// <summary>Bounds continuation ray-record visits and the irradiance reduction even with direct lighting disabled.</summary>
    public static long ShadeCacheCost(SdfIndirectLayout layout) => (((long)layout.RaysPerProbe) * ((8 * layout.RaysPerProbe) + SdfIndirectLayout.IrradianceTexels));
}
/// <summary>The named refusal of indirect work whose single field query or indivisible unit exceeds one submission's
/// instruction-visit cap. The residency checks it first and renders that program without indirect lighting.</summary>
/// <param name="message">The refusal, naming the unit's cost and the cap.</param>
public sealed class SdfIndirectCostRefusedException(string message) : InvalidOperationException(message: message);
