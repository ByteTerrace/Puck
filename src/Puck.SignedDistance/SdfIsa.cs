namespace Puck.SignedDistance;

/// <summary>Defines the SDF instruction-set version contract.</summary>
public static class SdfIsa {
    /// <summary>The only SDF instruction-set version accepted by this build. The kernels report it back as
    /// <c>SDF_ISA_VERSION</c> from the generated <c>sdf-isa.hlsli</c>, and a version mismatch refuses the kernel set;
    /// raise it when existing bytecode would misread an encoding.</summary>
    public const byte Version = 2;
}
