using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

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
    /// <summary>The shared receiver launch and proof allowance per admitted receiver.</summary>
    public const int ReceiverQueries = SdfIndirectLayout.FeedbackSteps;

    /// <summary>Prices counted field evaluations against the entire program, even when masks could skip work.</summary>
    public static long EstimateCost(long queries, int instructionCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(queries);
        ArgumentOutOfRangeException.ThrowIfNegative(instructionCount);
        return checked((queries * Math.Max(val1: 1, val2: instructionCount)));
    }
    /// <summary>Admits whole work items within both the existing count cap and the instruction-visit cap.
    /// An indivisible item exceeding the cap is refused before recording, never silently admitted over budget.</summary>
    public static int Admit(int count, long queriesPerItem, int instructionCount, long fixedItemCost = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(fixedItemCost);
        var cost = checked((EstimateCost(instructionCount: instructionCount, queries: queriesPerItem) + fixedItemCost));

        if ((count == 0) || (cost == 0)) { return count; }
        if (cost > SubmissionCostLimit) {
            throw new InvalidOperationException(message: $"An indirect work item costs {cost} instruction visits, exceeding the {SubmissionCostLimit} submission budget ({queriesPerItem} queries, {instructionCount} field instructions).");
        }
        return ((int)Math.Min(val1: count, val2: (SubmissionCostLimit / cost)));
    }
    /// <summary>Bounds fallback field evaluations per shaded probe, including duplicate incoming channels.</summary>
    public static int ShadeQueries(SdfIndirectLayout layout, SdfFrame frame) {
        if (((frame.IndirectSources & SdfIndirectSources.Direct) == 0) || (frame.IndirectGains.Lights == 0f)) { return 0; }
        return checked(((layout.RaysPerProbe * DirectionalChannels(lights: frame.Lights)) * SdfIndirectLightLayout.MarchSteps));
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
