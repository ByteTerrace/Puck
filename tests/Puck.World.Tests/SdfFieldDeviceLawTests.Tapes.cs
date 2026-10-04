using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    [Fact]
    public void VulkanTileTapesPreserveEverySampleAndCountLessShapeWork() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyTileTapes(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXTileTapesPreserveEverySampleAndCountLessShapeWork() {
        using var device = DirectXTestDevices.Hardware();

        VerifyTileTapes(services: device.Services, extension: ".dxil");
    }
    [Fact]
    public void VulkanNexusTileTapesPreserveTheFullMarchSamples() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyNexusTapes(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXNexusTileTapesPreserveTheFullMarchSamples() {
        using var device = DirectXTestDevices.Hardware();

        VerifyNexusTapes(services: device.Services, extension: ".dxil");
    }

    private static void VerifyTileTapes(GpuDeviceServices services, string extension) {
        var programs = TapePrograms().ToArray();
        var results = Run(kernel: GradientKernel(extension: extension, name: "sdf-tape.comp"), services: services,
            legs: programs.Select(selector: static pair => GradientLeg(name: pair.Name, program: pair.Program)).ToArray(),
            transforms: GradientTransforms,
            tapeWordsPerCase: programs.Max(selector: static pair => SdfWorldPackage.SegmentTapeWordCountFor(segments: pair.Program.SkipSegmentCount, tokens: pair.Program.TapeTokenCount)));

        AssertTapeResults(results: results);
        Assert.True(condition: (results.Sum(selector: static result => result.X) > 0),
            userMessage: "the pruned walk must save counted shape evaluations, including later winners discarding earlier shapes");
        for (var leg = 0; (leg < programs.Length); leg++) {
            if (programs[leg].Name.StartsWith(comparisonType: StringComparison.Ordinal, value: "required")) {
                Assert.True(condition: (results.AsSpan(start: (leg * Points.Length), length: Points.Length).ToArray().Sum(selector: static result => result.X) > 0),
                    userMessage: $"{programs[leg].Name}: this path must save counted evaluations itself");
            }
        }
    }
    private static void VerifyNexusTapes(GpuDeviceServices services, string extension) {
        var (program, transforms, directions, parameters) = NexusFieldProbe();
        var results = Run(kernel: GradientKernel(extension: extension, name: "sdf-tape-rays.comp"), services: services,
            legs: [GradientLeg(name: "Nexus", program: program)], points: directions, parameters: parameters,
            transforms: transforms,
            tapeWordsPerCase: SdfWorldPackage.SegmentTapeWordCountFor(segments: program.SkipSegmentCount, tokens: program.TapeTokenCount));

        AssertTapeResults(results: results);
    }
    private static void AssertTapeResults(Vector4[] results) {
        for (var index = 0; (index < results.Length); index++) {
            Assert.True(condition: (results[index].Y == 0),
                userMessage: $"sample ray {index}: distance, identity, lanes or smooth-material bits changed at {results[index].Y} points");
        }
        Assert.True(condition: (results.Sum(selector: static result => result.Z) >= 8), userMessage: "no meaningful samples read a built tape");
        Assert.True(condition: (results.Sum(selector: static result => result.W) > 0), userMessage: "the tape pass counted no shape evaluations");
    }
    private static IEnumerable<(string Name, SdfProgram Program)> TapePrograms() {
        yield return ("required shapes inside one scoped segment", Pack(emit: static (builder, material) => {
            builder.PushField().ResetPoint();
            for (var index = 0; (index < 40); index++) {
                builder.Sphere(radius: (2 + (2 * index)), material: material);
            }
            return builder.Sphere(radius: 0.2f, material: material, blend: SdfBlendOp.Intersection).PopField();
        }));
        yield return ("required dynamic certificates", Pack(emit: static (builder, material) => {
            for (var index = 0; (index < 40); index++) {
                builder.ResetPoint().TransformDynamic(slot: 0).Sphere(radius: (2 + (2 * index)), material: material);
            }
            return builder;
        }));
        yield return ("required clamped power envelopes", Pack(emit: static (builder, material) => {
            for (var index = 0; (index < 40); index++) {
                var radius = (2 + (2 * index));

                builder.ResetPoint().Superellipsoid(radii: new Vector3(x: radius, y: (0.5f * radius), z: radius),
                    exponent: 2.05f, material: material);
            }
            return builder;
        }));
        yield return ("required losing scope retains the parent", Pack(emit: static (builder, material) => {
            builder.ResetPoint().Sphere(radius: 200, material: material).PushField().ResetPoint();
            for (var index = 0; (index < 40); index++) {
                builder.Sphere(radius: (2 + (2 * index)), material: material);
            }
            return builder.PopField();
        }));
        yield return ("required smooth losing scope clears the parent seam", Pack(emit: static (builder, material) => {
            var other = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));

            builder.ResetPoint().Sphere(radius: 20, material: material)
                .Sphere(radius: 20.2f, material: other, blend: SdfBlendOp.SmoothUnion, smooth: 1)
                .PushField(compose: SdfBlendOp.SmoothUnion, smooth: 0.5f)
                .ResetPoint().Translate(offset: new Vector3(x: 100, y: 0, z: 0));
            for (var index = 0; (index < 40); index++) {
                builder.Sphere(radius: (0.1f * (index + 1)), material: material);
            }
            return builder.PopField();
        }));
        foreach (var separate in new[] { false, true }) {
            yield return ($"a sphere-bound smooth loser preserves the earlier seam, separate={separate}", Pack(emit: (builder, material) => {
                var other = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));

                builder.ResetPoint().Sphere(radius: 20, material: material)
                    .Sphere(radius: 20.2f, material: other, blend: SdfBlendOp.SmoothUnion, smooth: 1);
                for (var index = 0; (index < 40); index++) {
                    if (separate) { builder.ResetPoint(); }
                    builder.Translate(offset: new Vector3(x: (100 + index), y: 0, z: 0))
                        .Sphere(radius: 0.1f, material: material, blend: SdfBlendOp.SmoothUnion, smooth: 0.5f);
                }
                return builder;
            }));
        }
        var compiled = Pack(emit: static (builder, material) => {
            for (var placement = 0; (placement < 2); placement++) {
                builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 120);
                builder.PushField();
                for (var index = 0; (index < 40); index++) {
                    builder.ResetPoint().TransformDynamic(slot: (index + placement) & 1)
                        .Sphere(radius: (2 + (2 * index)), material: material);
                }
                builder.PopField();
                builder.EndInstance();
            }
            return builder.ResetPoint().Sphere(radius: 2, material: material);
        });
        const int VectorWords = 4;
        var words = compiled.Words;
        var segmentHeader = (((int)((words[SdfProgram.ProgramMaterialOffsetLane]
            + (SdfProgram.MaterialVectorsPerEntry * words[SdfProgram.ProgramMaterialCountLane]))
            + (SdfProgram.BoundRecordVectors * words[SdfProgram.ProgramInstructionCountLane]))) * VectorWords);
        var instanceHeader = (segmentHeader + ((SdfProgram.DirectoryHeaderVectors
            + (SdfProgram.BoundRecordVectors * compiled.SkipSegmentCount)) * VectorWords));
        var partHeader = (((int)words[(instanceHeader + SdfProgram.InstancePartProgramsLane)]) * VectorWords);

        Assert.NotEqual(actual: partHeader, expected: 0);
        Assert.Equal(expected: 2u, actual: words[partHeader] & 0x7FFFFFFFu);
        Assert.Equal(expected: words[(partHeader + VectorWords)], actual: words[(partHeader + (2 * VectorWords))]);
        yield return ("required compiled placements read their own instruction masks", compiled);
        yield return ("compiled scope retained before later winners", Pack(emit: static (builder, material) => {
            builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 4);
            builder.PushField();
            builder.ResetPoint().Scale(scale: new Vector3(x: 1.1f, y: 0.9f, z: 1.3f))
                .Sphere(radius: 1, material: material);
            builder.ResetPoint().Sphere(radius: 0.5f, material: material, blend: SdfBlendOp.Subtraction);
            builder.PopField();
            builder.EndInstance();
            for (var index = 0; (index < 16); index++) {
                builder.ResetPoint().Sphere(radius: (0.1f * (index + 1)), material: material);
            }
            return builder;
        }));
        yield return ("late union winners", Pack(emit: static (builder, material) => {
            for (var index = 0; (index < 40); index++) {
                builder.ResetPoint().Sphere(radius: (2 + (2 * index)), material: material);
            }
            return builder;
        }));
        yield return ("required huge cancelling field offsets retain later winners", Pack(emit: static (builder, material) => {
            for (var index = 0; (index < 40); index++) {
                builder.ResetPoint().Sphere(radius: (2 + (2 * index)), material: material);
            }
            return builder.Dilate(radius: 1e31f).Dilate(radius: -1e31f).ResetPoint().Sphere(radius: 2, material: material);
        }));
        foreach (var blend in new[] { SdfBlendOp.SmoothUnion, SdfBlendOp.Intersection, SdfBlendOp.Subtraction,
            SdfBlendOp.SmoothIntersection, SdfBlendOp.SmoothSubtraction }) {
            yield return (blend.ToString(), Pack(emit: (builder, material) => {
                for (var index = 0; (index < 16); index++) {
                    builder.ResetPoint().Sphere(radius: (0.05f * (index + 1)), material: material);
                }
                builder.ResetPoint().Translate(offset: new Vector3(x: 0.2f, y: 0, z: 0))
                    .Sphere(radius: 0.8f, material: material, blend: SdfBlendOp.SmoothUnion, smooth: 0.4f);
                return builder.ResetPoint().Translate(offset: new Vector3(x: 8, y: 0, z: 0))
                    .Sphere(radius: 0.3f, material: material, blend: blend, smooth: 0.3f);
            }));
        }
        yield return ("scaled and unmodelled candidates", Pack(emit: static (builder, material) => {
            for (var index = 0; (index < 16); index++) {
                builder.ResetPoint().Sphere(radius: (0.05f * (index + 1)), material: material);
            }
            builder.ResetPoint().Scale(scale: new Vector3(x: 0.025f, y: 4, z: 0.125f))
                .Sphere(radius: 0.8f, material: material, blend: SdfBlendOp.SmoothUnion, smooth: 0.1f);
            return builder.ResetPoint().Shear(linear: 0.7f, quadratic: 0.3f)
                .Sphere(radius: 0.7f, material: material, blend: SdfBlendOp.SmoothUnion, smooth: 0.2f);
        }));
        yield return ("detail selection differs from the tape", Pack(emit: static (builder, material) => {
            for (var index = 0; (index < 16); index++) {
                builder.ResetPoint().Sphere(radius: (0.05f * (index + 1)), material: material);
            }
            return builder.ResetPoint().Sphere(radius: 3, material: material, detail: true);
        }));
        yield return ("secondary selection differs from the tape", Pack(emit: static (builder, material) => {
            for (var index = 0; (index < 16); index++) {
                builder.ResetPoint().Sphere(radius: (0.05f * (index + 1)), material: material);
            }
            return builder.ResetPoint().Sphere(radius: 3, material: material).MarkSecondary(secondary: false);
        }));
    }
}
