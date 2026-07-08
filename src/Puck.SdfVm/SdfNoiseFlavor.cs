namespace Puck.SdfVm;

// Values must match Shaders/Sdf/sdf-vm.hlsli (SDF_NOISE_*); rides the SDF_OP_CELL_JITTER Blend lane (header.z).
/// <summary>How a <see cref="SdfOp.CellJitter"/> op distributes its per-cell POSITION offset (r0). Reshapes ONLY the
/// displacement — the tumble and the material variant are UNAFFECTED. Every flavor keeps the offset in
/// <c>[0,1)^3</c> before centering, so <c>(r0 − 0.5) · jitter</c> stays within <c>±jitter/2</c> per axis — the SAME
/// bound White has — and <c>SdfProgram.AnalyzeLipschitz</c>'s reach-independent clamp is unchanged for all three.</summary>
public enum SdfNoiseFlavor : uint {
    /// <summary>The independent PCG3D uniform: three decorrelated hashed uniforms, one per axis. The default, and
    /// BYTE-IDENTICAL to pre-flavor programs (which packed the Blend lane 0).</summary>
    White = 0,
    /// <summary>Roberts' R3 generalized-golden low-discrepancy offset (the rank-1 lattice on the integer cell index),
    /// evaluated in FIXED-POINT integer so it is de-clumped yet bit-identical cross-backend. "Blue-ish" low-discrepancy
    /// scatter — NOT true isotropic blue noise.</summary>
    Blue = 1,
    /// <summary>The central-limit average of several decorrelated hashed uniforms per axis: a bell-shaped offset whose
    /// mass clusters toward the cell centre, with occasional outliers.</summary>
    Gaussian = 2,
}
