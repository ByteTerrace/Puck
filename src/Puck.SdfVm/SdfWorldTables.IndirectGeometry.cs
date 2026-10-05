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
        if (!Enum.IsDefined(value: frame.IndirectBodies)) { throw new ArgumentOutOfRangeException(nameof(frame), "IndirectBodies must be Default, Cast, Receive or Off."); }
        var tier = (m_indirect?.Layout.Tier ?? SdfIndirectTier.Off);
        var bodies = SdfIndirectPolicy.Resolve(SdfIndirectParticipation.Default, dynamic: true, tier, frame.IndirectBodies);

        if (ReferenceEquals(objA: m_indirectPolicyProgram, objB: frame.Program) && (m_indirectPolicyTier == tier) && (m_indirectBodies == bodies)) { return; }
        if ((tier != SdfIndirectTier.Off) && !frame.Program.IndirectInstancesComposable && frame.Program.Instances.Any(predicate: instance =>
            (instance.Active && (SdfIndirectPolicy.Resolve(instance.Indirect, instance.IsDynamic, tier, bodies) != SdfIndirectParticipation.Cast)))) {
            throw new ArgumentException(message: "Indirect Receive/Off requires whole independently composable instances; cross-instance field operations cannot omit an operand.", paramName: nameof(frame));
        }
        if ((m_indirectPolicyProgram is not null) && (m_indirect is { } cache) && (m_indirectBodies != bodies)) {
            var all = new IrradianceSphere(Center: default, Radius: double.PositiveInfinity);

            cache.MarkGeometry(current: all, previous: all);
        }
        m_indirectPolicyProgram = frame.Program;
        m_indirectPolicyTier = tier;
        m_indirectBodies = bodies;
        m_indirectDynamicBounds = frame.Program.BuildDynamicTransformBounds(bodies: bodies, indirectTier: tier);
    }
    // Compare before overwriting the CPU shadow. Rotation and lanes can change a caster at the same position.
    // Direct-shadow suppression is independent of indirect participation and does not change its transport field.
    private void MarkIndirectTransform(ReadOnlySpan<float> current, int slot) {
        if (!m_dynamicTransformsPacked || (m_indirect is not { } cache) || (slot >= m_indirectDynamicBounds.Length) ||
            (m_indirectDynamicBounds[slot] is not { } bound)) { return; }
        var bytes = m_dynamicTransformRegion.Contents.Slice(length: DynamicTransformByteLength, start: (slot * DynamicTransformByteLength));
        var previous = MemoryMarshal.Cast<byte, float>(span: bytes);

        if (previous[..3].SequenceEqual(other: current[..3]) && previous[4..].SequenceEqual(other: current[4..])) { return; }
        m_indirectTransformRevision++;
        if (!float.IsFinite(f: bound.Radius)) {
            var unbounded = new IrradianceSphere(Center: default, Radius: double.PositiveInfinity);

            cache.MarkGeometry(current: unbounded, previous: unbounded);
            return;
        }
        var center = Point(value: bound.Center);

        cache.MarkGeometry(
            new IrradianceSphere(Center: (center + new Double3(X: previous[0], Y: previous[1], Z: previous[2])), Radius: bound.Radius),
            new IrradianceSphere(Center: (center + new Double3(X: current[0], Y: current[1], Z: current[2])), Radius: bound.Radius));
    }
}
