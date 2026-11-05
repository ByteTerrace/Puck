namespace Puck.SdfVm;

/// <summary>
/// Which stages of the irradiance gather's chain answer a receiver, as <see cref="SdfMeshIrradiance.TryGather(System.Numerics.Vector3, System.Numerics.Vector3, System.Span{System.ValueTuple{int, int, int}}, System.Span{float}, System.Span{SdfMeshProbeReport}, out SdfMeshProbeSelection)"/>
/// reads them and the views shader does (<c>sdfMeshProbesAt</c>): the chosen stage, the first whose probes answer, in
/// the share of the receiver's front support they measured clear, and the remainder stage, the next over another cage,
/// in the rest.
/// </summary>
/// <param name="Level">The chosen stage's level; zero where no stage answers.</param>
/// <param name="Measured">Whether the chosen stage read its probes bounded by their measured reach.</param>
/// <param name="Share">The chosen stage's share of the answer, from zero to one: its corners' weights over the same
/// cage's front weight before visibility; one where no remainder stage answers, zero where no stage answers.</param>
/// <param name="RemainderLevel">The remainder stage's level, or -1 where none answers.</param>
/// <param name="RemainderMeasured">Whether the remainder stage read its probes bounded by their measured reach.</param>
public readonly record struct SdfMeshProbeSelection(int Level, bool Measured, float Share, int RemainderLevel = -1, bool RemainderMeasured = false) {
    /// <summary>Gets the share of the answer read bounded by the probes' measured reach, which a traced light's
    /// visibility scales with.</summary>
    public float MeasuredShare => ((Measured ? Share : 0f) + ((RemainderMeasured && (RemainderLevel >= 0)) ? (1f - Share) : 0f));
}
