namespace Puck.SignedDistance;

internal enum SdfReliefStreamRole { None, ReliefOwner, ReliefRow }
internal enum SdfReliefAction { Identity, Noise }

/// <summary>Field instructions and the contract each reader consumes.</summary>
public static partial class SdfFieldOps {
    private abstract class Entry {
        public abstract SdfOpRole Role { get; }
        public abstract bool Contact { get; }
        public abstract SdfReliefStreamRole Stream { get; }
        public abstract SdfReliefAction Point { get; }
        public abstract SdfReliefAction Interval { get; }
        public abstract SdfShaderAction Shader { get; }
    }

    [SdfFieldEntry(21, "SdfOp")]
    private sealed class NoiseDisplace : Entry {
        public override SdfOpRole Role => SdfOpRole.Field;
        public override bool Contact => true;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.ReliefOwner;
        public override SdfReliefAction Point => SdfReliefAction.Noise;
        public override SdfReliefAction Interval => SdfReliefAction.Noise;
        public override SdfShaderAction Shader => new(
            Code: """
                // The relief's mask rows stand directly before it in its segment (the program's door, SdfProgram.
                // ReliefRows.cs); malformed words that count past the segment's start or the row cap are refused here.
                uint reliefRows = SDF_INSTRUCTION_MATERIAL(instructionHeader);
                if ((reliefRows > SDF_MAX_RELIEF_ROWS) || (reliefRows > (index - segmentMeta.z))) { return sdfIsaErrorHit(); }
                float maskProduct = ((reliefRows == 0u) ? 1.0 : sdfMaskProduct((index - reliefRows), reliefRows, localPosition, dataOffset));
                // A weight of exactly zero adds no relief, so the noise is not evaluated.
                if (maskProduct <= 0.0) { break; }
                // Keep the field continuous, including outside the zero-set neighborhood: the bake's lattice,
                // material points and gradient taps all consume these values (see SDF_OP_DISPLACE).
                float3 q = (localPosition * data0.x);
                float octaveAmplitude = 1.0;
                float noiseSum = 0.0;
                uint octaveCount = SDF_INSTRUCTION_BLEND(instructionHeader);
                for (uint octave = 0u; octave < SDF_MAX_NOISE_OCTAVES; octave++) {
                    if (octave >= octaveCount) { break; }
                    uint octaveSeed = (SDF_INSTRUCTION_SHAPE(instructionHeader) + octave);
                    noiseSum += (octaveAmplitude * sdfValueNoise3(q, uint3(octaveSeed, (octaveSeed * SDF_HASH_STREAM_A), (octaveSeed * SDF_HASH_STREAM_B))));
                    q *= data0.w;
                    octaveAmplitude *= data0.z;
                }
                // A relief its masks left at weight one adds exactly what an unmasked one adds.
                result.distance += (((data0.y * data1.x) * noiseSum) * maskProduct);
                break;
                """,
            Operands: SdfShaderOperands.Data0 | SdfShaderOperands.Data1
        );
    }

    [SdfFieldEntry(27, "SdfOp")]
    private sealed class ReliefRow : Entry {
        public override SdfOpRole Role => SdfOpRole.Field;
        public override bool Contact => true;
        public override SdfReliefStreamRole Stream => SdfReliefStreamRole.ReliefRow;
        public override SdfReliefAction Point => SdfReliefAction.Identity;
        public override SdfReliefAction Interval => SdfReliefAction.Identity;
        public override SdfShaderAction Shader => new(
            Code: """
                break;
                """,
            Operands: SdfShaderOperands.None
        );
    }
}
