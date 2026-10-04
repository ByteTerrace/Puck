using System.Runtime.InteropServices;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private SdfSkipSphere?[] m_indirectDynamicBounds = [];

    // Compare before overwriting the existing CPU shadow. All three rows matter: rotation, participation and lanes
    // can change a field even at the same position. Packed bounds already enclose the permitted orientations/lanes.
    private void MarkIndirectTransform(ReadOnlySpan<float> current, int slot) {
        if (!m_dynamicTransformsPacked || m_indirect is not { } cache || slot >= m_indirectDynamicBounds.Length ||
            m_indirectDynamicBounds[slot] is not { } bound) { return; }
        var bytes = m_dynamicTransformRegion.Contents.Slice(slot * DynamicTransformByteLength, DynamicTransformByteLength);
        if (bytes.SequenceEqual(MemoryMarshal.AsBytes(current))) { return; }
        if (!float.IsFinite(bound.Radius)) {
            var unbounded = new IrradianceSphere(default, double.PositiveInfinity);
            cache.MarkGeometry(unbounded, unbounded);
            return;
        }
        var previous = MemoryMarshal.Cast<byte, float>(bytes);
        var center = Point(bound.Center);
        cache.MarkGeometry(
            new IrradianceSphere(center + new Double3(previous[0], previous[1], previous[2]), bound.Radius),
            new IrradianceSphere(center + new Double3(current[0], current[1], current[2]), bound.Radius));
    }
}
