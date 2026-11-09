using Puck.Shaders;
using static Puck.Shaders.ShaderParameterWriter;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public static partial class SdfFrameBlock {
    // The indirect sources, the sky's gain and the receiver-only application the views pass reads beside the
    // irradiance volume. The off tier applies none of the volume's light (intensity zero), while its probes still occlude
    // the traced lights and the reflected sky, so turning indirect light off only ever darkens a surface.
    private static void WriteIndirectControls(Span<byte> block, SdfFrame frame) {
        var gains = frame.IndirectGains;
        var apply = frame.IndirectApply;

        gains.Validate();
        apply.Validate();
        if (frame.IndirectBounces is { } bounces) {
            ArgumentOutOfRangeException.ThrowIfNegative(bounces);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(bounces, SdfIndirectGains.MaximumBounces);
        }
        var offset = Offset(member: SdfWorldPackage.IndirectSourceGains);

        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.IndirectSources), value: ((uint)frame.IndirectSources));
        WriteSingle(block: block, offset: offset, value: gains.Lights);
        WriteSingle(block: block, offset: (offset + sizeof(float)), value: gains.Emission);
        WriteSingle(block: block, offset: (offset + (2 * sizeof(float))), value: gains.Screens);
        WriteSingle(block: block, offset: (offset + (3 * sizeof(float))), value: gains.Sky);
        offset = Offset(member: SdfWorldPackage.IndirectApply);
        WriteVector3(block: block, offset: offset, value: apply.Tint);
        WriteSingle(block: block, offset: (offset + (3 * sizeof(float))), value: ((frame.IndirectTier == SdfIndirectTier.Off) ? 0f : apply.Intensity));
        WriteSingle(block: block, offset: Offset(member: SdfWorldPackage.IndirectContact), value: apply.Contact);
    }
}
