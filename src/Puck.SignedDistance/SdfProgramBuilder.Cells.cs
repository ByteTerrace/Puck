using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
    /// <summary>Adds cellular relief to the accumulated field. Isolate it in a field scope to affect one object.</summary>
    /// <param name="frequency">Positive cells per local coordinate unit.</param>
    /// <param name="amplitude">Nonnegative displacement in accumulated-field units.</param>
    /// <param name="seed">Unsigned PCG3D feature seed.</param>
    /// <param name="mode">F1 or F2MinusF1.</param>
    /// <param name="randomness">Centered feature-box side length, bounded by the selected mode.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is invalid.</exception>
    /// <remarks>Build refuses sampling through folds without a global continuous derivative bound.
    /// ResetPoint and restore a rigid sampling frame after such geometry.</remarks>
    public SdfProgramBuilder CellDisplace(float frequency, float amplitude, uint seed, SdfCellMode mode, float randomness) {
        new SdfCellDisplacement(frequency, amplitude, seed, mode, randomness).Validate();
        m_instructions.Add(new SdfInstruction(SdfOp.CellDisplace, seed, (uint)mode, 0,
            new Vector4(frequency, amplitude, randomness, 0f), Vector4.Zero));
        return this;
    }
}
