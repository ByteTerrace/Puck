namespace Puck.SdfVm;

// Packed layout (each element is one uvec4 = 16 bytes); KEEP IN SYNC with Shaders/sdf-vm.glsl sdfWords[] indexing:
//   word[0]             = (instructionCount, materialCount, dataOffset, materialOffset) in uvec4 units
//   [1 .. 1+N)          = instruction headers (op, shape, blend, material)
//   [dataOffset ..)     = instruction data, 2 uvec4 per instruction (data0, data1 as float bits)
//   [materialOffset ..) = materials, 1 uvec4 each (albedo.rgb as float bits, w reserved)
public sealed class SdfProgram {
    private const int WordsPerVector = 4;

    private readonly SdfInstruction[] m_instructions;
    private readonly uint[] m_words;

    public SdfProgram(IReadOnlyList<SdfInstruction> instructions, IReadOnlyList<SdfMaterial> materials) {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(materials);

        m_instructions = [.. instructions];

        var instructionCount = instructions.Count;
        var materialCount = materials.Count;
        var dataOffsetVectors = (1 + instructionCount);
        var materialOffsetVectors = (dataOffsetVectors + (2 * instructionCount));
        var totalVectors = (materialOffsetVectors + materialCount);

        InstructionCount = instructionCount;
        m_words = new uint[(totalVectors * WordsPerVector)];

        m_words[0] = (uint)instructionCount;
        m_words[1] = (uint)materialCount;
        m_words[2] = (uint)dataOffsetVectors;
        m_words[3] = (uint)materialOffsetVectors;

        for (var index = 0; (index < instructionCount); index++) {
            var instruction = instructions[index];
            var headerBase = ((1 + index) * WordsPerVector);

            m_words[headerBase] = (uint)instruction.Op;
            m_words[(headerBase + 1)] = instruction.Shape;
            m_words[(headerBase + 2)] = instruction.Blend;
            m_words[(headerBase + 3)] = instruction.Material;

            var dataBase = ((dataOffsetVectors + (2 * index)) * WordsPerVector);

            WriteVector4(
                baseIndex: dataBase,
                w: instruction.Data0.W,
                x: instruction.Data0.X,
                y: instruction.Data0.Y,
                z: instruction.Data0.Z
            );
            WriteVector4(
                baseIndex: (dataBase + WordsPerVector),
                w: instruction.Data1.W,
                x: instruction.Data1.X,
                y: instruction.Data1.Y,
                z: instruction.Data1.Z
            );
        }

        for (var index = 0; (index < materialCount); index++) {
            var materialBase = ((materialOffsetVectors + index) * WordsPerVector);

            WriteVector4(
                baseIndex: materialBase,
                w: 0f,
                x: materials[index].Albedo.X,
                y: materials[index].Albedo.Y,
                z: materials[index].Albedo.Z
            );
        }
    }

    public int InstructionCount { get; }
    /// <summary>The typed instructions the program was built from, in order — the source the packed
    /// <see cref="Words"/> are compiled from. Retained so a consumer (e.g. a ray-tracing instance extractor that
    /// needs per-primitive world bounds) can read the scene structure without decoding the packed word layout.</summary>
    public IReadOnlyList<SdfInstruction> Instructions => m_instructions;
    public ReadOnlySpan<uint> Words => m_words;

    private void WriteVector4(int baseIndex, float x, float y, float z, float w) {
        m_words[baseIndex] = BitConverter.SingleToUInt32Bits(value: x);
        m_words[(baseIndex + 1)] = BitConverter.SingleToUInt32Bits(value: y);
        m_words[(baseIndex + 2)] = BitConverter.SingleToUInt32Bits(value: z);
        m_words[(baseIndex + 3)] = BitConverter.SingleToUInt32Bits(value: w);
    }
}
