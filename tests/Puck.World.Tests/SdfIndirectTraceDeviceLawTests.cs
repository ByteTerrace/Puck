using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The indirect kernel's ray certificates, probe classes, partitions, launches and segment proofs agree
/// with the illumination CPU model on resolved fixtures and refuse the same sealed and unresolved paths.</summary>
[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed partial class SdfIndirectTraceDeviceLawTests {
    private const float Spacing = 1.5f;
    private const int ResultRows = 12;

    private enum ProbeMode { Trace, Place, Partition, Launch, Segment, EndpointSupport, NormalCodec, LaunchRecord, ProofEntries, ProofReuse, StoreCell, ProofPublication }
    private sealed record ProbeCase(string Name, SdfProgram Program, ProbeMode Mode, Vector3 Point, Vector3 Vector,
        float Reach = 4, float Far = 10, float Spacing = SdfIndirectTraceDeviceLawTests.Spacing, bool? ExpectedReuse = null);

    [Fact]
    public void VulkanMatchesTheIlluminationModel() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfIndirectTraceDeviceLawTests));

        Verify(extension: ".spv", services: device.Services);
    }
    [Fact]
    public void DirectXMatchesTheIlluminationModel() {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) {
            Verify(extension: ".dxil", services: device.Services);
        }
        Assert.DoesNotContain(comparisonType: StringComparison.Ordinal, expectedSubstring: "[d3d12-debug]", actualString: output.ToString());
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        var cases = Cases();
        var inputs = new Vector4[(cases.Length * 3)];

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];

            inputs[(index * 3)] = new Vector4(value: item.Point, w: item.Reach);
            inputs[((index * 3) + 1)] = new Vector4(value: item.Vector, w: item.Far);
            inputs[((index * 3) + 2)] = new Vector4(x: ((float)item.Mode), y: item.Spacing, z: 0, w: 0);
        }
        var results = SdfIndirectDeviceProbe.Run(extension: extension, kernel: "sdf-indirect-trace-proof.comp",
            programs: cases.Select(static item => item.Program).ToArray(), resultRows: ResultRows, rows: inputs, services: services);

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];
            var field = new IrradianceField(program: item.Program);
            var point = AsDouble(value: item.Point);
            var vector = AsDouble(value: item.Vector);
            var result = results[index];

            switch (item.Mode) {
                case ProbeMode.Trace:
                    var expected = field.Cast(direction: vector, maxDistance: item.Far, origin: point);
                    var kind = expected.Kind switch {
                        IrradianceRayKind.Hit => IrradianceHitKind.Hit,
                        IrradianceRayKind.Miss => IrradianceHitKind.Exit,
                        _ => IrradianceHitKind.Unresolved,
                    };
                    Assert.True(condition: (((float)kind) == result.X), userMessage: $"{item.Name}: CPU {kind}, GPU kind {result.X}");
                    var stored = results[((10 * cases.Length) + index)];
                    Assert.Equal(expected: result.Y, actual: stored.X);
                    Assert.Equal(expected: result.Z, actual: stored.Y);
                    Assert.Equal(expected: ((uint)kind) | (165u << SdfIndirectLayout.ProofMaskShift) | (1u << SdfIndirectLayout.ProofLevelShift), actual: ((uint)stored.Z));
                    if (kind == IrradianceHitKind.Hit) {
                        Assert.InRange(actual: result.Y, low: (((float)expected.Distance) - 0.002f), high: (((float)expected.Distance) + 0.002f));
                        Assert.Equal(expected: expected.Material, actual: ((int)result.Z));
                        Assert.True(condition: field.TryGradient(gradient: out var normal, point: expected.Point));
                        var actualNormal = results[(cases.Length + index)];

                        Assert.True(condition: (Double3.Dot(a: normal, b: AsDouble(value: new Vector3(actualNormal.X, actualNormal.Y, actualNormal.Z))) > 0.999),
                            userMessage: $"{item.Name}: normal {actualNormal}, CPU {normal}");
                        var storedNormal = results[((11 * cases.Length) + index)];

                        Assert.True(condition: (Double3.Dot(a: normal, b: AsDouble(value: new Vector3(storedNormal.X, storedNormal.Y, storedNormal.Z))) > 0.999),
                            userMessage: $"{item.Name}: stored normal {storedNormal}, CPU {normal}");
                    } else if (kind == IrradianceHitKind.Unresolved) {
                        Assert.InRange(actual: result.Y, low: 0f, high: (item.Far - 0.001f));
                    }
                    break;
                case ProbeMode.Place:
                    HoldsPlacement(expected: IrradianceCells.Place(field: field, lattice: point, spacing: item.Spacing), actual: result, name: item.Name);
                    break;
                case ProbeMode.Partition:
                    HoldsPartition(cases: cases.Length, field: field, index: index, item: item, results: results);
                    break;
                case ProbeMode.Launch:
                    HoldsLaunch(actual: result, certificate: results[(cases.Length + index)], field: field, item: item);
                    break;
                case ProbeMode.Segment:
                    Assert.True(condition: (field.SegmentClear(from: point, to: vector) == (result.W > 0.5f)),
                        userMessage: $"{item.Name}: GPU clear={result.W}, CPU disagrees");
                    break;
                case ProbeMode.EndpointSupport:
                    var fromHandoff = (vector - point);
                    var supported = ((fromHandoff.X > 0.0) && (fromHandoff.Normalize().X >= Math.Cos(0.5)));
                    Assert.True(condition: (supported == (result.W > 0.5f)), userMessage: $"{item.Name}: continuation support {result.W}, expected {supported}");
                    break;
                case ProbeMode.NormalCodec:
                    if (vector == Double3.Zero) {
                        Assert.Equal(expected: Vector4.Zero, actual: result);
                    } else {
                        Assert.Equal(expected: 1f, actual: result.W);
                        Assert.True(condition: (Double3.Dot(a: vector.Normalize(), b: AsDouble(value: new Vector3(result.X, result.Y, result.Z))) > 0.9999),
                            userMessage: $"{item.Name}: decoded normal {result}");
                    }
                    break;
                case ProbeMode.LaunchRecord:
                    HoldsLaunchRecord(item: item, field: field, rows: Enumerable.Range(start: 0, count: 5)
                        .Select(row => results[((row * cases.Length) + index)]).ToArray());
                    break;
                case ProbeMode.ProofEntries:
                    Assert.Equal(expected: (7 * SdfIndirectLayout.ProofsPerCell), actual: ((int)result.X));
                    Assert.Equal(expected: ((8 * SdfIndirectLayout.ProofsPerCell) - 1), actual: ((int)result.Y));
                    Assert.Equal(expected: ((1 << SdfIndirectLayout.ProofsPerCell) - 1), actual: ((int)result.Z));
                    Assert.Equal(expected: 0f, actual: result.W);
                    break;
                case ProbeMode.ProofReuse:
                    Assert.Equal(expected: item.ExpectedReuse!.Value, actual: (result.W > 0.5f));
                    if (item.Name == "two certified balls reuse the anchor proof") {
                        Assert.True(condition: (result.X > item.Reach), userMessage: "The receiver lies inside the anchor ball, so the control does not require two-ball reuse.");
                    }
                    break;
                case ProbeMode.StoreCell:
                    Assert.True(condition: (result.X == 0f), userMessage: $"{item.Name}: {result.X} proof words survived the cell store");
                    Assert.True(condition: (result.Y == 0f), userMessage: $"{item.Name}: {result.Y} words outside the cell and proof range changed");
                    Assert.True(condition: (result.Z == 0f), userMessage: $"{item.Name}: the cell record was not stored intact");
                    Assert.Equal(expected: ((1 << SdfIndirectLayout.ProofsPerCell) - 1), actual: ((int)result.W));
                    break;
                case ProbeMode.ProofPublication:
                    Assert.Equal(expected: new Vector4(1, 0, 0, 0), actual: result);
                    var publication = results[(cases.Length + index)];
                    Assert.Equal(expected: 165f, actual: publication.X);
                    Assert.Equal(expected: 0f, actual: publication.Y);
                    Assert.Equal(expected: 0f, actual: publication.Z);
                    break;
            }
        }
    }
    private static void HoldsLaunchRecord(ProbeCase item, IrradianceField field, Vector4[] rows) {
        var original = rows[0];
        var quantized = rows[1];
        var reconstructed = rows[2];
        var certificate = rows[3];
        var originalPosition = new Vector3(original.X, original.Y, original.Z);
        var quantizedPosition = new Vector3(quantized.X, quantized.Y, quantized.Z);
        var height = Vector3.Dot((originalPosition - item.Point), item.Vector);

        Assert.Equal(expected: 1f, actual: original.W);
        Assert.InRange(actual: height, low: 0.0001f, high: 0.0012f);
        Assert.True(condition: (height < (IrradianceCells.ReceiverBias * item.Spacing)));
        var encoded = ((uint)certificate.Y);
        var terminal = ((uint)certificate.Z) | (((uint)certificate.W) << 16);

        Assert.Equal(expected: encoded, actual: (terminal >> SdfIndirectLayout.LaunchHeightShift));
        Assert.Equal(expected: new Vector3(quantized.X, quantized.Y, quantized.Z), actual: new Vector3(reconstructed.X, reconstructed.Y, reconstructed.Z));
        Assert.InRange(actual: reconstructed.W, low: 0f, high: height);
        var displacement = Vector3.Distance(originalPosition, quantizedPosition);

        Assert.InRange(actual: quantized.W, low: 0.0000001f, high: ((certificate.X - displacement) + 0.000000001f));
        if (item.Vector != Vector3.UnitY) {
            Assert.True(condition: (Vector3.Distance(quantizedPosition, (item.Point + (item.Vector * reconstructed.W))) > 0.000000001f),
                userMessage: $"{item.Name}: the fixture did not exercise normal quantization");
        }
        Assert.True(condition: field.TryClampedDistance(point: AsDouble(new Vector3(reconstructed.X, reconstructed.Y, reconstructed.Z)), distance: out var distance, material: out _));
        Assert.True(condition: (quantized.W <= (distance + (2 * IrradianceField.Resolution))));
        Assert.Equal(expected: 1f, actual: rows[4].W);
    }
    private static void HoldsPlacement(IrradianceProbePlacement expected, Vector4 actual, string name) {
        Assert.True(condition: (actual.W == ((float)expected.Class)), userMessage: $"{name}: class {actual.W}, CPU {expected.Class}");
        HoldsPoint(actual: new Vector3(actual.X, actual.Y, actual.Z), expected: expected.Position, tolerance: 0.002, name: name);
    }
    private static void HoldsPartition(int cases, IrradianceField field, int index, ProbeCase item, Vector4[] results) {
        var corners = new IrradianceProbePlacement[IrradianceLattice.CellCorners];

        for (var corner = 0; (corner < corners.Length); corner++) {
            var offset = (new Double3(X: corner & 1, Y: (corner >> 1) & 1, Z: (corner >> 2) & 1) * item.Spacing);

            corners[corner] = IrradianceCells.Place(field: field, lattice: (AsDouble(value: item.Point) + offset), spacing: item.Spacing);
            HoldsPlacement(actual: results[((corner * cases) + index)], expected: corners[corner], name: $"{item.Name}, corner {corner}");
        }
        var expected = IrradianceCells.Partition(corners: corners, field: field, spacing: item.Spacing);
        var packed = results[((8 * cases) + index)];
        var components = ((uint)packed.X) | (((uint)packed.Y) << 16);

        for (var corner = 0; (corner < corners.Length); corner++) {
            var component = ((int)((components >> (corner * 4)) & 15));

            Assert.True(condition: (component == ((expected.Components[corner] < 0) ? 15 : expected.Components[corner])),
                userMessage: $"{item.Name}, corner {corner}: component {component}, CPU {expected.Components[corner]}");
        }
        var plane = results[((9 * cases) + index)];

        if (expected.Plane is { } fitted) {
            Assert.True(condition: (Double3.Dot(a: fitted.Normal, b: AsDouble(value: new Vector3(plane.X, plane.Y, plane.Z))) > 0.99),
                userMessage: $"{item.Name}: plane normal {plane}, CPU {fitted.Normal}");
            Assert.InRange(actual: packed.Z, low: (((float)fitted.Offset) - 0.01f), high: (((float)fitted.Offset) + 0.01f));
        } else {
            Assert.Equal(expected: Vector4.Zero, actual: plane);
        }
    }
    private static void HoldsLaunch(Vector4 actual, Vector4 certificate, IrradianceField field, ProbeCase item) {
        var expected = IrradianceCells.Launch(field: field, height: (IrradianceCells.ReceiverBias * item.Spacing),
            normal: AsDouble(value: item.Vector), surface: AsDouble(value: item.Point));

        if (expected is null) {
            Assert.True(condition: (actual.W == 0f), userMessage: $"{item.Name}: uncertified launch {actual}");
            return;
        }
        if (item.Name == "a slab stops the outward launch") {
            Assert.True(condition: ((actual.W == 0f) || (actual.Y < 0.0012f)), userMessage: $"{item.Name}: launch crossed the slab: {actual}");
            return;
        }
        Assert.Equal(expected: 1f, actual: actual.W);
        HoldsPoint(actual: new Vector3(actual.X, actual.Y, actual.Z), expected: expected.Value.Point, tolerance: 0.002, name: item.Name);
        Assert.True(condition: (certificate.X > 0f), userMessage: $"{item.Name}: no positive launch certificate");
        Assert.True(condition: field.TryClampedDistance(distance: out var distance, material: out _, point: AsDouble(value: new Vector3(actual.X, actual.Y, actual.Z))));
        Assert.True(condition: (certificate.X <= (distance + 0.0001)), userMessage: $"{item.Name}: clearance {certificate.X} exceeds full field {distance}");
    }
    private static Double3 AsDouble(Vector3 value) => new(X: value.X, Y: value.Y, Z: value.Z);
    private static void HoldsPoint(Vector3 actual, Double3 expected, double tolerance, string name) =>
        Assert.True(condition: ((AsDouble(value: actual) - expected).Length <= tolerance), userMessage: $"{name}: point {actual}, CPU {expected}");
}
