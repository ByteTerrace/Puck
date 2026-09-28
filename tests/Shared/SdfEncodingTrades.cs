using System.Numerics;
using Puck.SignedDistance;

namespace Puck.Testing;

// Probe calls with one field each put elsewhere, spelled at the call, which is what the encoding description sees of a
// builder or packer writing one field where the other goes: a rotation's Y and W, a sampled region's Y and Z dimension
// bitfields, a weathering's Edge and Lines, a sweep's start and end radii, and a cell displacement's frequency and
// amplitude.
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
        ];
    }
}
