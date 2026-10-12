using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The key law for a program's chains and specialization set (<see cref="SdfProgram.Specialization"/>): every op has its
/// code-selecting lane classification; a segment's chains cover its instructions exactly and key each by its op and
/// code-selecting lanes; an edit that moves only data, including one that merges or splits directory segments, keeps the
/// set; a changed shape type, blend, compose form or code-selecting lane changes it; and two distinct chains of one
/// program that share a 32-bit identity are refused by name.
/// </summary>
public sealed class SdfProgramChainLawTests {
    private static readonly IReadOnlyList<SdfEncodingProbeCall> Calls = SdfEncodingProbe.Calls();
    // Ops whose every lane is data, which a search for colliding chains strings together.
    private static readonly SdfOp[] PointOps = [SdfOp.Translate, SdfOp.Rotate, SdfOp.Scale, SdfOp.Elongate];

    [Fact]
    public void EveryOpHasALaneClassificationAndTheComposesSelectTheirVocabulary() {
        Assert.All(
            action: static op => _ = SdfOpRoles.CodeLanesOf(op: op),
            collection: Enum.GetValues<SdfOp>()
        );
        Assert.Equal(expected: SdfInstructionLanes.Shape | SdfInstructionLanes.Blend, actual: SdfOpRoles.CodeLanesOf(op: SdfOp.ShapeBlend));
        Assert.Equal(expected: SdfInstructionLanes.Blend, actual: SdfOpRoles.CodeLanesOf(op: SdfOp.PopField));
    }
    // Each directory segment's chains are its instructions in order: every non-compose instruction a step holding its op
    // and code-selecting lanes, a shape or pop closing the chain it ends, and a cut only where a segment range ends.
    [Fact]
    public void ASegmentsChainsCoverItsInstructionsExactly() {
        foreach (var call in Calls) {
            var program = SdfEncodingProbe.Build(call: call);
            var words = program.Words;
            var directory = SegmentDirectoryOf(program: program);

            for (var segment = 0; (segment < program.SkipSegmentCount); segment++) {
                var meta = ((((directory + SdfProgram.DirectoryHeaderVectors) + (SdfProgram.BoundRecordVectors * segment)) + 1) * 4);
                var index = ((int)words[(meta + 2)]);

                foreach (var chain in program.ChainsOf(segment: segment)) {
                    foreach (var step in chain.Steps) {
                        Assert.Equal(expected: SdfChainStep.Of(instruction: program.Instructions[index]), actual: step);
                        index++;
                    }

                    var end = ((index < program.InstructionCount) ? program.Instructions[index].Op : SdfOp.ResetPoint);

                    switch (chain.End) {
                        case SdfChainEnd.Shape:
                            Assert.Equal(actual: end, expected: SdfOp.ShapeBlend);
                            index++;
                            break;
                        case SdfChainEnd.Pop:
                            Assert.Equal(actual: end, expected: SdfOp.PopField);
                            index++;
                            break;
                        default:
                            Assert.True(condition: ((index == ((int)words[(meta + 3)])) || (end == SdfOp.ResetPoint)), userMessage: $"{call.Name}: a chain is cut at instruction {index}, inside a segment range.");
                            break;
                    }
                }

                Assert.Equal(expected: ((int)words[(meta + 3)]), actual: index);
            }

            var set = program.Specialization;

            Assert.Equal(expected: program.Instructions.Where(predicate: static instruction => (instruction.Op == SdfOp.ShapeBlend)).Select(selector: static instruction => ((SdfShapeType)instruction.Shape)).Distinct().Order(), actual: set.Shapes);
            Assert.Equal(expected: program.Instructions.Where(predicate: static instruction => (instruction.Op == SdfOp.PopField)).Select(selector: static instruction => ((SdfBlendOp)instruction.Blend)).Distinct().Order(), actual: set.Composes);
            Assert.Equal(expected: program.Instructions.Where(predicate: static instruction => (instruction.Op == SdfOp.ShapeBlend)).Select(selector: static instruction => ((SdfBlendOp)instruction.Blend)).Distinct().Order(), actual: set.Blends);
        }
    }
    // Raising any probe input moves data: wherever the raised program runs the same ops in the same order, it keeps its
    // chains and vocabulary, and so its whole set, however its bounds, merges and rigid plans move.
    [Fact]
    public void ADataEditKeepsTheSet() {
        var compared = 0;

        foreach (var call in Calls) {
            var baseline = SdfEncodingProbe.Build(call: call);

            for (var input = 0; (input < call.Inputs.Count); input++) {
                var raised = call.Inputs.ToArray();

                raised[input] = ((raised[input] == MathF.Floor(x: raised[input])) ? (raised[input] + 1f) : (raised[input] * 1.0625f));

                var edited = SdfEncodingProbe.Build(call: call with { Inputs = raised });

                if (!edited.Instructions.Select(selector: static instruction => instruction.Op).SequenceEqual(second: baseline.Instructions.Select(selector: static instruction => instruction.Op))) {
                    continue;
                }

                Assert.True(condition: baseline.Specialization.Equals(other: edited.Specialization), userMessage: $"{call.Name} input {input}: [{Named(set: baseline.Specialization)}] became [{Named(set: edited.Specialization)}].");
                Assert.Equal(expected: baseline.ChainCount, actual: edited.ChainCount);
                compared++;
            }
        }

        Assert.True(condition: (compared > 200), userMessage: $"Only {compared} data edits kept their op sequence.");
    }
    // Two reset ranges close enough to merge into one directory segment run the same chains as when they stand apart,
    // the first range's trailing translate cut where its range ends rather than joined to the next range's chain.
    [Fact]
    public void AMergeOfSegmentsKeepsTheChains() {
        static SdfProgram TwoSpheres(float x) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            return builder
                .ResetPoint().Translate(offset: new Vector3(x: 1f, y: 0f, z: 0f)).Sphere(radius: 0.5f, material: material)
                .Translate(offset: new Vector3(x: 0f, y: 0.25f, z: 0f))
                .ResetPoint().Translate(offset: new Vector3(x: x, y: 0f, z: 0f)).Sphere(radius: 0.5f, material: material)
                .Build();
        }

