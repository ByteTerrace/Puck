using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // Writes every convex-polygon profile's vertices into the side table appended after the rigid-leaf plan — two
    // packed (x, y) float-bit vertices per uvec4, an odd final vertex padded with a zero-filled second slot (never
    // read: sdfPolygonVertex/the fixed-point mirror index by the instruction's own patched vertex count).
    private void PackConvexPolygonProfiles(int[] profileOffsets) {
        for (var profileIndex = 0; (profileIndex < m_convexPolygonProfiles.Length); profileIndex++) {
            var vertices = m_convexPolygonProfiles[profileIndex].Vertices;
            var baseVectors = profileOffsets[profileIndex];

            for (var vertexIndex = 0; (vertexIndex < vertices.Length); vertexIndex += 2) {
                var entryBase = ((baseVectors + (vertexIndex / 2)) * WordsPerVector);
                var first = vertices[vertexIndex];
                var second = (((vertexIndex + 1) < vertices.Length) ? vertices[(vertexIndex + 1)] : Vector2.Zero);

                WriteVector4(
                    words: m_words,
                    baseIndex: entryBase,
                    w: second.Y,
                    x: first.X,
                    y: first.Y,
                    z: second.X
                );
            }
        }
    }
    // A linear scan is fine: a program's convex-polygon count is small, and this runs at Build() time, never per query.
    private static bool TryFindConvexPolygonVertices((int InstructionIndex, Vector2[] Vertices)[] convexPolygonProfiles, int instructionIndex, out Vector2[] vertices) {
        for (var index = 0; (index < convexPolygonProfiles.Length); index++) {
            if (convexPolygonProfiles[index].InstructionIndex == instructionIndex) {
                vertices = convexPolygonProfiles[index].Vertices;

                return true;
            }
        }

        vertices = [];

        return false;
    }
}
