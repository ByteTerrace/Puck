namespace Puck.SdfVm;

/// <summary>
/// The closing ramp a probe level's surface visibility applies to the blocked share of its four angular taps: the
/// receiver reads hidden as the blocked share rises from <paramref name="Start"/> over <c>1 / </c><paramref name="Scale"/>.
/// Each level of the volume's table carries its own (<see cref="SdfMeshIrradianceLevel.Coverage"/>), and the views
/// shader reads it from the pass block's level row (<c>sdfMeshProbeCoverage</c>).
/// <para>The tolerant ramp (<see cref="Finest"/>) lets a tap half blocked read visible: a 1 m probe needs it for thin
/// objects, and a coarse probe over open ground needs it because a curved surface's near bulge falls in a minority tap
/// metres wide. The conservative ramp (<see cref="Coarse"/>) closes on almost any measured hit before the receiver, as
/// a level whose probes stand in every storey of a building must, since there such a hit is a slab or a wall.</para>
/// </summary>
/// <param name="Start">The blocked share at which the ramp starts closing, from zero to one.</param>
/// <param name="Scale">The ramp's slope: the ramp closes completely at <paramref name="Start"/> plus its reciprocal.</param>
public readonly record struct SdfMeshProbeCoverage(float Start, float Scale) {
    /// <summary>Gets the tolerant ramp, the finest level's.</summary>
    public static SdfMeshProbeCoverage Finest => new(Scale: SdfMeshIrradianceProbe.DepthCoverageScale, Start: SdfMeshIrradianceProbe.DepthCoverageStart);
    /// <summary>Gets the conservative ramp.</summary>
    public static SdfMeshProbeCoverage Coarse => new(Scale: SdfMeshIrradianceProbe.CoarseDepthCoverageScale, Start: SdfMeshIrradianceProbe.CoarseDepthCoverageStart);

    /// <summary>Returns the visibility a blocked share leaves: one minus the smoothstep of the ramp.</summary>
    /// <param name="blocked">The weighted share of the angular taps that block the receiver, from zero to one.</param>
    /// <returns>The visibility, from zero to one.</returns>
    public float Visibility(float blocked) {
        var coverage = Math.Clamp(max: 1f, min: 0f, value: ((blocked - Start) * Scale));

        return (1f - ((coverage * coverage) * (3f - (2f * coverage))));
    }
}
