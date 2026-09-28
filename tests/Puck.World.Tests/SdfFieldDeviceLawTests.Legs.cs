using System.Numerics;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    // The shape after a warp: a rounded box whose three half-extents differ, so a warp read from the wrong lane moves it.
    private static readonly Func<SdfProgramBuilder, int, SdfProgramBuilder> WarpedBox = static (b, m) =>
        b.Box(halfExtents: new Vector3(x: 0.55f, y: 0.35f, z: 0.2f), material: m, round: 0.05f);

    // The oracles only a point's warp describes are right when the equivalent the reference takes lands the point where
    // the op's warp does: the evaluator over the equivalent program at a point equals the evaluator over the shape alone
    // at the warped point.
    [Fact]
    public void EveryEquivalentTakesThePointWhereItsOpDoes() {
        var failures = new List<string>();

        foreach (var oracle in Oracles().Where(predicate: static oracle => (oracle.Warp is not null))) {
            var shape = Evaluator(program: Pack(emit: (b, m) => oracle.Tail(arg1: b.ResetPoint(), arg2: m)))!;

            foreach (var point in Points) {
                var equivalent = Reference(
                    oracle: oracle,
                    point: point
                );
                var warped = Reference(
                    evaluator: shape,
                    point: oracle.Warp!(arg: point)
                );

                if (!(MathF.Abs(x: (equivalent.Distance - warped.Distance)) <= 1e-4f)) {
                    failures.Add(item: $"{oracle.Name} at {point}: the equivalent answers {equivalent.Distance}, the shape at the warped point {warped.Distance}");
                }
            }
        }

        Assert.Empty(collection: failures);
    }

    // Every leg the device law evaluates: the accepted probe calls, the refused ops' oracles, and the swapped words.
    private static List<SdfFieldLeg> Legs() => [.. AcceptedLegs(), .. OracleLegs(), .. SwappedLegs()];
    private static IEnumerable<SdfFieldLeg> AcceptedLegs() {
        foreach (var call in SdfEncodingProbe.Calls()) {
            var program = SdfEncodingProbe.Build(call: call);

            if (Evaluator(program: program) is not { } evaluator) {
                continue;
            }

            var (distances, materials) = Reference(evaluator: evaluator);

            yield return new SdfFieldLeg(
                Distances: distances,
                Materials: materials,
                Name: call.Name,
                Words: program.Words.ToArray()
            );
        }

        // The probe rotates a sphere, which reads the same under any rotation; the rigid leaf the swapped leg exchanges a
        // rotation's lanes in rotates a box, held to its reference here first.
        var rotated = RotatedBox();
        var reference = Reference(evaluator: Evaluator(program: rotated)!);

        yield return new SdfFieldLeg(
            Distances: reference.Distances,
            Materials: reference.Materials,
            Name: "rotate, a box",
            Words: rotated.Words.ToArray()
        );
    }
    // A box behind a rotation, packed as a rigid leaf.
    private static SdfProgram RotatedBox() =>
        Pack(emit: static (b, m) => WarpedBox(arg1: b.ResetPoint().Rotate(rotation: new Quaternion(w: 0.7f, x: 0.4f, y: 0.5f, z: 0.6f)), arg2: m));
    private static IEnumerable<SdfFieldLeg> OracleLegs() {
        foreach (var oracle in Oracles()) {
            var (distances, materials) = Reference(oracle: oracle);

            yield return new SdfFieldLeg(
                Distances: distances,
                Materials: materials,
                Name: oracle.Name,
                Words: Program(oracle: oracle).Words.ToArray()
            );
        }
    }
    // The red leg: programs whose words carry two lanes exchanged, each against its unexchanged program's reference.
    private static IEnumerable<SdfFieldLeg> SwappedLegs() {
        var calls = SdfEncodingProbe.Calls();

        SdfProgram Called(string name) =>
            SdfEncodingProbe.Build(call: calls.Single(predicate: call => (call.Name == name)));
        SdfFieldLeg Swapped(string name, SdfProgram program, uint[] words, (float[] Distances, int[] Materials) reference) {
            Assert.NotEqual(expected: program.Words.ToArray(), actual: words);

            return new SdfFieldLeg(
                Distances: reference.Distances,
                Materials: reference.Materials,
                Name: name,
                Swapped: true,
                Words: words
            );
        }

        var rotate = RotatedBox();
        var box = Called(name: "box");

        yield return Swapped(
            name: "rotate, its rigid leaf's rotation X and Y exchanged",
            program: rotate,
            reference: Reference(evaluator: Evaluator(program: rotate)!),
            words: SdfEncodingTrades.RigidLeafRotationXySwapped(program: rotate)
        );
        yield return Swapped(
            name: "box, its half-extents X and Y exchanged",
            program: box,
            reference: Reference(evaluator: Evaluator(program: box)!),
            words: DataLanesExchanged(first: 0, op: SdfOp.ShapeBlend, program: box, second: 1)
        );

        // A plane rotation and a shear with the two enum lanes they carry exchanged: a plane read as a driver and a
        // driver as a plane, a target as a driver and a driver as a target.
        foreach (var (name, op) in (((string, SdfOp)[])[("rotate-plane XY by Z", SdfOp.RotatePlane), ("shear X by Z", SdfOp.Shear)])) {
            var oracle = Oracles().Single(predicate: oracle => (oracle.Name == name));
            var program = Program(oracle: oracle);

            yield return Swapped(
                name: $"{name}, its shape and blend lanes exchanged",
                program: program,
                reference: Reference(oracle: oracle),
                words: HeaderLanesExchanged(first: SdfProgram.InstructionShapeLane, op: op, program: program, second: SdfProgram.InstructionBlendLane)
            );
        }
    }
    // The evaluator over a program, or null when it refuses the program or the program has no field (every shape a
    // detail shape, which the evaluator leaves out).
    private static SdfFieldEvaluator? Evaluator(SdfProgram program) {
        try {
            var evaluator = new SdfFieldEvaluator(program: program);

            return (evaluator.TryDistance(
                distance: out _,
                material: out _,
                position: default
            ) ? evaluator : null);
        } catch (ArgumentException) {
            return null;
        }
    }
    // A program of one material, emitted by a call.
    private static SdfProgram Pack(Func<SdfProgramBuilder, int, SdfProgramBuilder> emit) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = emit(arg1: builder, arg2: material);

        return builder.Build();
    }
    private static SdfProgram Program(SdfFieldOracle oracle) =>
        Pack(emit: (b, m) => ((oracle.Relief is null)
            ? oracle.Tail(arg1: oracle.Op(arg: b.ResetPoint()), arg2: m)
            : oracle.Op(arg: oracle.Tail(arg1: b.ResetPoint(), arg2: m))));
    private static (float[] Distances, int[] Materials) Reference(SdfFieldOracle oracle) {
        var distances = new float[Points.Length];
        var materials = new int[Points.Length];

        for (var point = 0; (point < Points.Length); point++) {
            (distances[point], materials[point]) = Reference(
                oracle: oracle,
                point: Points[point]
            );
        }

        return (distances, materials);
    }
    // The evaluator over the op's equivalent at one point.
    private static (float Distance, int Material) Reference(SdfFieldOracle oracle, Vector3 point) {
        SdfProgram equivalent;

        if (oracle.Relief is not null) {
            var relief = oracle.Relief(arg: point);

            equivalent = Pack(emit: (b, m) => oracle.Tail(arg1: b.ResetPoint(), arg2: m).Dilate(radius: -relief));
        } else if (oracle.Axis != Vector3.Zero) {
            var rotation = RotationTaking(
                axis: oracle.Axis,
                from: oracle.Warp!(arg: point),
                to: point
            );

            equivalent = Pack(emit: (b, m) => oracle.Tail(arg1: b.ResetPoint().Rotate(rotation: rotation), arg2: m));
        } else {
            var offset = (point - oracle.Warp!(arg: point));

            equivalent = Pack(emit: (b, m) => oracle.Tail(arg1: b.ResetPoint().Translate(offset: offset), arg2: m));
        }

        return Reference(
            evaluator: new SdfFieldEvaluator(program: equivalent),
            point: point
        );
    }
    // The rotation about an axis that takes one point to another at the same height along it and the same distance from
    // it: a Rotate by it reads the second point where the first is, since the interpreter rotates the point by the
    // inverse.
    private static Quaternion RotationTaking(Vector3 axis, Vector3 from, Vector3 to) {
        var fromAcross = (from - (Vector3.Dot(vector1: from, vector2: axis) * axis));
        var toAcross = (to - (Vector3.Dot(vector1: to, vector2: axis) * axis));
        var angle = MathF.Atan2(
            x: Vector3.Dot(vector1: fromAcross, vector2: toAcross),
            y: Vector3.Dot(vector1: axis, vector2: Vector3.Cross(vector1: fromAcross, vector2: toAcross))
        );

        return Quaternion.CreateFromAxisAngle(
            angle: angle,
            axis: axis
        );
    }
    // The words of a program with two lanes of its first instruction of an op's header exchanged.
    private static uint[] HeaderLanesExchanged(SdfProgram program, SdfOp op, int first, int second) {
        var words = program.Words.ToArray();
        var header = ((SdfProgram.ProgramHeaderVectors + InstructionOf(op: op, program: program)) * 4);

        (words[(header + first)], words[(header + second)]) = (words[(header + second)], words[(header + first)]);

        return words;
    }
    // The words of a program with two lanes of its first instruction of an op's first data vector exchanged.
    private static uint[] DataLanesExchanged(SdfProgram program, SdfOp op, int first, int second) {
        var words = program.Words.ToArray();
        var data = ((((int)words[SdfProgram.ProgramDataOffsetLane]) + (SdfProgram.InstructionDataVectors * InstructionOf(op: op, program: program))) * 4);

        (words[(data + first)], words[(data + second)]) = (words[(data + second)], words[(data + first)]);

        return words;
    }
    private static int InstructionOf(SdfProgram program, SdfOp op) {
        var instructions = program.Instructions;

        for (var index = 0; (index < instructions.Count); index++) {
            if (instructions[index].Op == op) {
                return index;
            }
        }

        throw new InvalidOperationException(message: $"The program carries no {op}.");
    }

    // An op the evaluator refuses, emitted before the tail when it warps the point (Warp, a rotation about Axis when
    // Axis is set and a translation otherwise) or after it when it adds a relief to the field (Relief).
    private sealed record SdfFieldOracle(
        string Name,
        Func<SdfProgramBuilder, SdfProgramBuilder> Op,
        Func<SdfProgramBuilder, int, SdfProgramBuilder> Tail,
        Func<Vector3, Vector3>? Warp = null,
        Vector3 Axis = default,
        Func<Vector3, float>? Relief = null
    );
}
