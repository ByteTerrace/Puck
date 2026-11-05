namespace Puck.SdfVm;

/// <summary>
/// The closing ramp a probe level's surface visibility applies to the blocked share of its four angular taps: the
/// receiver reads hidden as the blocked share rises from <paramref name="Start"/> over <c>1 / </c><paramref name="Scale"/>.
/// The finest level's ramp lets a tap half blocked read visible, which a 1 m probe needs for thin objects; a coarser
/// level's closes on almost any measured hit before the receiver, since its taps are metres wide and such a hit is a
/// slab or a wall. The views shader chooses the same ramp by level (<c>sdfMeshProbesAt</c>).
/// </summary>
/// <param name="Start">The blocked share at which the ramp starts closing, from zero to one.</param>
/// <param name="Scale">The ramp's slope: the ramp closes completely at <paramref name="Start"/> plus its reciprocal.</param>
public readonly record struct SdfMeshProbeCoverage(float Start, float Scale) {
    /// <summary>Gets the finest level's ramp.</summary>
    public static SdfMeshProbeCoverage Finest => new(Scale: SdfMeshIrradianceProbe.DepthCoverageScale, Start: SdfMeshIrradianceProbe.DepthCoverageStart);
    /// <summary>Gets every coarser level's ramp.</summary>
    public static SdfMeshProbeCoverage Coarse => new(Scale: SdfMeshIrradianceProbe.CoarseDepthCoverageScale, Start: SdfMeshIrradianceProbe.CoarseDepthCoverageStart);

    /// <summary>Returns a level's ramp.</summary>
    /// <param name="level">The volume level, zero for the finest.</param>
    /// <returns><see cref="Finest"/> for level zero, else <see cref="Coarse"/>.</returns>
    public static SdfMeshProbeCoverage Of(int level) => ((level == 0) ? Finest : Coarse);

    /// <summary>Returns the visibility a blocked share leaves: one minus the smoothstep of the ramp.</summary>
    /// <param name="blocked">The weighted share of the angular taps that block the receiver, from zero to one.</param>
    /// <returns>The visibility, from zero to one.</returns>
    public float Visibility(float blocked) {
        var coverage = Math.Clamp(max: 1f, min: 0f, value: ((blocked - Start) * Scale));

        return (1f - ((coverage * coverage) * (3f - (2f * coverage))));
    }
}
