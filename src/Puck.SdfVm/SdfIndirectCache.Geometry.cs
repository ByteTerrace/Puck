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
        var change = Cover(previous, current);
        m_changedGeometry = m_changedGeometry is { } pending ? Cover(pending, change) : change;
    }

    private void ApplyGeometryChanges() {
        if (m_changedGeometry is not { } change) { return; }
        var changed = m_schedule.MarkGeometry(change, change);
        m_changedGeometry = null;
        if (changed.Count == 0) { return; }
        CertificateRevision = checked(CertificateRevision + 1u);
        InvalidateLighting();
        foreach (var key in changed) {
            m_placed.Remove(key);
            Array.Clear(m_traceStates, m_slots[key] * SdfIndirectLayout.ProbesPerBrick, SdfIndirectLayout.ProbesPerBrick);
        }
        Regions[3].Write(offset: 0, bytes: MemoryMarshal.AsBytes(m_traceStates.AsSpan()));
    }

    private static IrradianceSphere Cover(IrradianceSphere first, IrradianceSphere second) {
        if (double.IsPositiveInfinity(first.Radius) || double.IsPositiveInfinity(second.Radius)) {
            return new(default, double.PositiveInfinity);
        }
        var delta = second.Center - first.Center;
        var distance = delta.Length;
        if (distance + second.Radius <= first.Radius) { return first; }
        if (distance + first.Radius <= second.Radius) { return second; }
        var radius = (distance + first.Radius + second.Radius) * 0.5;
        var center = first.Center + delta * ((radius - first.Radius) / distance);
        // Include the represented centre's rounding error in the final conservative radius.
        radius = Math.Max((center - first.Center).Length + first.Radius, (center - second.Center).Length + second.Radius);
        return new(center, Math.BitIncrement(radius));
    }
}
