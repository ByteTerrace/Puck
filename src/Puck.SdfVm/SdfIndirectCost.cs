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
/// <summary>A kind's admission unit and its price. The unit is the smallest piece of an item a dispatch can run on its
/// own: a probe's placement, a cell's partition, a ray's trace, a ray's shading or a receiver's proof. Prices are counted
/// visits: field instruction visits and cache record loads. A dispatch spends device time two ways, and a chunk is priced
/// at the larger: its total visits at the device's throughput (<see cref="SdfIndirectCost.DeviceVisitsPerMicrosecond"/>),
/// and its longest lane's visits, which run in sequence however few lanes share the device
/// (<see cref="SdfIndirectCost.ChainLanes"/> lanes' worth of visits per visit of that chain).</summary>
/// <param name="UnitsPerItem">The units in one work item, in their fixed order.</param>
/// <param name="QueriesPerUnit">The counted full-field evaluations one unit performs at most.</param>
/// <param name="FixedUnitCost">Bounded record visits per unit that do not run the field interpreter, priced until the
/// kind is measured.</param>
/// <param name="FixedItemCost">Bounded record visits per touched item, such as a probe's irradiance reduction, priced
/// until the kind is measured.</param>
/// <param name="SplitItemCost">Bounded record visits per item a chunk covers only in part, such as the stored rays a
/// split probe writes and reads back for its reduction, priced until the kind is measured.</param>
/// <param name="MeasuredFieldCost">The visits per unit the device counted for this kind's recent submissions
/// (<see cref="SdfIndirectCache.MeasuredFieldCost"/>), field visits and record loads together, or null before any was
/// counted. It replaces the conservative price and its fixed record visits, never exceeding the conservative unit.</param>
/// <param name="LaneUnits">With <paramref name="LaneDivisor"/>, the share of one unit's visits a single lane runs in
/// sequence: one for a unit a lane runs alone, a probe's rays per lane for shading.</param>
/// <param name="LaneDivisor">The divisor of <paramref name="LaneUnits"/>: the lanes one unit's work is spread over.</param>
public readonly record struct SdfIndirectUnits(int UnitsPerItem, long QueriesPerUnit, long FixedUnitCost = 0, long FixedItemCost = 0, long SplitItemCost = 0,
    long? MeasuredFieldCost = null, int LaneUnits = 1, int LaneDivisor = 1) {
    /// <summary>Gets whether the device has measured this kind, so its fixed record bounds no longer apply.</summary>
    public bool IsMeasured => (MeasuredFieldCost is not null);

    /// <summary>Prices one unit: its measured visits, or every query against the complete field and its fixed record
    /// visits before a measurement exists.</summary>
    /// <param name="instructionCount">The field program's instruction count.</param>
    /// <returns>The unit's visit estimate.</returns>
    public long UnitCost(int instructionCount) {
        var conservative = checked((SdfIndirectCost.EstimateCost(instructionCount: instructionCount, queries: QueriesPerUnit) + FixedUnitCost));

        return ((MeasuredFieldCost is { } measured) ? Math.Clamp(value: measured, min: Math.Min(val1: 1L, val2: conservative), max: conservative) : conservative);
    }
    /// <summary>Prices the visits one lane runs in sequence: its share of a unit.</summary>
    /// <param name="instructionCount">The field program's instruction count.</param>
    /// <returns>The lane's visit estimate, at least one.</returns>
    public long LaneCost(int instructionCount) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LaneDivisor);
        return Math.Max(val1: 1L, val2: (((checked((UnitCost(instructionCount: instructionCount) * LaneUnits)) + LaneDivisor) - 1) / LaneDivisor));
    }
    /// <summary>Prices the device time of a dispatch's longest lane, in visits at the device's throughput: the lane's
    /// sequential visits times <see cref="SdfIndirectCost.ChainLanes"/>, at most one submission's cap
    /// (<see cref="SdfIndirectCost.SubmissionCostLimit"/>). No chunk costs less.</summary>
    /// <param name="instructionCount">The field program's instruction count.</param>
    /// <returns>The chain's visit-equivalent.</returns>
    public long ChainCost(int instructionCount) =>
        Math.Min(val1: SdfIndirectCost.SubmissionCostLimit, val2: checked((LaneCost(instructionCount: instructionCount) * SdfIndirectCost.ChainLanes)));
    /// <summary>Prices the visits of one whole item against the complete field.</summary>
    /// <param name="instructionCount">The field program's instruction count.</param>
    /// <returns>The item's visit estimate.</returns>
    public long ItemCost(int instructionCount) => checked(((UnitsPerItem * UnitCost(instructionCount: instructionCount)) + (IsMeasured ? 0 : FixedItemCost)));
    /// <summary>Prices the visits one chunk performs: its units, and before measurement every item it touches and every
    /// item it covers only in part.</summary>
    /// <param name="chunk">The admitted range.</param>
    /// <param name="instructionCount">The field program's instruction count.</param>
    /// <returns>The chunk's visit estimate.</returns>
    public long VisitsOf(SdfIndirectChunk chunk, int instructionCount) {
        ArgumentNullException.ThrowIfNull(chunk);
        var units = checked((chunk.UnitCount * UnitCost(instructionCount: instructionCount)));

        return (IsMeasured ? units
            : checked(((units + (chunk.Items(unitsPerItem: UnitsPerItem) * FixedItemCost)) + (chunk.PartialItems(unitsPerItem: UnitsPerItem) * SplitItemCost))));
    }
    /// <summary>Prices one chunk's device time in visits at the device's throughput: the larger of its visits and its
    /// longest lane's chain (<see cref="ChainCost"/>).</summary>
    /// <param name="chunk">The admitted range.</param>
    /// <param name="instructionCount">The field program's instruction count.</param>
    /// <returns>The chunk's device-time estimate, in visits.</returns>
    public long CostOf(SdfIndirectChunk chunk, int instructionCount) =>
        Math.Max(val1: VisitsOf(chunk: chunk, instructionCount: instructionCount), val2: ChainCost(instructionCount: instructionCount));
    /// <summary>Gets the visits one chunk may hold under a limit: the limit, or the chain's device time when one lane
    /// alone outlasts it, since a dispatch's other lanes then run beside that lane at no further cost; never more than
    /// <see cref="SdfIndirectCost.SubmissionCostLimit"/>.</summary>
    /// <param name="instructionCount">The field program's instruction count.</param>
    /// <param name="limit">The frame's slice or a submission's cap.</param>
    /// <returns>The chunk's visit allowance.</returns>
    public long ChunkVisits(int instructionCount, long limit) =>
        Math.Min(val1: SdfIndirectCost.SubmissionCostLimit, val2: Math.Max(val1: limit, val2: ChainCost(instructionCount: instructionCount)));
    /// <summary>Names why this kind cannot run within one submission: a single field query, or a single indivisible
    /// unit with its item's fixed cost, exceeds <see cref="SdfIndirectCost.SubmissionCostLimit"/>.</summary>
    /// <param name="instructionCount">The field program's instruction count.</param>
    /// <returns>The refusal, or null when every unit fits.</returns>
    public string? RefusalOf(int instructionCount) {
        var query = SdfIndirectCost.EstimateCost(instructionCount: instructionCount, queries: 1);

        if ((QueriesPerUnit > 0) && (query > SdfIndirectCost.SubmissionCostLimit)) {
            return $"one field query costs {query} instruction visits, exceeding the {SdfIndirectCost.SubmissionCostLimit} submission budget";
        }
        var unit = checked(((UnitCost(instructionCount: instructionCount) + (IsMeasured ? 0 : FixedItemCost)) + (((UnitsPerItem > 1) && !IsMeasured) ? SplitItemCost : 0)));

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
    /// <summary>The calibrated rate at which the device retires counted visits when a dispatch holds lanes enough to hide
    /// their latency. It turns a frame's indirect device time into counted visits. Calibrated on the RTX 2060 floor
    /// desktop; faster devices finish their slice early.</summary>
    public const long DeviceVisitsPerMicrosecond = 1024;
    /// <summary>The visits the device retires beside each visit of one lane's sequential chain: the throughput rate
    /// (<see cref="DeviceVisitsPerMicrosecond"/>) over the rate one lane alone runs its dependent field visits. A
    /// dispatch never finishes before its longest lane, however few lanes it holds, so a chunk costs at least its
    /// longest lane's visits times this. Calibrated on the RTX 2060 floor desktop, where one lane runs well under two
    /// field visits a microsecond.</summary>
    public const long ChainLanes = 1536;

    /// <summary>Gets the counted field visits one produced frame admits for a tier's transport and lighting solve:
    /// its device-time slice (<see cref="SdfIndirectLayout.FrameMicroseconds"/>) at the calibrated device rate.</summary>
    /// <param name="layout">The cache tier.</param>
    /// <returns>The frame's visit budget; zero for the disabled tier.</returns>
    public static long FrameCost(SdfIndirectLayout layout) {
        ArgumentNullException.ThrowIfNull(layout);
        return checked((layout.FrameMicroseconds * DeviceVisitsPerMicrosecond));
    }

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
    /// <summary>Partitions <paramref name="count"/> ordered work items into unit-range chunks that each fit one chunk's
    /// visit allowance and together cover every unit from <paramref name="firstUnit"/> to the last item's last unit
    /// exactly once, in order. The allowance is <paramref name="limit"/>, or the kind's chain cost when one lane alone
    /// outlasts it (<see cref="SdfIndirectUnits.ChunkVisits"/>). Items that fit are admitted whole, as many to a chunk as
    /// the allowance allows, after the rest of an item already begun; an item over it is split into runs of its units, a
    /// run continuing into the next item when the allowance leaves room.</summary>
    /// <param name="count">The scheduled items.</param>
    /// <param name="units">The kind's admission unit and price.</param>
    /// <param name="instructionCount">The complete field program's instruction count.</param>
    /// <param name="firstUnit">The first unit still to admit, counted across items; zero admits every item.</param>
    /// <param name="limit">The device time one chunk may take, in visits: a submission's cap, or a frame's device-time
    /// slice (<see cref="FrameCost"/>). A unit over it still forms a chunk of its own; only a unit over the submission
    /// cap is refused.</param>
    /// <returns>The chunks in submission order; empty when there is nothing to admit.</returns>
    /// <exception cref="SdfIndirectCostRefusedException">A single field query or indivisible unit exceeds the submission cap.</exception>
    public static IReadOnlyList<SdfIndirectChunk> Admit(int count, SdfIndirectUnits units, int instructionCount, int firstUnit = 0, long limit = SubmissionCostLimit) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, SubmissionCostLimit);
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
        var budget = units.ChunkVisits(instructionCount: instructionCount, limit: limit);
        var fixedItem = (units.IsMeasured ? 0 : units.FixedItemCost);
        var splitItem = (units.IsMeasured ? 0 : units.SplitItemCost);
        var chunks = new List<SdfIndirectChunk>();
        var position = firstUnit;

        if (itemCost == 0) { return [new SdfIndirectChunk(ItemFirst: (position / size), UnitCount: (total - position), UnitFirst: (position % size))]; }
        var rest = (((position % size) == 0) ? null : new SdfIndirectChunk(ItemFirst: (position / size), UnitCount: (size - (position % size)), UnitFirst: (position % size)));

        if ((itemCost <= budget) && ((rest is null) || (units.VisitsOf(chunk: rest, instructionCount: instructionCount) <= budget))) {
            var whole = ((int)(budget / itemCost));

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
        // An item over the allowance never fits whole, so a run touches at most its own item and the start of the next. A
        // run holds at least one unit, so a unit over a frame's slice still advances, one unit a chunk.
        while (position < total) {
            var item = (position / size);
            var local = (position % size);
            var room = (size - local);
            var leading = ((local != 0) ? splitItem : 0);
            var within = Math.Max(val1: 1L, val2: (((budget - fixedItem) - splitItem) / unitCost));
            var length = ((within < room) ? within
                : Math.Max(val1: room, val2: Math.Min(val1: ((((budget - (2 * fixedItem)) - leading) - splitItem) / unitCost), val2: ((room + size) - 1))));
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
    /// <param name="limit">The visits one chunk may take, as <see cref="Admit"/> takes it.</param>
    /// <returns>The whole items per chunk, or zero when one item exceeds the cap and is split.</returns>
    public static int WholeItems(int count, SdfIndirectUnits units, int instructionCount, long limit = SubmissionCostLimit) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var itemCost = units.ItemCost(instructionCount: instructionCount);
        var budget = units.ChunkVisits(instructionCount: instructionCount, limit: limit);

        if (itemCost == 0) { return count; }
        return ((itemCost <= budget) ? ((int)Math.Min(val1: count, val2: (budget / itemCost))) : 0);
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
    /// reduction once per touched probe, and the stored rays a split probe writes and reads back. A probe's workgroup
    /// gives each lane every <see cref="ShadeLanes"/>th ray in turn.</summary>
    /// <param name="layout">The cache tier.</param>
    /// <param name="frame">The pinned lighting source.</param>
    /// <returns>The shade pass's admission unit.</returns>
    public static SdfIndirectUnits ShadeUnits(SdfIndirectLayout layout, SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(layout);
        return new(UnitsPerItem: Math.Max(val1: 1, val2: layout.RaysPerProbe), QueriesPerUnit: ShadeRayQueries(frame: frame),
            FixedUnitCost: (8L * layout.RaysPerProbe), FixedItemCost: (((long)layout.RaysPerProbe) * SdfIndirectLayout.IrradianceTexels),
            SplitItemCost: (2L * layout.RaysPerProbe), LaneUnits: Math.Max(val1: 1, val2: (layout.RaysPerProbe / ShadeLanes)));
    }

    /// <summary>The lanes of one probe's shading workgroup (<c>sdf-indirect-shade.comp.hlsl</c>'s group size).</summary>
    public const int ShadeLanes = 64;

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
