using System.Numerics;
using Puck.SignedDistance;

namespace Puck.Testing;

// Probe calls with one field each put elsewhere, spelled at the call, which is what the encoding description sees of a
// builder or packer writing one field where the other goes: a rotation's Y and W, a sampled region's Y and Z dimension
// bitfields, a weathering's Edge and Lines, a sweep's start and end radii, a cell displacement's frequency and
// amplitude, and a stroked path's start and end radii.
internal static class SdfEncodingTrades {
    public static IReadOnlyList<SdfEncodingProbeCall> Traded(IReadOnlyList<SdfEncodingProbeCall> calls) {
        SdfEncodingProbeCall Named(string name) =>
            calls.Single(predicate: call => (call.Name == name));
        SdfEncodingProbeCall Exchanged(string name, int first, int second) {
            var original = Named(name: name);

            return original with {
                Emit = (b, m, v) => {
                    var exchanged = v.ToArray();

                    (exchanged[first], exchanged[second]) = (exchanged[second], exchanged[first]);
                    original.Emit(arg1: b, arg2: m, arg3: exchanged);
                },
            };
        }

        return [
            Named(name: "rotate") with { Emit = static (b, m, v) => b.ResetPoint().Rotate(rotation: new Quaternion(w: v[1], x: v[0], y: v[3], z: v[2])).Sphere(material: m, radius: 1f) },
            Named(name: "sampled-region") with { Emit = static (b, m, v) => b.ResetPoint().SampledRegion(boundaryFloor: v[8], boxMin: new Vector3(x: v[0], y: v[1], z: v[2]), brickWordOffset: ((int)v[7]), cellSize: v[3], dimX: ((int)v[4]), dimY: ((int)v[6]), dimZ: ((int)v[5]), material: m) },
            Exchanged(first: 39, name: "material", second: 42),
            Named(name: "sweep") with { Emit = static (b, m, v) => b.ResetPoint().Sweep(a: Vector3.Zero, b: new Vector3(x: v[0], y: v[1], z: 0f), bulge: v[5], c: new Vector3(x: v[2], y: 0f, z: 0f), material: m, radiusEnd: v[3], radiusStart: v[4], strandOffset: v[8], strands: ((int)v[6]), twist: v[7]) },
            Exchanged(first: 0, name: $"cell-displace {SdfCellMode.F1}", second: 1),
            Named(name: "path stroke") with { Emit = static (b, m, v) => b.ResetPoint().Path(halfDepth: v[2], material: m, profile: new SdfPathProfile(Contours: [new SdfPathContour(new Vector2(x: -0.4f, y: -0.3f), [new SdfPathSegment(new Vector2(x: 0.4f, y: -0.3f)), new SdfPathSegment(new Vector2(x: 0f, y: 0.35f))])], Stroke: new SdfPathStroke(RadiusEnd: v[3], RadiusStart: v[4])), scale: new Vector2(x: v[0], y: v[1])) },
        ];
    }
    // The words of a packer that writes every rigid leaf's rotation X into Y and Y into X: the rigid-leaf plan follows the
    // segment directory header's rigid-plan lane, one vector per segment, then three per leaf, the rotation the second.
    public static uint[] RigidLeafRotationXySwapped(SdfProgram program) {
        var words = program.Words.ToArray();
        var segmentOffset = SegmentDirectoryOf(program: program, words: words);
        var segmentCount = (int)words[((segmentOffset * 4) + SdfProgram.SegmentCountLane)];
        var planOffset = (int)words[((segmentOffset * 4) + SdfProgram.SegmentRigidPlanLane)];
        var leaves = 0;

        for (var segment = 0; (segment < segmentCount); segment++) {
            leaves += (int)words[(((planOffset + segment) * 4) + 1)];
        }
        for (var leaf = 0; (leaf < leaves); leaf++) {
            var rotation = (((planOffset + segmentCount) + (3 * leaf) + 1) * 4);

            (words[rotation], words[(rotation + 1)]) = (words[(rotation + 1)], words[rotation]);
        }

        return words;
    }
    // The words of a packer that writes every path edge's RadiusA where RadiusB goes and RadiusB where RadiusA goes: each
    // path's table starts at the vector its instruction's Data0.x bits name, two vectors an edge, the radii the second's
    // x and y.
    public static uint[] PathRadiiSwapped(SdfProgram program) {
        var words = program.Words.ToArray();

        foreach (var instruction in program.Instructions.Where(predicate: static instruction => ((instruction.Op == SdfOp.ShapeBlend) && ((instruction.Shape & SdfProgram.ShapeTypeMask) == ((uint)SdfShapeType.Path))))) {
            var table = (int)BitConverter.SingleToUInt32Bits(value: instruction.Data0.X);
            var edges = (int)instruction.Data0.Y;

            for (var edge = 0; (edge < edges); edge++) {
                var radii = ((table + (2 * edge) + 1) * 4);

                (words[radii], words[(radii + 1)]) = (words[(radii + 1)], words[radii]);
            }
        }

        return words;
    }

    private static int SegmentDirectoryOf(uint[] words, SdfProgram program) =>
        ((((int)words[SdfProgram.ProgramMaterialOffsetLane]) + (SdfProgram.MaterialVectorsPerEntry * ((int)words[SdfProgram.ProgramMaterialCountLane]))) + (SdfProgram.BoundRecordVectors * program.InstructionCount));
}
