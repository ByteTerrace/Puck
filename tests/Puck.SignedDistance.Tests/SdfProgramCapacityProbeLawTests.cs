using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>Laws for the capacity probe's build, which skips the tile-tape certificates: a probe is read for the buffer
/// capacities it measures, and no certificate's value changes a capacity.</summary>
public sealed class SdfProgramCapacityProbeLawTests {
    // Instances of translated, rotated and scaled shapes, which certify, beside a reserved path table, which only a
    // probe reserves: the parts of a program a capacity depends on.
    private static SdfProgramBuilder Scene() {
        var builder = new SdfProgramBuilder();
        var stone = builder.AddMaterial(material: new(Vector3.One));
        var moss = builder.AddMaterial(material: new(new Vector3(x: 0.2f, y: 0.6f, z: 0.2f)));

        builder.ReservePathTables(shapeCount: 3);
        for (var index = 0; (index < 6); index++) {
            _ = builder.BeginInstance(boundCenter: new Vector3(x: (index * 3f), y: 0f, z: 0f), boundRadius: 2f);
            _ = builder.ResetPoint();
            _ = builder.Translate(offset: new Vector3(x: (index * 3f), y: 1f, z: 0.5f));
            _ = builder.Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (0.3f * (index + 1))));
            _ = builder.Box(halfExtents: new Vector3(x: 0.5f, y: 0.75f, z: 0.5f), material: stone, round: 0.05f);
            _ = builder.Sphere(blend: SdfBlendOp.SmoothUnion, material: moss, radius: 0.6f, smooth: 0.1f);
            _ = builder.EndInstance();
        }
        return builder;
    }

    [Fact]
    public void AProbeBuildMeasuresTheSameCapacitiesAsTheFullBuild() {
        var scene = Scene();
        var full = scene.Build();
        var probe = scene.Build(packTapeCertificates: false);

        Assert.Equal(actual: probe.PartCompilationWordCapacity, expected: full.PartCompilationWordCapacity);
        Assert.Equal(actual: probe.Instances.Count, expected: full.Instances.Count);
        Assert.Equal(actual: probe.InstructionCount, expected: full.InstructionCount);
        Assert.Equal(actual: probe.Words.Length, expected: full.Words.Length);
        Assert.True(condition: (full.Instances.Count > 0), userMessage: "The scene must declare instances for the capacity to be measured over them.");
    }
    [Fact]
    public void AProbeBuildPacksNoCertificateWhereTheFullBuildCertifiesShapes() {
        var scene = Scene();
        var full = scene.Build();
        var probe = scene.Build(packTapeCertificates: false);
        var certified = 0;

        for (var instruction = 0; (instruction < full.InstructionCount); instruction++) {
            if (full.TapeCertificate(instruction: instruction).Flags != 0U) { certified++; }
            Assert.Equal(actual: probe.TapeCertificate(instruction: instruction), expected: new SdfTapeCertificate(ErrorOffset: 0f, ErrorSlope: 0f, Flags: 0U, Lipschitz: 0f));
        }
        Assert.True(condition: (certified > 0), userMessage: "The full build must certify at least one instruction, or the probe's zero proves nothing.");
    }
    [Fact]
    public void AProbeBuildPacksEveryOtherWordAsTheFullBuildDoes() {
        var scene = Scene();
        var full = scene.Build();
        var probe = scene.Build(packTapeCertificates: false);
        var differing = 0;

        for (var word = 0; (word < full.Words.Length); word++) {
            if (full.Words[word] != probe.Words[word]) { differing++; }
        }

        // The certificates are four words per instruction and the tokens a few per instance; nothing else may differ.
        Assert.InRange(actual: differing, low: 1, high: (((4 * full.InstructionCount) + (4 * full.Instances.Count)) + 16));
        Assert.Equal(actual: probe.Instructions, expected: full.Instructions);
        Assert.Equal(actual: probe.Instances, expected: full.Instances);
    }
}
