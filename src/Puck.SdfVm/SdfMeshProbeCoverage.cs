namespace Puck.SdfVm;

/// <summary>
/// The closing rule a probe's surface visibility applies to the blocked share of its four angular taps. At a coarser
/// level, whose trace marks sheets (<see cref="SdfMeshIrradianceLevel.Sheets"/>), the taps that hit a solid's near side
/// close the receiver only once they are the majority, and the taps that hit a sheet close it at almost any share. At the
/// finest level, which marks no sheets, the taps on solids close it smoothly over their whole blocked share. The views
/// shader applies the same rule (<c>sdfMeshProbeCoverageVisibility</c>).
/// <para>A coarse probe's taps are metres wide: a minority tap on a solid is a hill's near bulge in front of the slope it
/// sees, so it must not extend its block over the footprint, while a tap on a sheet
/// (<see cref="SdfMeshIrradianceProbe.DepthSheetThickness"/>, marked by the trace) measured a slab or a wall before the
/// receiver, with another space beyond it: a coarse probe whose taps straddle a slab otherwise lights a storey with the
/// room above it. A finest probe's taps resolve its room's walls and furnishings; its visibility follows the share of
/// its footprint they block, so a receiver passing a furnishing's edge as the probe sees it changes that probe's weight
/// across a texel rather than at a line.</para>
/// </summary>
public static class SdfMeshProbeCoverage {
    /// <summary>Returns the visibility the taps' blocked shares leave.</summary>
    /// <param name="blocked">The weighted share of the angular taps that block the receiver, sheets included, from zero
    /// to one.</param>
    /// <param name="sheets">The weighted share of the angular taps on sheets that block the receiver, from zero to
    /// <paramref name="blocked"/>.</param>
    /// <param name="finest">Whether the probe belongs to the volume's finest level.</param>
    /// <returns>The visibility, from zero to one.</returns>
    public static float Visibility(float blocked, float sheets, bool finest) =>
        (Ramp(blocked: blocked,
                scale: (finest ? SdfMeshIrradianceProbe.DepthFinestCoverageScale : SdfMeshIrradianceProbe.DepthCoverageScale),
                start: (finest ? SdfMeshIrradianceProbe.DepthFinestCoverageStart : SdfMeshIrradianceProbe.DepthCoverageStart))
            * Ramp(blocked: sheets, scale: SdfMeshIrradianceProbe.DepthSheetCoverageScale, start: SdfMeshIrradianceProbe.DepthSheetCoverageStart));

    // One minus the smoothstep of a share over a ramp from start, over one over scale.
    private static float Ramp(float blocked, float start, float scale) {
        var coverage = Math.Clamp(max: 1f, min: 0f, value: ((blocked - start) * scale));

        return (1f - ((coverage * coverage) * (3f - (2f * coverage))));
    }
}