        var merged = TwoSpheres(x: 1.1f);
        var apart = TwoSpheres(x: 100f);

        Assert.Equal(expected: (1, 2), actual: (merged.SkipSegmentCount, apart.SkipSegmentCount));
        Assert.Equal(expected: 3, actual: merged.ChainsOf(segment: 0).Length);
        Assert.Equal(expected: SdfChainEnd.Cut, actual: merged.ChainsOf(segment: 0)[1].End);
        Assert.Equal(expected: apart.Specialization, actual: merged.Specialization);
        Assert.Equal(expected: apart.ChainsOf(segment: 1)[0], actual: merged.ChainsOf(segment: 0)[2]);
    }
    // Every probe family that enumerates a lane's members (a rotate-plane's plane and driver, a wallpaper fold's group and
    // plane, a cell jitter's flavor, a polar repeat's, axial profile's and shear's axes, a cell displacement's mode, a
    // shape's blend and type, a scope's compose form) holds a different set for every member; so does a polar repeat's
    // mirror flag.
    [Fact]
    public void AChangedShapeBlendComposeOrCodeLaneChangesTheSet() {
        var families = new (string Family, string[] Names)[] {
            ("rotate-plane", Names(prefix: "rotate-plane ")),
            ("wallpaper-fold", Names(prefix: "wallpaper-fold ")),
            ("cell-jitter", Names(prefix: "cell-jitter ")),
            ("repeat-polar", Names(prefix: "repeat-polar ")),
            ("axial-profile", Names(prefix: "axial-profile ")),
            ("shear", Names(prefix: "shear ")),
            ("cell-displace", Names(prefix: "cell-displace ")),
            ("blend", Names(prefix: "blend ")),
            ("field-scope", [.. Names(prefix: "field-scope ").Where(predicate: static name => (name != "field-scope nested cut"))]),
            ("shape", ["box", "capsule", "sphere", "torus", "cylinder", "plane", "vesica", "round-cone", "superellipsoid"]),
        };

        foreach (var (family, names) in families) {
            var sets = names.Select(selector: static name => SdfEncodingProbe.Build(call: Calls.Single(predicate: call => (call.Name == name))).Specialization).ToArray();

            Assert.True(condition: (sets.Length > 1), userMessage: $"The {family} family enumerates no members.");
            Assert.Equal(expected: sets.Length, actual: sets.Distinct().Count());
        }

        static SdfSpecializationSet Polar(bool mirror) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            return builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0f, z: 3f)).RepeatPolar(count: 6, mirror: mirror).Sphere(radius: 1f, material: material).Build().Specialization;
        }

        Assert.NotEqual(expected: Polar(mirror: false), actual: Polar(mirror: true));
    }
    // Strings of point ops whose lanes are all data are keyed by their ops alone; the first two such chains that share a
    // 32-bit identity, run in one program, are refused naming both, while each runs alone.
    [Fact]
    public void TwoChainsSharingAnIdentityAreRefusedByName() {
        var (first, second) = CollidingChains();

        Assert.Equal(expected: first.Identity, actual: second.Identity);
        Assert.NotEqual(actual: second, expected: first);
        Assert.Contains(collection: Program(chains: [first]).Specialization.Chains, expected: first);
        Assert.Contains(collection: Program(chains: [second]).Specialization.Chains, expected: second);

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => Program(chains: [first, second]));

        Assert.Contains(expectedSubstring: first.ToString(), actualString: refusal.Message);
        Assert.Contains(expectedSubstring: second.ToString(), actualString: refusal.Message);
        Assert.Contains(expectedSubstring: $"0x{first.Identity:X8}", actualString: refusal.Message);
    }
    // A chain's identity is its key's: the same steps and end name the same chain on every build, and a different end
    // names a different one.
    [Fact]
    public void AChainsIdentityIsItsKeys() {
        SdfChainStep[] steps = [new(Blend: 0u, Op: SdfOp.ResetPoint, Shape: 0u), new(Blend: 1u, Op: SdfOp.RotatePlane, Shape: 2u)];

        Assert.Equal(expected: new SdfChain(end: SdfChainEnd.Shape, steps: steps).Identity, actual: new SdfChain(end: SdfChainEnd.Shape, steps: steps).Identity);
        Assert.NotEqual(expected: new SdfChain(end: SdfChainEnd.Shape, steps: steps).Identity, actual: new SdfChain(end: SdfChainEnd.Pop, steps: steps).Identity);
        Assert.Throws<ArgumentException>(testCode: () => new SdfChain(end: SdfChainEnd.Shape, steps: [new SdfChainStep(Blend: 0u, Op: SdfOp.CellJitter, Shape: 7u)]));
        Assert.Throws<ArgumentException>(testCode: () => new SdfChain(end: SdfChainEnd.Shape, steps: [new SdfChainStep(Blend: 0u, Op: SdfOp.ShapeBlend, Shape: 0u)]));
    }

    // Searches strings of data-only point ops, shortest first, for the first two that share an identity.
    private static (SdfChain First, SdfChain Second) CollidingChains() {
        var seen = new Dictionary<uint, SdfChain>();

        for (var length = 1; ; length++) {
            var count = (1L << (2 * length));

            for (var code = 0L; (code < count); code++) {
                var steps = new SdfChainStep[length];
                var digits = code;

                for (var index = 0; (index < length); index++) {
                    steps[index] = new SdfChainStep(Blend: 0u, Op: PointOps[((int)(digits & 3L))], Shape: 0u);
                    digits >>= 2;
                }

                var chain = new SdfChain(end: SdfChainEnd.Shape, steps: steps);

                if (seen.TryGetValue(key: chain.Identity, value: out var earlier)) {
                    return (earlier, chain);
                }

                seen.Add(key: chain.Identity, value: chain);
            }
        }
    }
    // A program that runs each chain after a first shape, in one segment.
    private static SdfProgram Program(IReadOnlyList<SdfChain> chains) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.ResetPoint().Sphere(radius: 0.5f, material: material);

        foreach (var chain in chains) {
            foreach (var step in chain.Steps) {
                _ = step.Op switch {
                    SdfOp.Translate => builder.Translate(offset: new Vector3(x: 0.1f, y: 0.2f, z: 0.3f)),
                    SdfOp.Rotate => builder.Rotate(rotation: Quaternion.CreateFromAxisAngle(angle: 0.3f, axis: Vector3.UnitY)),
                    SdfOp.Scale => builder.Scale(scale: new Vector3(x: 1.1f, y: 1.2f, z: 1.3f)),
                    _ => builder.Elongate(extents: new Vector3(x: 0.1f, y: 0.2f, z: 0.3f)),
                };
            }

            builder.Sphere(radius: 0.5f, material: material);
        }

        return builder.Build();
    }
    // A set's description and the name of each of its chains.
    private static string Named(SdfSpecializationSet set) =>
        $"{set.Describe()}: {string.Join(separator: "; ", values: set.Chains)}";
    private static string[] Names(string prefix) =>
        [.. Calls.Select(selector: static call => call.Name).Where(predicate: name => name.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix))];
    // The vector offset of a program's segment directory, after the per-instruction bound table.
    private static int SegmentDirectoryOf(SdfProgram program) {
        var words = program.Words;

        return ((((int)words[SdfProgram.ProgramMaterialOffsetLane]) + (SdfProgram.MaterialVectorsPerEntry * ((int)words[SdfProgram.ProgramMaterialCountLane]))) + (SdfProgram.BoundRecordVectors * program.InstructionCount));
    }
}
