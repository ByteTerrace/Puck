namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    /// <summary>The instruction's compact tape token starts above the certificate's flag byte.</summary>
    public const int TapeTokenShift = 8;
    /// <summary>The low byte of a packed certificate word contains its certificate flags.</summary>
    public const uint TapeCertificateFlagsMask = 0xFFu;
    /// <summary>The instance-directory header's lane holding the compact tape token count.</summary>
    public const int InstanceTapeTokenCountLane = 3;

    /// <summary>The shapes and field pops addressable by one slab's live and deciding masks.</summary>
    public int TapeTokenCount { get; private set; }

    private void PackTapeTokens(int offset, int segmentOffset, int segmentCount) {
        var token = 0;

        for (var instruction = 0; (instruction < InstructionCount); instruction++) {
            m_words[(((offset + instruction) * WordsPerVector) + 3)] = checked((((uint)token) * (1u << TapeTokenShift)));
            if (m_instructions[instruction].Op is SdfOp.ShapeBlend or SdfOp.PopField) { token++; }
        }
        TapeTokenCount = token;
        var instances = ((segmentOffset + DirectoryHeaderVectors) + (BoundRecordVectors * segmentCount));

        m_words[((instances * WordsPerVector) + InstanceTapeTokenCountLane)] = ((uint)token);
    }
}
