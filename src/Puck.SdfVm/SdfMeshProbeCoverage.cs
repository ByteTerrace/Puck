namespace Puck.SdfVm;

/// <summary>
/// The closing rule a probe's surface visibility applies to the blocked share of its four angular taps, the same at
/// every level of the volume: the taps that hit a solid's near side close the receiver only once they are the majority,
/// and the taps that hit a sheet close it at almost any share. The views shader applies the same rule
/// (<c>sdfMeshProbeCoverageVisibility</c>); a level whose trace marks no sheets
/// (<see cref="SdfMeshIrradianceLevel.Sheets"/>, the finest) closes by the solids' ramp alone.
/// <para>A minority tap on a solid is a finite blocker beside the receiver's segment or, for a coarse probe whose taps are
/// metres wide, a hill's near bulge in front of the slope it sees, so it must not extend its block over the footprint. A
/// tap on a sheet (<see cref="SdfMeshIrradianceProbe.DepthSheetThickness"/>, marked by the trace) measured a slab or a
/// wall before the receiver, with another space beyond it: a coarse probe whose taps straddle a slab otherwise lights a
/// storey with the room above it.</para>
/// </summary>
public static class SdfMeshProbeCoverage {
    /// <summary>Returns the visibility the taps' blocked shares leave.</summary>
    /// <param name="blocked">The weighted share of the angular taps that block the receiver, sheets included, from zero
    /// to one.</param>
    /// <param name="sheets">The weighted share of the angular taps on sheets that block the receiver, from zero to
    /// <paramref name="blocked"/>.</param>
    /// <returns>The visibility, from zero to one.</returns>
    public static float Visibility(float blocked, float sheets) =>
        (Ramp(blocked: blocked, scale: SdfMeshIrradianceProbe.DepthCoverageScale, start: SdfMeshIrradianceProbe.DepthCoverageStart)
            * Ramp(blocked: sheets, scale: SdfMeshIrradianceProbe.DepthSheetCoverageScale, start: SdfMeshIrradianceProbe.DepthSheetCoverageStart));

    // One minus the smoothstep of a share over a ramp from start, over one over scale.
    private static float Ramp(float blocked, float start, float scale) {
        var coverage = Math.Clamp(max: 1f, min: 0f, value: ((blocked - start) * scale));

        return (1f - ((coverage * coverage) * (3f - (2f * coverage))));
    }
}
