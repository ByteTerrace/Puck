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
        var depth = Math.Max(val1: 0, val2: ((pick.Cache?.PublishedSweeps ?? 0) - 1));
        var sequence = (pick.LightingSource?.Sequence ?? 0UL);
        IrradianceField? field = null;

        WorldIndirectReferenceResult Refused(string reason) => new(null, reason, (field?.Samples ?? 0),
            (field?.Casts ?? 0), depth, sequence, null);
        var near = (pick.Near is SdfIndirectNearOutcome.Hit or SdfIndirectNearOutcome.Continuation);
        var capturedSource = (near ? pick.NearSource : pick.LightingSource);

        if (near && ((capturedSource is null) || !ReferenceEquals(objA: capturedSource, objB: pick.LightingSource) ||
            !Finite(value: pick.NearDirection) || (pick.NearDirection.LengthSquared() < 1e-12f) || !Finite(value: pick.Launched))) {
            return Refused(reason: $"Near {pick.Near} reference needs its sampled direction and incoming source; the cache estimate cannot supply it.");
        }
        if (pick.Method != SdfIndirectMethod.Cache) { return Refused(reason: $"{pick.Method} reference needs its rendered-frame source; the cache publication cannot supply it."); }
        if ((pick.Status != SdfIndirectPickStatus.Resolved) || (capturedSource is not { } source) ||
            (pick.Cache is not { PublishedSweeps: > 0 })) { return Refused(reason: $"Receiver is {pick.Status}; no complete captured lighting answer is available."); }
        if ((pick.Publication != pick.Cache.PublishedStamp) || (pick.Generation != ((uint)pick.Cache.PublishedGeneration))) {
            return Refused(reason: "Receiver and captured cache publication identities disagree.");
        }
        if (!Finite(value: pick.Position) || !Finite(value: pick.Normal) || (pick.Normal.LengthSquared() < 1e-12f)) { return Refused(reason: "Receiver position or normal is invalid."); }
        if (depth > 9) { return Refused(reason: $"Published feedback depth {depth} exceeds the independent reference's nine-bounce bound."); }
        var frame = source.CopyFrame();

        if (frame.MeshDraws.Count != 0) { return Refused(reason: "CPU reference does not interpret the captured triangle mesh field."); }
        if (((frame.IndirectSources & SdfIndirectSources.Screens) != 0) && !frame.DisableScreenLights && (frame.Program.ScreenSurfaces.Count != 0)) {
            return Refused(reason: "CPU reference needs the captured screen and portal radiance publication.");
        }
        try {
            field = new IrradianceField(program: frame.Program);
            var baseSurfaces = IrradianceSurfaces.FromMaterials(frame.Program.Materials);
            var sky = new Vector3[SdfSkyEnvironment.Texels];

            if ((frame.IndirectSources & SdfIndirectSources.Sky) != 0) {
                var layers = new SdfSkyLayer[SdfSky.MaxLayers];

                frame.Sky.Pack(frame.Lights, frame.FarDistance, new SdfSkyDetails(), out var block, layers);
                SdfSkyEnvironment.Render(block: in block, layers: layers, map: sky);
            }
            var lighting = new ReferenceLights(field: field, frame: frame, source: source);

            Double3 Material(int material) {
                if (((uint)material) >= ((uint)frame.Program.Materials.Count)) { throw new NotSupportedException(message: $"CPU reference has no captured material {material}."); }
                return baseSurfaces.Albedo(material);
            }
            var surfaces = new IrradianceSurfaces(albedo: Material,
                emission: material => (((frame.IndirectSources & SdfIndirectSources.Emission) != 0) ? (baseSurfaces.Emission(material) * frame.IndirectGains.Emission) : Double3.Zero),
                direct: (position, normal, material) => (((frame.IndirectSources & SdfIndirectSources.Direct) != 0) ? (lighting.Direct(material: material, normal: normal, position: position) * frame.IndirectGains.Lights) : Double3.Zero),
                sky: direction => (ToDouble(value: SdfSkyEnvironment.Sample(sky, ToVector(value: direction))) * frame.IndirectGains.Sky),
                reflection: (position, normal, material) => (Material(material: material) * lighting.Attenuation(normal: normal, position: position)));
            var reference = new IrradianceReference(field, surfaces, frame.FarDistance, frame.IndirectGains.Feedback);
            var feedbackDepth = (((frame.IndirectSources & SdfIndirectSources.Feedback) != 0) ? depth : 0);
            var sourceEstimate = (near
                ? reference.EstimateIncidentSources(ToDouble(value: pick.Launched), ToDouble(value: pick.NearDirection), feedbackDepth, paths)
                : reference.EstimateSources(ToDouble(value: pick.Position), ToDouble(value: Vector3.Normalize(value: pick.Normal)), feedbackDepth, paths));
            var estimate = new IrradianceEstimate(Irradiance: sourceEstimate.Contributions.Select(sources: pick.SourcesEnabled), Paths: paths, Unresolved: sourceEstimate.Unresolved);

            if (estimate.Unresolved != 0) {
                return new(estimate, $"CPU reference has {estimate.Unresolved}/{paths} unresolved paths.", field.Samples, field.Casts, depth, sequence, null);
            }
            // Both sides are incoming radiance. The captured receiver's application controls and material response
            // are deliberately outside this source comparison; neither is reapplied to the pinned transport.
            var gpu = ((((pick.Sources.Direct + pick.Sources.Feedback) + pick.Sources.Emission) + pick.Sources.Sky) + pick.Sources.Screens);

            return new(estimate, null, field.Samples, field.Casts, depth, sequence, (gpu - ToVector(value: estimate.Irradiance)));
        } catch (NotSupportedException exception) { return Refused(reason: exception.Message); } catch (ArgumentException exception) { return Refused(reason: $"CPU field reference: {exception.Message}"); } catch (UnresolvedReferenceException exception) { return Refused(reason: exception.Message); }
    }

    private static bool Finite(Vector3 value) => (float.IsFinite(f: value.X) && float.IsFinite(f: value.Y) && float.IsFinite(f: value.Z));
    private static Double3 ToDouble(Vector3 value) => new(X: value.X, Y: value.Y, Z: value.Z);
    private static Vector3 ToVector(Double3 value) => new(x: ((float)value.X), y: ((float)value.Y), z: ((float)value.Z));

    private sealed class UnresolvedReferenceException(string message) : Exception(message) { }
    // The reference integrates shadow geometry independently of the GPU depth map, over a bounded eight-point
    // disk for a finite directional source. Point lights keep their authored unshadowed response.
    private sealed class ReferenceLights(IrradianceField field, SdfIndirectLightingSnapshot source, SdfFrame frame) {
        public Double3 Direct(Double3 position, Double3 normal, int material) {
            if (((uint)material) >= ((uint)frame.Program.Materials.Count)) { throw new NotSupportedException(message: $"CPU reference has no captured material {material}."); }
            var wrap = frame.Program.Materials[material].Wrap;
            var sum = Double3.Zero;

            for (var index = 0; (index < source.LightCount); index++) {
                var light = source.Lights[index];
                double response;

                if (light.Kind == SdfLightKind.Directional) {
                    response = ((light.Weight * Math.Max(val1: 0, val2: ((Double3.Dot(a: normal, b: ToDouble(value: light.Direction)) + wrap) / (1 + wrap))))
                        * Visibility(index: index, light: light, normal: normal, position: position));
                } else if (light.Kind == SdfLightKind.Point) {
                    var delta = (Position(light: light) - position);
                    var distance = delta.Length;
                    var toward = (delta * (1 / Math.Max(val1: distance, val2: 1e-4)));
                    var ratio = (distance / Math.Max(val1: light.Param, val2: 1e-3));

                    response = ((light.Weight / (1 + (ratio * ratio))) * Math.Max(val1: 0, val2: ((Double3.Dot(a: normal, b: toward) + wrap) / (1 + wrap))));
                } else { continue; }
                sum += (ToDouble(value: light.Color) * (response * light.Bounce));
            }
            return sum;
        }
        public double Attenuation(Double3 position, Double3 normal) {
            var attenuation = 1d;

            for (var index = 0; (index < source.LightCount); index++) {
                var light = source.Lights[index];

                if ((light.Kind != SdfLightKind.Occluder) || (light.Weight <= 0)) { continue; }
                var delta = (Position(light: light) - position);
                var squared = Double3.Dot(a: delta, b: delta);
                var facing = ((squared > 1e-12) ? Math.Clamp(Double3.Dot(a: normal, b: (delta * (1 / Math.Sqrt(d: squared)))), 0, 1) : 1);
                var radius = Math.Max(val1: light.Param, val2: 1e-6);

                attenuation *= (1 - Math.Clamp(((light.Weight * Math.Exp(d: (-squared / (radius * radius)))) * facing), 0, 1));
            }
            return attenuation;
        }

        private Double3 Position(SdfLight light) {
            if (light.DynamicSlot == SdfProgram.NoDynamicTransformSlot) { return ToDouble(value: light.Direction); }
            if (((uint)light.DynamicSlot) >= ((uint)frame.DynamicTransforms.Count)) { throw new NotSupportedException(message: $"Captured light transform slot {light.DynamicSlot} is unavailable."); }
            var transform = frame.DynamicTransforms[light.DynamicSlot];

            return ToDouble(value: (transform.Position + Vector3.Transform(light.Direction, transform.Orientation)));
        }
        private double Visibility(int index, Double3 position, Double3 normal, SdfLight light) {
            var strength = 0d;
            var slots = frame.Lights.ShadowSlots;

            foreach (var handoff in slots.Handoffs) {
                if (handoff.Outgoing == index) { strength = (1 - handoff.Weight); break; }
                if (handoff.Incoming == index) { strength = handoff.Weight; break; }
            }
            if (strength == 0) {
                var isHandoff = false;

                foreach (var handoff in slots.Handoffs) { isHandoff |= ((handoff.Outgoing == index) || (handoff.Incoming == index)); }
                if (!isHandoff) { for (var slot = 0; (slot < slots.SlotCount); slot++) { if (slots[slot] == index) { strength = 1; break; } } }
            }
            if (strength == 0) { return 1; }
            var launch = IrradianceCells.Launch(field: field, height: 0.004, normal: normal, surface: position);

            if (launch is null) { throw new UnresolvedReferenceException(message: "CPU direct-light launch has no complete-field certificate."); }
            var direction = ToDouble(value: light.Direction).Normalize();
            var helper = ((Math.Abs(value: direction.Y) < 0.99) ? new Double3(X: 0, Y: 1, Z: 0) : new Double3(X: 1, Y: 0, Z: 0));
            var tangent = Double3.Cross(a: helper, b: direction).Normalize();
            var bitangent = Double3.Cross(a: direction, b: tangent);
            var clear = 0;
            var samples = ((light.Param > 0) ? 8 : 1);

            for (var sample = 0; (sample < samples); sample++) {
                var u = IrradianceReference.RadicalInverse(index: (sample + 1), primeBase: 2);
                var v = IrradianceReference.RadicalInverse(index: (sample + 1), primeBase: 3);
                var radius = (Math.Sqrt(d: u) * Math.Max(val1: light.Param, val2: 0));
                var angle = ((2 * Math.PI) * v);
                var toward = ((direction + (tangent * (radius * Math.Cos(d: angle)))) + (bitangent * (radius * Math.Sin(a: angle)))).Normalize();
                var ray = field.Cast(launch.Value.Point, toward, frame.FarDistance);

                if (ray.Kind == IrradianceRayKind.Unresolved) { throw new UnresolvedReferenceException(message: "CPU direct-light shadow ray is unresolved."); }
                if (ray.Kind == IrradianceRayKind.Miss) { clear++; }
            }
            return (1 - ((1 - (((double)clear) / samples)) * strength));
        }
    }
}
