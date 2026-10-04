using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Runs the existing independent CPU irradiance reference once for an explicitly requested fenced pick.
/// It reads that publication's source and depth; it never samples a later live frame or infers GPU classifications.</summary>
public static class WorldIndirectReference {
    /// <summary>Estimates a captured cache receiver or exact sampled Near incoming ray with independent finite paths.</summary>
    /// <param name="pick">The actual fenced receiver answer.</param>
    /// <param name="paths">The bounded path count, from one through 256; the default is 64.</param>
    /// <returns>The estimate and work, or a named unsupported source without numeric divergence.</returns>
    /// <exception cref="ArgumentNullException">The pick is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The path count is outside its bounded range.</exception>
    public static WorldIndirectReferenceResult Evaluate(SdfIndirectPick pick, int paths = 64) {
        ArgumentNullException.ThrowIfNull(pick);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(paths);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(paths, 256);
        var depth = Math.Max(0, (pick.Cache?.PublishedSweeps ?? 0) - 1);
        var sequence = pick.LightingSource?.Sequence ?? 0UL;
        IrradianceField? field = null;
        WorldIndirectReferenceResult Refused(string reason) => new(null, reason, field?.Samples ?? 0,
            field?.Casts ?? 0, depth, sequence, null);
        var near = pick.Near is SdfIndirectNearOutcome.Hit or SdfIndirectNearOutcome.Continuation;
        var capturedSource = near ? pick.NearSource : pick.LightingSource;
        if (near && (capturedSource is null || !ReferenceEquals(capturedSource, pick.LightingSource) ||
            !Finite(pick.NearDirection) || pick.NearDirection.LengthSquared() < 1e-12f || !Finite(pick.Launched))) {
            return Refused($"Near {pick.Near} reference needs its sampled direction and incoming source; the cache estimate cannot supply it.");
        }
        if (pick.Method != SdfIndirectMethod.Cache) { return Refused($"{pick.Method} reference needs its rendered-frame source; the cache publication cannot supply it."); }
        if (pick.Status != SdfIndirectPickStatus.Resolved || capturedSource is not { } source ||
            pick.Cache is not { PublishedSweeps: > 0 }) { return Refused($"Receiver is {pick.Status}; no complete captured lighting answer is available."); }
        if (pick.Publication != pick.Cache.PublishedStamp || pick.Generation != (uint)pick.Cache.PublishedGeneration) {
            return Refused("Receiver and captured cache publication identities disagree.");
        }
        if (!Finite(pick.Position) || !Finite(pick.Normal) || pick.Normal.LengthSquared() < 1e-12f) { return Refused("Receiver position or normal is invalid."); }
        if (depth > 9) { return Refused($"Published feedback depth {depth} exceeds the independent reference's nine-bounce bound."); }
        var frame = source.CopyFrame();
        if (frame.MeshDraws.Count != 0) { return Refused("CPU reference does not interpret the captured triangle mesh field."); }
        if ((frame.IndirectSources & SdfIndirectSources.Screens) != 0 && !frame.DisableScreenLights && frame.Program.ScreenSurfaces.Count != 0) {
            return Refused("CPU reference needs the captured screen and portal radiance publication.");
        }
        try {
            field = new IrradianceField(frame.Program);
            var baseSurfaces = IrradianceSurfaces.FromMaterials(frame.Program.Materials);
            var sky = new Vector3[SdfSkyEnvironment.Texels];
            if ((frame.IndirectSources & SdfIndirectSources.Sky) != 0) {
                var layers = new SdfSkyLayer[SdfSky.MaxLayers];
                frame.Sky.Pack(frame.Lights, frame.FarDistance, new SdfSkyDetails(), out var block, layers);
                SdfSkyEnvironment.Render(in block, layers, sky);
            }
            var lighting = new ReferenceLights(field, source, frame);
            Double3 Material(int material) {
                if ((uint)material >= (uint)frame.Program.Materials.Count) { throw new NotSupportedException($"CPU reference has no captured material {material}."); }
                return baseSurfaces.Albedo(material);
            }
            var surfaces = new IrradianceSurfaces(albedo: Material,
                emission: material => (frame.IndirectSources & SdfIndirectSources.Emission) != 0 ? baseSurfaces.Emission(material) : Double3.Zero,
                direct: (position, normal, material) => (frame.IndirectSources & SdfIndirectSources.Direct) != 0 ? lighting.Direct(position, normal, material) : Double3.Zero,
                sky: direction => ToDouble(SdfSkyEnvironment.Sample(sky, ToVector(direction))),
                reflection: (position, normal, material) => Material(material) * lighting.Attenuation(position, normal));
            var reference = new IrradianceReference(field, surfaces, frame.FarDistance);
            var feedbackDepth = (frame.IndirectSources & SdfIndirectSources.Feedback) != 0 ? depth : 0;
            var sourceEstimate = near
                ? reference.EstimateIncidentSources(ToDouble(pick.Launched), ToDouble(pick.NearDirection), feedbackDepth, paths)
                : reference.EstimateSources(ToDouble(pick.Position), ToDouble(Vector3.Normalize(pick.Normal)), feedbackDepth, paths);
            var estimate = new IrradianceEstimate(sourceEstimate.Contributions.Select(pick.SourcesEnabled), paths, sourceEstimate.Unresolved);
            if (estimate.Unresolved != 0) {
                return new(estimate, $"CPU reference has {estimate.Unresolved}/{paths} unresolved paths.", field.Samples, field.Casts, depth, sequence, null);
            }
            var gpu = pick.Sources.Direct + pick.Sources.Feedback + pick.Sources.Emission + pick.Sources.Sky + pick.Sources.Screens;
            return new(estimate, null, field.Samples, field.Casts, depth, sequence, gpu - ToVector(estimate.Irradiance));
        } catch (NotSupportedException exception) { return Refused(exception.Message); }
        catch (ArgumentException exception) { return Refused($"CPU field reference: {exception.Message}"); }
        catch (UnresolvedReferenceException exception) { return Refused(exception.Message); }
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static Double3 ToDouble(Vector3 value) => new(value.X, value.Y, value.Z);
    private static Vector3 ToVector(Double3 value) => new((float)value.X, (float)value.Y, (float)value.Z);
    private sealed class UnresolvedReferenceException(string message) : Exception(message) { }

    // The reference integrates shadow geometry independently of the GPU depth map, over a bounded eight-point
    // disk for a finite directional source. Point lights keep their authored unshadowed response.
    private sealed class ReferenceLights(IrradianceField field, SdfIndirectLightingSnapshot source, SdfFrame frame) {
        public Double3 Direct(Double3 position, Double3 normal, int material) {
            if ((uint)material >= (uint)frame.Program.Materials.Count) { throw new NotSupportedException($"CPU reference has no captured material {material}."); }
            var wrap = frame.Program.Materials[material].Wrap;
            var sum = Double3.Zero;
            for (var index = 0; index < source.LightCount; index++) {
                var light = source.Lights[index];
                double response;
                if (light.Kind == SdfLightKind.Directional) {
                    response = light.Weight * Math.Max(0, (Double3.Dot(normal, ToDouble(light.Direction)) + wrap) / (1 + wrap))
                        * Visibility(index, position, normal, light);
                } else if (light.Kind == SdfLightKind.Point) {
                    var delta = Position(light) - position;
                    var distance = delta.Length;
                    var toward = delta * (1 / Math.Max(distance, 1e-4));
                    var ratio = distance / Math.Max(light.Param, 1e-3);
                    response = light.Weight / (1 + ratio * ratio) * Math.Max(0, (Double3.Dot(normal, toward) + wrap) / (1 + wrap));
                } else { continue; }
                sum += ToDouble(light.Color) * (response * light.Bounce);
            }
            return sum;
        }

        public double Attenuation(Double3 position, Double3 normal) {
            var attenuation = 1d;
            for (var index = 0; index < source.LightCount; index++) {
                var light = source.Lights[index];
                if (light.Kind != SdfLightKind.Occluder || light.Weight <= 0) { continue; }
                var delta = Position(light) - position;
                var squared = Double3.Dot(delta, delta);
                var facing = squared > 1e-12 ? Math.Clamp(Double3.Dot(normal, delta * (1 / Math.Sqrt(squared))), 0, 1) : 1;
                var radius = Math.Max(light.Param, 1e-6);
                attenuation *= 1 - Math.Clamp(light.Weight * Math.Exp(-squared / (radius * radius)) * facing, 0, 1);
            }
            return attenuation;
        }

        private Double3 Position(SdfLight light) {
            if (light.DynamicSlot == SdfProgram.NoDynamicTransformSlot) { return ToDouble(light.Direction); }
            if ((uint)light.DynamicSlot >= (uint)frame.DynamicTransforms.Count) { throw new NotSupportedException($"Captured light transform slot {light.DynamicSlot} is unavailable."); }
            var transform = frame.DynamicTransforms[light.DynamicSlot];
            return ToDouble(transform.Position + Vector3.Transform(light.Direction, transform.Orientation));
        }

        private double Visibility(int index, Double3 position, Double3 normal, SdfLight light) {
            var strength = 0d;
            var slots = frame.Lights.ShadowSlots;
            foreach (var handoff in slots.Handoffs) {
                if (handoff.Outgoing == index) { strength = 1 - handoff.Weight; break; }
                if (handoff.Incoming == index) { strength = handoff.Weight; break; }
            }
            if (strength == 0) {
                var isHandoff = false;
                foreach (var handoff in slots.Handoffs) { isHandoff |= handoff.Outgoing == index || handoff.Incoming == index; }
                if (!isHandoff) { for (var slot = 0; slot < slots.SlotCount; slot++) { if (slots[slot] == index) { strength = 1; break; } } }
            }
            if (strength == 0) { return 1; }
            var launch = IrradianceCells.Launch(field, position, normal, 0.004);
            if (launch is null) { throw new UnresolvedReferenceException("CPU direct-light launch has no complete-field certificate."); }
            var direction = ToDouble(light.Direction).Normalize();
            var helper = Math.Abs(direction.Y) < 0.99 ? new Double3(0, 1, 0) : new Double3(1, 0, 0);
            var tangent = Double3.Cross(helper, direction).Normalize();
            var bitangent = Double3.Cross(direction, tangent);
            var clear = 0;
            var samples = light.Param > 0 ? 8 : 1;
            for (var sample = 0; sample < samples; sample++) {
                var u = IrradianceReference.RadicalInverse(sample + 1, 2);
                var v = IrradianceReference.RadicalInverse(sample + 1, 3);
                var radius = Math.Sqrt(u) * Math.Max(light.Param, 0);
                var angle = 2 * Math.PI * v;
                var toward = (direction + tangent * (radius * Math.Cos(angle)) + bitangent * (radius * Math.Sin(angle))).Normalize();
                var ray = field.Cast(launch.Value.Point, toward, frame.FarDistance);
                if (ray.Kind == IrradianceRayKind.Unresolved) { throw new UnresolvedReferenceException("CPU direct-light shadow ray is unresolved."); }
                if (ray.Kind == IrradianceRayKind.Miss) { clear++; }
            }
            return 1 - (1 - (double)clear / samples) * strength;
        }
    }
}
