using System.Runtime.InteropServices;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

public sealed partial class SdfIndirectCache {
    private IrradianceSphere? m_changedGeometry;

    /// <summary>Queues old and new casting bounds for the existing transport schedule. An admitted batch may finish;
    /// frozen motion coalesces into one conservative sphere until update admission resumes.</summary>
    /// <param name="previous">The complete geometry bound before the change.</param>
    /// <param name="current">The complete geometry bound after the change.</param>
    public void MarkGeometry(IrradianceSphere previous, IrradianceSphere current) {
        var change = Cover(first: previous, second: current);

        m_changedGeometry = ((m_changedGeometry is { } pending) ? Cover(first: pending, second: change) : change);
    }

    private void ApplyGeometryChanges() {
        if (m_changedGeometry is not { } change) { return; }
        var changed = m_schedule.MarkGeometry(current: change, previous: change);

        m_changedGeometry = null;
        if (changed.Count == 0) { return; }
        CertificateRevision = checked((CertificateRevision + 1u));
        InvalidateLighting();
        foreach (var key in changed) {
            m_placed.Remove(item: key);
            Array.Clear(array: m_traceStates, index: (m_slots[key] * SdfIndirectLayout.ProbesPerBrick), length: SdfIndirectLayout.ProbesPerBrick);
        }
        Regions[3].Write(offset: 0, bytes: MemoryMarshal.AsBytes(span: m_traceStates.AsSpan()));
    }
    private static IrradianceSphere Cover(IrradianceSphere first, IrradianceSphere second) {
        if (double.IsPositiveInfinity(d: first.Radius) || double.IsPositiveInfinity(d: second.Radius)) {
            return new(Center: default, Radius: double.PositiveInfinity);
        }
        var delta = (second.Center - first.Center);
        var distance = delta.Length;

        if ((distance + second.Radius) <= first.Radius) { return first; }
        if ((distance + first.Radius) <= second.Radius) { return second; }
        var radius = (((distance + first.Radius) + second.Radius) * 0.5);
        var center = (first.Center + (delta * ((radius - first.Radius) / distance)));
        // Include the represented centre's rounding error in the final conservative radius.
        radius = Math.Max(val1: ((center - first.Center).Length + first.Radius), val2: ((center - second.Center).Length + second.Radius));
        return new(Center: center, Radius: Math.BitIncrement(x: radius));
    }
}
