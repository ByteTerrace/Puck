using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The far field's laws, over a four-storey hall seen from one end at the High tier: what the coarse levels'
/// probes may answer at receivers past the finest window's reach.</summary>
public sealed partial class SdfMeshIrradianceLawTests {
    // The hall: storeys 4.5 m apart over 0.3 m slabs, 12 m wide and 70 m long, a 2 m doorway in each side wall of every
    // storey, over a sunlit ground; the camera stands at its west end.
    private const float StoreyHeight = 4.5f;
    private const float SlabThickness = 0.3f;
    private const int Storeys = 4;
    private const float HallLength = 70f;
    private const float HallHalfWidth = 6f;
    private const float DoorLow = 50f;
    private const float DoorHigh = 52f;
    private const float DoorTop = 2.5f;
    // Where the far third starts: past the finest window's reach at High about a camera at the hall's west end.
    private const float FarStart = 38f;

    /// <summary>A far receiver's gather, as the CPU reads it: the stage it chose and each corner's report.</summary>
    private readonly record struct FarRead(Vector3 Point, Vector3 Normal, int Storey, int Level, bool Measured, SdfMeshProbeReport[] Reports, bool[] Clear);

    /// <summary>The counts the far field's laws read over every far receiver.</summary>
    private readonly record struct FarCounts(int Receivers, int Answering, int Blocked, int CoarseBlocked, int Clear, int OverOccluded, int StoreyFallbacks, int PlanelessReceivers) {
        public override string ToString() => string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"receivers={Receivers} answering={Answering} exact-blocked={Blocked} coarse-exact-blocked={CoarseBlocked} clear={Clear} over-occluded={OverOccluded} planeless-fallbacks={StoreyFallbacks}/{PlanelessReceivers}");
    }

    [GeneratedRegex(pattern: @"level=(\d) measured=(\d)")]
    private static partial Regex FarLevelPattern();

    /// <summary>THE LAW: a coarse level's probe answers no receiver in another room. Over the far third of a four-storey
    /// hall, no answering corner of a coarse level stands where the exact segment to its receiver meets a slab or a wall,
    /// even where a tap only partly saw that slab, and the conservative ramp that closes them darkens at most one in a
    /// hundred clear corners.</summary>
    [Fact]
    public void ACoarseProbeAnswersNoReceiverAcrossASlabOrAWall() {
        var frame = StoreyedHall();
        var volume = new SdfMeshIrradiance();
        var (parameters, _) = Trace(frame: frame, volume: volume);
        using var scene = volume.DiagnosticScene(frame: frame);

        Assert.NotNull(@object: scene);

        var counts = Count(parameters: parameters, reads: ReadFar(scene: scene, volume: volume));

        Assert.True(condition: (counts.CoarseBlocked == 0), userMessage: counts.ToString());
        Assert.True(condition: (counts.OverOccluded <= (counts.Clear / 100)), userMessage: counts.ToString());
    }

    private static SdfFrame StoreyedHall() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.8f)));

        builder.Sphere(material: material, radius: 0.1f);

        var lights = new SdfLights();

        lights.Set(index: 0, light: new SdfLight(Color: Vector3.One, Direction: Vector3.Normalize(value: new Vector3(x: 0.3f, y: 1f, z: 0.2f)), Kind: SdfLightKind.Directional, Param: 0f, Shadows: true, Weight: 1000f));
        lights.Count = 1;
        lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 0);

        var gains = new SdfIndirectGains(Emission: 0f, Feedback: 1f, Lights: 1f, Screens: 0f, Sky: 1f);
        var draws = new List<SdfMeshDraw> {
            Draw(mesh: Box(low: new Vector3(x: -300f, y: -0.6f, z: -300f), high: new Vector3(x: 300f, y: -0.3f, z: 300f))),
            Draw(mesh: Box(low: new Vector3(x: -SlabThickness, y: -SlabThickness, z: -HallHalfWidth), high: new Vector3(x: 0f, y: (Storeys * StoreyHeight), z: HallHalfWidth))),
            Draw(mesh: Box(low: new Vector3(x: HallLength, y: -SlabThickness, z: -HallHalfWidth), high: new Vector3(x: (HallLength + SlabThickness), y: (Storeys * StoreyHeight), z: HallHalfWidth))),
        };

        for (var storey = 0; (storey <= Storeys); storey++) {
            var top = (storey * StoreyHeight);

            draws.Add(item: Draw(mesh: Box(low: new Vector3(x: -SlabThickness, y: (top - SlabThickness), z: -(HallHalfWidth + SlabThickness)), high: new Vector3(x: (HallLength + SlabThickness), y: top, z: (HallHalfWidth + SlabThickness)))));
            if (storey == Storeys) { break; }

            var ceiling = (top + (StoreyHeight - SlabThickness));

            foreach (var (low, high) in new[] { (-(HallHalfWidth + SlabThickness), -HallHalfWidth), (HallHalfWidth, (HallHalfWidth + SlabThickness)) }) {
                draws.Add(item: Draw(mesh: Box(low: new Vector3(x: -SlabThickness, y: top, z: low), high: new Vector3(x: DoorLow, y: ceiling, z: high))));
                draws.Add(item: Draw(mesh: Box(low: new Vector3(x: DoorHigh, y: top, z: low), high: new Vector3(x: (HallLength + SlabThickness), y: ceiling, z: high))));
                draws.Add(item: Draw(mesh: Box(low: new Vector3(x: DoorLow, y: (top + DoorTop), z: low), high: new Vector3(x: DoorHigh, y: ceiling, z: high))));
            }
        }

        return new SdfFrame(
            Program: builder.Build(),
            ProgramChanged: true,
            Views: [new SdfViewSnapshot(Camera: CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: new Vector3(x: 2f, y: 2f, z: 0f), target: new Vector3(x: 3f, y: 2f, z: 0f), viewportHeight: 4u, viewportWidth: 4u), Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f))],
            Time: 0f
        ) {
            ConvergesLighting = true,
            FarDistance = 120f,
            IndirectBounces = 1,
            IndirectGains = gains,
            IndirectSources = gains.Sources,
            IndirectTier = SdfIndirectTier.High,
            Lights = lights,
            MeshDraws = [.. draws],
        };
    }
    // The far third's receivers on every storey: floors, slab undersides, both side walls and the end wall.
    private static IEnumerable<(Vector3 Point, Vector3 Normal, int Storey)> FarReceivers() {
        for (var storey = 0; (storey < Storeys); storey++) {
            var floor = (storey * StoreyHeight);
            var ceiling = (floor + (StoreyHeight - SlabThickness));

            for (var x = (FarStart + 0.7f); (x < HallLength); x += 2.3f) {
                for (var z = -5.1f; (z < 5.5f); z += 1.7f) {
                    yield return (new Vector3(x: x, y: floor, z: z), Vector3.UnitY, storey);
                    yield return (new Vector3(x: x, y: ceiling, z: z), -Vector3.UnitY, storey);
                }
                for (var y = (floor + 0.4f); (y < ceiling); y += 0.9f) {
                    if ((x > DoorLow) && (x < DoorHigh) && (y < (floor + DoorTop))) { continue; }
                    yield return (new Vector3(x: x, y: y, z: -HallHalfWidth), Vector3.UnitZ, storey);
                    yield return (new Vector3(x: x, y: y, z: HallHalfWidth), -Vector3.UnitZ, storey);
                }
            }
            for (var y = (floor + 0.4f); (y < ceiling); y += 0.9f) {
                for (var z = -5.1f; (z < 5.5f); z += 1.7f) {
                    yield return (new Vector3(x: HallLength, y: y, z: z), -Vector3.UnitX, storey);
                }
            }
        }
    }
    // Reads every far receiver's gather, and each corner's exact segment test from its probe to the pushed receiver.
    private static FarRead[] ReadFar(SdfMeshIrradiance volume, SdfMeshIrradianceScene scene) {
        var reads = new List<FarRead>();
        var cells = new (int X, int Y, int Z)[8];
        var weights = new float[8];

        foreach (var (point, normal, storey) in FarReceivers()) {
            var reports = new SdfMeshProbeReport[8];
            var clear = new bool[8];

            Assert.True(condition: volume.TryGather(cells: cells, normal: normal, point: point, reports: reports, weights: weights));

            var match = FarLevelPattern().Match(input: volume.DescribeProbes(normal: normal, point: point)[0]);

            Assert.True(condition: match.Success);

            var pushed = (point + (normal * SdfMeshIrradianceProbe.NormalBias));

            for (var corner = 0; (corner < 8); corner++) {
                var toward = (pushed - reports[corner].Position);
                var distance = toward.Length();

                clear[corner] = ((distance <= 1e-3f) || scene.Unblocked(direction: (toward / distance), maximum: (distance - 1e-3f), origin: reports[corner].Position));
            }
            reads.Add(item: new FarRead(Clear: clear, Level: int.Parse(s: match.Groups[1].Value, provider: CultureInfo.InvariantCulture), Measured: (match.Groups[2].Value == "1"),
                Normal: normal, Point: point, Reports: reports, Storey: storey));
        }
        return [.. reads];
    }
    // Whether a storey holds a plane of the 8 m level's probes, as the hall's camera places that level's window.
    private static bool HoldsCoarsePlane(in SdfMeshIrradianceParameters parameters, int storey) {
        var window = parameters.Middle;
        var floor = (storey * StoreyHeight);
        var ceiling = (floor + (StoreyHeight - SlabThickness));

        for (var y = window.Low.Y; (y < (window.Low.Y + window.Dims.Y)); y++) {
            var plane = ((y + 0.5f) * window.Spacing);

            if ((plane > floor) && (plane < ceiling)) { return true; }
        }
        return false;
    }
    private static FarCounts Count(FarRead[] reads, in SdfMeshIrradianceParameters parameters) {
        var answering = 0;
        var blocked = 0;
        var coarseBlocked = 0;
        var clear = 0;
        var overOccluded = 0;
        var fallbacks = 0;
        var planeless = 0;

        foreach (var read in reads) {
            if (!HoldsCoarsePlane(parameters: parameters, storey: read.Storey)) {
                planeless++;
                if ((read.Level == 0) && !read.Measured) { fallbacks++; }
            }
            for (var corner = 0; (corner < 8); corner++) {
                var report = read.Reports[corner];

                if (!report.Present || report.Invalid || ((report.Trilinear * report.Facing) <= 0f)) { continue; }
                if (report.Weight > 0f) {
                    answering++;
                    if (!read.Clear[corner]) {
                        blocked++;
                        if (read.Level > 0) { coarseBlocked++; }
                    }
                }
                if (read.Clear[corner]) {
                    clear++;
                    if (report.Visibility <= 0f) { overOccluded++; }
                }
            }
        }
        return new FarCounts(Answering: answering, Blocked: blocked, Clear: clear, CoarseBlocked: coarseBlocked, OverOccluded: overOccluded,
            PlanelessReceivers: planeless, Receivers: reads.Length, StoreyFallbacks: fallbacks);
    }
}
