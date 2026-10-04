using System.Runtime.InteropServices;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private SdfSkipSphere?[] m_indirectDynamicBounds = [];
    private SdfProgram? m_indirectPolicyProgram;
    private SdfIndirectTier m_indirectPolicyTier;
    private SdfIndirectParticipation m_indirectBodies;
    private ulong m_indirectTransformRevision;

    private void StageIndirectParticipation(SdfFrame frame) {
        if (!Enum.IsDefined(frame.IndirectBodies)) { throw new ArgumentOutOfRangeException(nameof(frame), "IndirectBodies must be Default, Cast, Receive or Off."); }
        var tier = m_indirect?.Layout.Tier ?? SdfIndirectTier.Off;
        var bodies = SdfIndirectPolicy.Resolve(SdfIndirectParticipation.Default, dynamic: true, tier, frame.IndirectBodies);
        if (ReferenceEquals(m_indirectPolicyProgram, frame.Program) && m_indirectPolicyTier == tier && m_indirectBodies == bodies) { return; }
        if (tier != SdfIndirectTier.Off && !frame.Program.IndirectInstancesComposable && frame.Program.Instances.Any(instance =>
            instance.Active && SdfIndirectPolicy.Resolve(instance.Indirect, instance.IsDynamic, tier, bodies) != SdfIndirectParticipation.Cast)) {
            throw new ArgumentException("Indirect Receive/Off requires whole independently composable instances; cross-instance field operations cannot omit an operand.", nameof(frame));
        }
        if (m_indirectPolicyProgram is not null && m_indirect is { } cache && m_indirectBodies != bodies) {
            var all = new IrradianceSphere(default, double.PositiveInfinity);
            cache.MarkGeometry(all, all);
        }
        m_indirectPolicyProgram = frame.Program;
        m_indirectPolicyTier = tier;
        m_indirectBodies = bodies;
        m_indirectDynamicBounds = frame.Program.BuildDynamicTransformBounds(tier, bodies);
    }

    // Compare before overwriting the CPU shadow. Rotation and lanes can change a caster at the same position.
    // Direct-shadow suppression is independent of indirect participation and does not change its transport field.
    private void MarkIndirectTransform(ReadOnlySpan<float> current, int slot) {
        if (!m_dynamicTransformsPacked || m_indirect is not { } cache || slot >= m_indirectDynamicBounds.Length ||
            m_indirectDynamicBounds[slot] is not { } bound) { return; }
        var bytes = m_dynamicTransformRegion.Contents.Slice(slot * DynamicTransformByteLength, DynamicTransformByteLength);
        var previous = MemoryMarshal.Cast<byte, float>(bytes);
        if (previous[..3].SequenceEqual(current[..3]) && previous[4..].SequenceEqual(current[4..])) { return; }
        m_indirectTransformRevision++;
        if (!float.IsFinite(bound.Radius)) {
            var unbounded = new IrradianceSphere(default, double.PositiveInfinity);
            cache.MarkGeometry(unbounded, unbounded);
            return;
        }
        var center = Point(bound.Center);
        cache.MarkGeometry(
            new IrradianceSphere(center + new Double3(previous[0], previous[1], previous[2]), bound.Radius),
            new IrradianceSphere(center + new Double3(current[0], current[1], current[2]), bound.Radius));
    }
}
