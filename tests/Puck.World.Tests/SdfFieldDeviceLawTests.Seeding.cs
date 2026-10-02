using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    [Fact]
    public void VulkanPrimaryCountsOneCompleteSeedProofWithoutSpendingTheMarchBudget() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyPrimarySeed(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXPrimaryCountsOneCompleteSeedProofWithoutSpendingTheMarchBudget() {
        using var device = DirectXTestDevices.Hardware();

        VerifyPrimarySeed(services: device.Services, extension: ".dxil");
    }

    private static void VerifyPrimarySeed(GpuDeviceServices services, string extension) {
        var kernel = File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: ("sdf-primary-seed.comp" + extension)));
        var plane = Pack(emit: static (builder, material) => builder.Plane(normal: -Vector3.UnitX, offset: 8, material: material));
        var blocked = Pack(emit: static (builder, material) => builder
            .ResetPoint().Translate(offset: new Vector3(x: 4, y: 0, z: 0)).Sphere(radius: 0.125f, material: material)
            .ResetPoint().Plane(normal: -Vector3.UnitX, offset: 8, material: material));
        var parallel = Pack(emit: static (builder, material) => builder.Plane(normal: Vector3.UnitY, offset: 1, material: material));
        var independent = new SdfProgramBuilder();
        var surface = independent.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        independent.Plane(normal: -Vector3.UnitX, offset: 4, material: surface);
        independent.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 1);
        independent.PushField().ResetPoint().Sphere(radius: 0.25f, material: surface).PopField();
        independent.EndInstance();
        Check(name: "stationary plane", words: plane.Words.ToArray(), projected: 8, origin: 0, acceptedStart: 7.75f, depth: 8);
        Check(name: "near occluder", words: blocked.Words.ToArray(), projected: 8, origin: 0, acceptedStart: 2, depth: 3.875f);
        Check(name: "independent near part", words: independent.Build(buildInstanceGrid: false).Words.ToArray(),
            projected: 12, origin: -8, acceptedStart: 2, depth: 7.75f, parts: true);
        Check(name: "full exhaustion budget", words: parallel.Words.ToArray(), projected: -8, origin: 0, acceptedStart: 2, depth: null);

        // A zero/invalid raw bound must not borrow the ordinary reader's unit-scale fallback as proof provenance.
        foreach (var invalid in new[] { 0f, -1f, float.NaN }) {
            var words = plane.Words.ToArray();
            var segment = (((int)words[SdfProgram.ProgramMaterialOffsetLane])
                + (SdfProgram.MaterialVectorsPerEntry * ((int)words[SdfProgram.ProgramMaterialCountLane]))
                + (SdfProgram.BoundRecordVectors * plane.InstructionCount));

            words[((segment * 4) + SdfProgram.SegmentStepScaleLane)] = BitConverter.SingleToUInt32Bits(value: invalid);
            Check(name: $"unavailable bound {invalid}", words: words, projected: 8, origin: 0, acceptedStart: 2, depth: 8, proofQueries: 0);
        }

        void Check(string name, uint[] words, float projected, float origin, float acceptedStart, float? depth, bool parts = false, int proofQueries = 1) {
            Vector3[] cases = [new(x: 0, y: origin, z: projected), new(x: 1, y: origin, z: projected),
                new(x: 2, y: origin, z: projected), new(x: 3, y: origin, z: projected),
                new(x: 4, y: origin, z: projected), new(x: 5, y: origin, z: projected)];
            var result = Run(kernel: kernel, services: services, points: cases,
                legs: [new SdfFieldLeg(Name: name, Words: words, Distances: [], Materials: [])]);
            var baseline = result[0];
            var seeded = result[1];
            var baselineCounts = result[2];
            var seededCounts = result[3];

            Assert.True(condition: (seededCounts.Z == acceptedStart), userMessage: $"{name}: admitted start {seededCounts.Z}, expected {acceptedStart}");
            Assert.Equal(expected: (parts ? 1f : 0f), actual: seededCounts.W);
            Assert.Equal(expected: baselineCounts.X, actual: baselineCounts.Y);
            Assert.Equal(expected: seededCounts.X, actual: seededCounts.Y);
            Assert.True(condition: (seededCounts.X == (baselineCounts.X + proofQueries)),
                userMessage: $"{name}: queries {baselineCounts.X} -> {seededCounts.X}, expected one counted proof or none when unavailable");
            Assert.Equal(expected: baseline, actual: seeded);
            Assert.Equal(expected: result[4], actual: result[5]);
            if (depth is { } expected) {
                Assert.Equal(expected: expected, actual: seeded.X);
                Assert.Equal(expected: 1f, actual: seeded.Z);
            } else {
                Assert.Equal(expected: 128f, actual: seeded.Y);
                Assert.Equal(expected: 128f, actual: baselineCounts.X);
                Assert.Equal(expected: 0f, actual: seeded.Z);
            }
        }
    }
}
