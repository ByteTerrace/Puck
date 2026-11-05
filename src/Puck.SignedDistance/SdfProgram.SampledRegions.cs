using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>A sampled instruction's immutable-input candidate. Programs copy its values before validation or packing.</summary>
/// <param name="InstructionIndex">The sampled instruction.</param>
/// <param name="Values">Voxel distances, X fastest, then Y, then Z.</param>
public sealed record SdfSampledValues(int InstructionIndex, IReadOnlyList<float> Values);

public sealed partial class SdfProgram {
    // Invariant: identity and GPU sampling read the same program-owned snapshot, never a producer's mutable brick.
    private readonly SdfSampledValues[] m_sampledValues;

    private void ValidateSampledValues() {
        var seen = new HashSet<int>();
        foreach (var entry in m_sampledValues) {
            if ((entry.InstructionIndex < 0) || (entry.InstructionIndex >= m_instructions.Length) || !seen.Add(entry.InstructionIndex)) {
                throw new ArgumentException("A sampled value table must name one unique instruction.");
            }
            var instruction = m_instructions[entry.InstructionIndex];
            var dims = BitConverter.SingleToUInt32Bits(instruction.Data1.Y);
            var count = checked((int)((dims & 1023) * ((dims >> 10) & 1023) * ((dims >> 20) & 1023)));
            if ((instruction.Op != SdfOp.ShapeBlend) || ((SdfShapeType)instruction.Shape != SdfShapeType.SampledRegion) ||
                (entry.Values.Count != count) || entry.Values.Any(value => !float.IsFinite(value))) {
                throw new ArgumentException("A sampled value table must contain its instruction's finite voxel distances.");
            }
        }
        for (var index = 0; index < m_instructions.Length; index++) {
            var instruction = m_instructions[index];
            if ((instruction.Op == SdfOp.ShapeBlend) && ((SdfShapeType)instruction.Shape == SdfShapeType.SampledRegion) &&
                ((BitConverter.SingleToUInt32Bits(instruction.Data1.Z) & 0x80000000u) != 0) && !seen.Contains(index)) {
                throw new ArgumentException("An inline sampled instruction requires its voxel snapshot.");
            }
        }
    }

    private void PackSampledValues(int vectorOffset) {
        var cursor = checked(vectorOffset * 4);
        foreach (var entry in m_sampledValues) {
            var instruction = m_instructions[entry.InstructionIndex];
            m_instructions[entry.InstructionIndex] = instruction with {
                Data1 = instruction.Data1 with { Z = BitConverter.UInt32BitsToSingle(0x80000000u | (uint)cursor) },
            };
            foreach (var value in entry.Values) { m_words[cursor++] = BitConverter.SingleToUInt32Bits(value); }
            cursor = checked((cursor + 3) & ~3);
        }
    }
}

public sealed partial class SdfProgramBuilder {
    private readonly List<SdfSampledValues> m_sampledValues = [];

    /// <summary>Emits an immutable sampled field. Its values become part of the packed program and mesh identity.</summary>
    /// <param name="boxMin">The minimum corner.</param>
    /// <param name="cellSize">The voxel edge.</param>
    /// <param name="dimX">X dimension.</param>
    /// <param name="dimY">Y dimension.</param>
    /// <param name="dimZ">Z dimension.</param>
    /// <param name="boundaryFloor">The outside-box distance margin.</param>
    /// <param name="material">The surface material.</param>
    /// <param name="values">The voxel snapshot.</param>
    /// <param name="blend">The field composition.</param>
    /// <returns>This builder.</returns>
    public SdfProgramBuilder SampledValues(Vector3 boxMin, float cellSize, int dimX, int dimY, int dimZ,
        float boundaryFloor, int material, ReadOnlySpan<float> values, SdfBlendOp blend = SdfBlendOp.Union) {
        if (values.Length != checked(dimX * dimY * dimZ)) { throw new ArgumentException("Sampled dimensions and values disagree.", nameof(values)); }
        SampledRegion(boxMin, cellSize, dimX, dimY, dimZ, 0, boundaryFloor, material, blend);
        var index = m_instructions.Count - 1;
        m_instructions[index] = m_instructions[index] with {
            Data1 = m_instructions[index].Data1 with { Z = BitConverter.UInt32BitsToSingle(0x80000000u) },
        };
        m_sampledValues.Add(new(index, values.ToArray()));
        return this;
    }
}
