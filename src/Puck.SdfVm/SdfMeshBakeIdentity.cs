using System.IO.Hashing;

namespace Puck.SdfVm;

/// <summary>
/// The identity a bake keys the meshes it stores by (<see cref="SdfMeshStore"/>), with what it is made of: the code the
/// bake runs on the CPU (<see cref="SdfMeshBakeClosure"/>) and the bytecode of the kernels that sample a group's field,
/// <see cref="SdfKernel.MeshBakeMask"/> and <see cref="SdfKernel.MeshBake"/>, which hold the field interpreter and differ
/// by backend. A store records each identity's parts beside its entries, so a bake that misses names the part that
/// changed (<see cref="SdfMeshStore.ExplainMisses"/>). Immutable.
/// </summary>
public sealed class SdfMeshBakeIdentity {
    private static readonly Dictionary<string, UInt128> NoMembers = [];

    /// <summary>Initializes a new instance of the <see cref="SdfMeshBakeIdentity"/> class that carries a value alone, as
    /// a report prints it: its parts are unknown, so a miss under it names none.</summary>
    /// <param name="value">The identity's value.</param>
    public SdfMeshBakeIdentity(UInt128 value) {
        Value = value;
        Members = NoMembers;
    }
    private SdfMeshBakeIdentity(SdfMeshBakeClosure code, UInt128 maskKernel, UInt128 bakeKernel) {
        BakeKernel = bakeKernel;
        Code = code.Hash;
        MaskKernel = maskKernel;
        Members = code.Members;
        Value = SdfMeshStore.BuildIdentityOf(bakeKernel: bakeKernel, code: code.Hash, maskKernel: maskKernel);
        HasParts = true;
    }

    /// <summary>Gets the identity's value, which keys the store's entries.</summary>
    public UInt128 Value { get; }
    /// <summary>Gets whether the identity carries its parts, or its value alone.</summary>
    public bool HasParts { get; }
    /// <summary>Gets the code identity (<see cref="SdfMeshBakeClosure.Hash"/>), or zero for a value alone.</summary>
    public UInt128 Code { get; }
    /// <summary>Gets the hash of the mask kernel's bytecode, or zero for a value alone.</summary>
    public UInt128 MaskKernel { get; }
    /// <summary>Gets the hash of the bake kernel's bytecode, or zero for a value alone.</summary>
    public UInt128 BakeKernel { get; }
    /// <summary>Gets the code's members with their hashes (<see cref="SdfMeshBakeClosure.Members"/>); none for a value
    /// alone.</summary>
    public IReadOnlyDictionary<string, UInt128> Members { get; }

    /// <summary>Returns the identity of a bake that runs a closure's code on a kernel set.</summary>
    /// <param name="code">The code the bake runs.</param>
    /// <param name="kernels">The kernel set the bake dispatches.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> or <paramref name="kernels"/> is
    /// <see langword="null"/>.</exception>
    public static SdfMeshBakeIdentity Of(SdfMeshBakeClosure code, SdfKernelSet kernels) {
        ArgumentNullException.ThrowIfNull(argument: code);
        ArgumentNullException.ThrowIfNull(argument: kernels);

        return new SdfMeshBakeIdentity(
            bakeKernel: XxHash128.HashToUInt128(source: kernels[SdfKernel.MeshBake].Span),
            code: code,
            maskKernel: XxHash128.HashToUInt128(source: kernels[SdfKernel.MeshBakeMask].Span)
        );
    }
}
