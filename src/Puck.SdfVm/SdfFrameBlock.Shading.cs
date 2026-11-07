using Puck.Shaders;
using Puck.SignedDistance;
using static Puck.Shaders.ShaderParameterWriter;

namespace Puck.SdfVm;

public static partial class SdfFrameBlock {
    private static void WriteShadeDefault(Span<byte> block, uint first, SdfShadeProgram? program) {
        var binding = Offset(SdfWorldPackage.ShadeDefault);
        block.Slice(binding, 16).Clear();
        var identity = Offset(SdfWorldPackage.ShadeIdentity);
        block.Slice(identity, 32).Clear();
        var demand = Offset(SdfWorldPackage.ShadeDemand);
        block.Slice(demand, 16).Clear();
        if (program is null) { return; }
        var length = checked((uint)(program.Words.Length / 4));
        WriteUInt32(block, binding, first);
        WriteUInt32(block, binding + 4, length);
        WriteUInt32(block, binding + 8, first + length);
        WriteUInt32(block, binding + 12, (uint)program.ParameterLayout.Count);
        program.IdentityBytes.CopyTo(block.Slice(identity, 32));
        WriteSingle(block, demand, program.Demand.MaxWrap);
        WriteSingle(block, demand + 4, program.Demand.MaxTiltRadians);
        WriteSingle(block, demand + 8, program.Demand.EverySide ? 1f : 0f);
    }
}
