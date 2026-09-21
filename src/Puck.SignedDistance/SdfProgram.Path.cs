using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    private readonly SdfCompiledPath[] m_paths;

    private void ValidatePaths() {
        HashSet<int> owners = [];

        foreach (var path in m_paths) {
            var index = path.InstructionIndex;

            if ((((uint)index) >= ((uint)m_instructions.Length)) || !owners.Add(item: index) ||
                (m_instructions[index].Op != SdfOp.ShapeBlend) || (m_instructions[index].Shape != ((uint)SdfShapeType.Path))) {
                throw new ArgumentException(message: "Each path table must own exactly one Path instruction.");
            }
            var instruction = m_instructions[index];

            if ((path.Edges.Count is < 1 or > SdfPathProfile.MaxEdges) || (instruction.Data0.Y != path.Edges.Count) ||
                !float.IsFinite(f: instruction.Data0.W) || (instruction.Data0.W <= 0f) ||
                ((instruction.Data1.Y != 0f) && (instruction.Data1.Y != 1f)) || (instruction.Data1.Z != 0f) || (instruction.Data1.W != 0f)) {
                throw new ArgumentException(message: "Invalid path count, depth, mode, or reserved lane.");
            }
            var reach = 0f;

            foreach (var edge in path.Edges) {
                if (!float.IsFinite(f: edge.A.X) || !float.IsFinite(f: edge.A.Y) || !float.IsFinite(f: edge.B.X) || !float.IsFinite(f: edge.B.Y) ||
                    !float.IsFinite(f: edge.RadiusA) || !float.IsFinite(f: edge.RadiusB) || (edge.RadiusA < 0f) || (edge.RadiusB < 0f) ||
                    (Vector2.DistanceSquared(value1: edge.A, value2: edge.B) < (SdfPathProfile.MinEdgeLength * SdfPathProfile.MinEdgeLength)) ||
                    ((MathF.Abs(x: edge.A.X) + edge.RadiusA) > 1f) || ((MathF.Abs(x: edge.A.Y) + edge.RadiusA) > 1f) ||
                    ((MathF.Abs(x: edge.B.X) + edge.RadiusB) > 1f) || ((MathF.Abs(x: edge.B.Y) + edge.RadiusB) > 1f) ||
                    ((instruction.Data1.Y == 0f) && ((edge.RadiusA != 0f) || (edge.RadiusB != 0f))) ||
                    ((instruction.Data1.Y == 1f) && ((edge.RadiusA <= 0f) || (edge.RadiusB <= 0f)))) {
                    throw new ArgumentException(message: "Invalid path edge.");
                }
                reach = MathF.Max(x: reach, y: MathF.Max(x: (edge.A.Length() + edge.RadiusA), y: (edge.B.Length() + edge.RadiusB)));
            }
            if (instruction.Data1.Y == 0f) { SdfPathCompiler.ValidateFill(edges: path.Edges); }
            if (!float.IsFinite(f: reach) || (instruction.Data0.Z < reach)) { throw new ArgumentException(message: "Path bound does not cover its edges."); }
        }
        for (var i = 0; (i < m_instructions.Length); i++) {
            if ((m_instructions[i].Op == SdfOp.ShapeBlend) && (m_instructions[i].Shape == ((uint)SdfShapeType.Path)) && !owners.Contains(item: i)) {
                throw new ArgumentException(message: $"Path instruction {i} has no edge table.");
            }
        }
    }
    private void PackPaths(int offset) {
        foreach (var path in m_paths) {
            foreach (var edge in path.Edges) {
                WriteVector4(m_words, (offset * WordsPerVector), edge.A.X, edge.A.Y, edge.B.X, edge.B.Y);
                WriteVector4(m_words, ((offset + 1) * WordsPerVector), edge.RadiusA, edge.RadiusB, 0f, 0f);
                offset += 2;
            }
        }
    }
}
