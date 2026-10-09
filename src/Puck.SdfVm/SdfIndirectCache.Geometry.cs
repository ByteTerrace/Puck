using System.Runtime.InteropServices;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

// A geometry change withdraws only the transport it can reach. A moving caster of a program whose root operands are
// independently unioned reaches only near transport: past its first, near segment a probe's ray sees the static far
// field (the trace kernel's sdfIndirectStaticField), and the coarsest level sees it throughout, so such a change dirties
// the bricks whose probes lie within their level's near reach of it (IrradianceSchedule.NearReach). Every other change,
// a program's or a coupled field's, reaches the far distance. Changes are kept apart, one covering sphere per coarse
// cell, so bodies moving in separate places never merge into one sphere that covers the world. Changes queue until the
// current cycle completes, its transport traced and its finite lighting solve published, and the next plan applies them
// all at once: static transport stands, the transport they reach is traced once per cycle rather than once per frame, and
// every cycle publishes while bodies move.
public sealed partial class SdfIndirectCache {
    // The side of the coarse cells one queued change covers per kind of reach, in world units.
    private const double ChangeCell = 8.0;

    private readonly Dictionary<(long X, long Y, long Z, bool Near), IrradianceSphere> m_changedGeometry = [];

    /// <summary>Gets whether moving casters of this program reach only near transport: its root operands are
    /// independently unioned, so the static far field can omit a moving operand without changing any other.</summary>
    /// <param name="program">The transported program.</param>
    /// <returns>Whether the static far field applies.</returns>
    public static bool StaticFarField(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(program);
        return program.IndirectInstancesComposable;
    }

    /// <summary>Gets whether geometry changes are queued for the next plan.</summary>
    public bool HasQueuedGeometry => (m_changedGeometry.Count != 0);

    /// <summary>Queues old and new casting bounds for the existing transport schedule. An admitted batch and a running
    /// lighting solve may finish; the next plan withdraws the transport the change can reach.</summary>
    /// <param name="previous">The complete geometry bound before the change.</param>
    /// <param name="current">The complete geometry bound after the change.</param>
    /// <param name="near">Whether only near transport can see the change: a moving caster under the static far field.</param>
    public void MarkGeometry(IrradianceSphere previous, IrradianceSphere current, bool near = false) {
        Queue(near: near, sphere: previous);
        Queue(near: near, sphere: current);
    }

    private void Queue(IrradianceSphere sphere, bool near) {
        var finite = (double.IsFinite(d: sphere.Radius) && double.IsFinite(d: sphere.Center.X) && double.IsFinite(d: sphere.Center.Y) &&
            double.IsFinite(d: sphere.Center.Z));
        var key = (finite
            ? (X: ((long)Math.Floor(d: (sphere.Center.X / ChangeCell))), Y: ((long)Math.Floor(d: (sphere.Center.Y / ChangeCell))),
                Z: ((long)Math.Floor(d: (sphere.Center.Z / ChangeCell))), Near: near)
            : (X: long.MinValue, Y: long.MinValue, Z: long.MinValue, Near: false));
        var change = (finite ? sphere : new IrradianceSphere(Center: default, Radius: double.PositiveInfinity));

        m_changedGeometry[key] = (m_changedGeometry.TryGetValue(key: key, value: out var queued) ? Cover(first: queued, second: change) : change);
    }
    private void ApplyGeometryChanges() {
        // A cycle completes before the next begins: the current transport is traced and its finite solve published.
        if ((m_changedGeometry.Count == 0) || !m_schedule.IsComplete || (m_solve is not { IsComplete: true })) { return; }
        var changes = m_changedGeometry.Select(selector: static entry => new IrradianceGeometryChange(Near: entry.Key.Near, Sphere: entry.Value)).ToArray();

        m_changedGeometry.Clear();
        var changed = m_schedule.MarkGeometry(changes: changes);

        if (changed.Count == 0) { return; }
        CountInvalidation();
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
