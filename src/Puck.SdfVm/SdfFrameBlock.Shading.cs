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
    /// <summary>Writes a plan's bins into a pass block: each bin's job width in tiles, and zero for every bin the plan does
    /// not record, which the bins pass writes no job of (<c>sdf-world-views-bins.comp.hlsl</c>).</summary>
    /// <param name="block">The pass block, at least <see cref="SizeBytes"/> bytes.</param>
    /// <param name="plan">The frame's plan; an unsplit plan writes every width zero.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public static void WriteShadeBins(Span<byte> block, SdfShadeDispatch plan) {
        ArgumentNullException.ThrowIfNull(plan);
        var widths = Offset(SdfWorldPackage.ShadeBinWidths);

        for (var bin = 0; (bin < SdfWorldPackage.ShadeBins); bin++) {
            WriteUInt32(block, widths + (bin * sizeof(uint)), ((bin < plan.Bins.Count) ? plan.Bins[bin].Width : 0U));
        }
    }
}
