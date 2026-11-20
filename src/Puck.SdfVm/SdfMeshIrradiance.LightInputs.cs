using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    // Shared by every batch lit from this capture. A cached probe need not keep the program or its scene alive.
    private sealed record LightInputs(SdfLight[] Directions, MultisetDigest Roles, Vector3[] Sky, SdfIndirectLightingGrid Grid) {
        public float ChordSquared { get; } = MathF.Pow(2f * MathF.Sin(float.DegreesToRadians(Grid.DirectionDegrees) * .5f), 2);
    }
    // Three adjacent octahedral texels share a bit. A collision can schedule extra work only.
    private static ulong SkyGroup(int ray) => 1UL << (ray / ((SdfMeshProbeDepth.HitCount + 63) / 64));

    private enum InputChange { None, WithinBound, Due }

    // Compare captures once per distinct version in the listing. The angular displacement is a chord on the unit
    // sphere, so multiplying it by a probe's recorded reach gives the displacement at the end of that ray.
    private readonly record struct LightDelta(float DirectionSquared, float ShadowSquared, float EnergyShare, ulong SkyChanged, ulong SkyDue) {
        public static LightDelta Between(LightInputs before, LightInputs after) {
            var direction = 0f;
            var shadow = 0f;
            var energy = 0f;
            for (var i = 0; i < after.Directions.Length; i++) {
                var a = before.Directions[i];
                var b = after.Directions[i];
                var chord = Vector3.DistanceSquared(a.Direction, b.Direction);
                direction = MathF.Max(direction, chord);
                if (b.CastsShadow) { shadow = MathF.Max(shadow, chord); }
                var oldEnergy = a.Color * (a.Weight * a.Bounce);
                var newEnergy = b.Color * (b.Weight * b.Bounce);
                var difference = Vector3.Abs(newEnergy - oldEnergy);
                if (a.Color != b.Color || a.Bounce != b.Bounce || a.Weight != b.Weight) { energy = MathF.Max(energy, float.Epsilon); }
                var scale = Vector3.Max(Vector3.Abs(oldEnergy), Vector3.Abs(newEnergy));
                for (var channel = 0; channel < 3; channel++) {
                    if (scale[channel] > 0f) { energy = MathF.Max(energy, difference[channel] / scale[channel]); }
                }
            }
            ulong changed = 0, due = 0;
            for (var ray = 0; ray < after.Sky.Length; ray++) {
                var a = before.Sky[ray];
                var b = after.Sky[ray];
                if (a == b) { continue; }
                var bit = SkyGroup(ray);
                changed |= bit;
                // Positive quadrature weights preserve a per-channel relative bound over any subset of open rays.
                var bound = Vector3.Abs(a) * after.Grid.Radiance;
                var difference = Vector3.Abs(b - a);
                if (difference.X > bound.X || difference.Y > bound.Y || difference.Z > bound.Z) { due |= bit; }
            }
            return new(direction, shadow, energy, changed, due);
        }
    }

    private readonly Dictionary<LightInputs, LightDelta> m_inputChanges = new(ReferenceEqualityComparer.Instance);

    private static InputChange ChangeOf(in ProbeData probe, Lighting lighting, Vector3 spacing, Vector3 center, SdfMeshIrradianceScene scene, Dictionary<LightInputs, LightDelta> changes) {
        if (probe.Shading is not { } block) { return InputChange.Due; }
        if (probe.Invalid || probe.Lighting == lighting.Version) { return InputChange.None; }
        var before = block.Inputs;
        var after = lighting.Inputs;
        if (probe.LocalVersion != lighting.LocalVersion || before.Roles != after.Roles || before.Grid != after.Grid) { return InputChange.Due; }
        if (!changes.TryGetValue(before, out var delta)) {
            changes.Add(before, delta = LightDelta.Between(before, after));
        }
        var chord = (probe.ReceivesSurface && lighting.Direct) ? delta.DirectionSquared : delta.ShadowSquared;
        if (chord > 0f && !(probe.ReceivesSurface && lighting.Direct)) {
            // Only the centre's visibility bits can read this motion. An already-visible light whose new ray
            // points out of the scene keeps its bit exactly, including in a converged capture.
            chord = 0f;
            for (var i = 0; i < after.Directions.Length; i++) {
                var light = after.Directions[i];
                var index = lighting.LightIndices[i];
                if (!light.CastsShadow || light.Direction == before.Directions[i].Direction || index >= 32 * SdfMeshIrradianceProbe.LightWords) { continue; }
                var visible = (probe.Lights[index >> 5] & (1u << (index & 31))) != 0;
                if (!visible || !scene.PointsOutside(center + probe.Offset, light.Direction)) {
                    chord = MathF.Max(chord, Vector3.DistanceSquared(before.Directions[i].Direction, light.Direction));
                }
            }
        }
        var energy = (probe.ReceivesSurface && lighting.Direct) ? delta.EnergyShare : 0f;
        var axisSpacing = MathF.Min(spacing.X, MathF.Min(spacing.Y, spacing.Z));
        if (chord > after.ChordSquared || chord * probe.Reach * probe.Reach > axisSpacing * axisSpacing || energy > after.Grid.Radiance) { return InputChange.Due; }
        if ((delta.SkyDue & probe.OpenSkyGroups) != 0) { return InputChange.Due; }
        var moved = chord > 0f || energy > 0f || (delta.SkyChanged & probe.OpenSkyGroups) != 0;
        return moved ? InputChange.WithinBound : InputChange.None;
    }

    // A below-bound change can wait only until its level's age deadline. Remember the earliest deadline so a sun
    // that stops between two samples still receives its final relight without spinning or listing each frame.
    private long m_nextLightCheck;
    private long m_listingLightCheck;
    private bool OwesRelight(in ProbeData probe, InputChange change, in Snapshot snapshot, bool drain, bool interleave, int level, float age, float bound, int phase, long now, ref bool deferred) {
        if (!InputsDue(in probe, change, in snapshot, drain, age, bound, phase, now)) { return false; }
        if (drain || !interleave || SdfMeshProbeRelight.RelightDue(m_selectionSweep, phase, SdfMeshProbeRelight.InterleaveOf(level), age, bound)) { return true; }
        deferred = true;
        return false;
    }

    private bool InputsDue(in ProbeData probe, InputChange change, in Snapshot snapshot, bool drain, float age, float bound, int phase, long now) {
        if (change == InputChange.None) {
            return probe.ReceivesSurface && snapshot.Lighting.Feedback && snapshot.Layout.Bounce && (drain || probe.Sweep != snapshot.Sweep);
        }
        if (change == InputChange.Due || drain) { return true; }
        // Distribute small updates over the eight existing cell phases before the hard age deadline.
        var release = bound * (phase / 8f);
        if (!(age < release)) { return true; }
        var delay = Math.Ceiling(((double)release - age) * Time.TimestampFrequency);
        var wake = now + (long)Math.Clamp(delay, 1, (double)(long.MaxValue - now) / 2);
        if (m_listingLightCheck == 0 || wake < m_listingLightCheck) { m_listingLightCheck = wake; }
        return false;
    }
}
