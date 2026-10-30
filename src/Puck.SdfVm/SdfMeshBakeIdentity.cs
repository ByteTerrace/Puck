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
        EntryVersion = string.Empty;
        Value = value;
        Members = NoMembers;
    }
    private SdfMeshBakeIdentity(SdfMeshBakeClosure code, UInt128 maskKernel, UInt128 bakeKernel, string applied) {
        Applied = applied;
        BakeKernel = bakeKernel;
        Code = ((applied.Length == 0) ? code.Hash : Fold(applied: applied, code: code.Hash));
        CodeRevision = Puck.Abstractions.CodeRevision.Current;
        EntryVersion = code.EntryVersion;
        MaskKernel = maskKernel;
        Members = code.Members;
        Value = SdfMeshStore.BuildIdentityOf(bakeKernel: bakeKernel, code: Code, maskKernel: maskKernel);
        HasParts = true;
    }

    private static UInt128 Fold(UInt128 code, string applied) {
        var bytes = new byte[(16 + System.Text.Encoding.UTF8.GetByteCount(s: applied))];

        System.Buffers.Binary.BinaryPrimitives.WriteUInt128LittleEndian(destination: bytes, value: code);
        _ = System.Text.Encoding.UTF8.GetBytes(s: applied, charIndex: 0, charCount: applied.Length, bytes: bytes, byteIndex: 16);

        return XxHash128.HashToUInt128(source: bytes);
    }

    /// <summary>Gets the identity's value, which keys the store's entries.</summary>
    public UInt128 Value { get; }
    /// <summary>Gets whether the identity carries its parts, or its value alone.</summary>
    public bool HasParts { get; }
    /// <summary>Gets the store's entry format version (<see cref="SdfMeshStore.EntryVersion"/>), or empty for a value
    /// alone.</summary>
    public string EntryVersion { get; }
    /// <summary>Gets the code identity (<see cref="SdfMeshBakeClosure.Hash"/>, with <see cref="Applied"/> folded in when
    /// it is not empty), or zero for a value alone.</summary>
    public UInt128 Code { get; }
    /// <summary>Gets the hot-reload update applied to the code the bake runs
    /// (<see cref="Puck.Abstractions.CodeRevision.AppliedTo"/>), or empty for the code as built and for a value
    /// alone.</summary>
    public string Applied { get; } = string.Empty;
    /// <summary>Gets the code revision (<see cref="Puck.Abstractions.CodeRevision.Current"/>) this identity was derived
    /// under, or zero for a value alone.</summary>
    public int CodeRevision { get; }
    /// <summary>Gets the hash of the mask kernel's bytecode, or zero for a value alone.</summary>
    public UInt128 MaskKernel { get; }
    /// <summary>Gets the hash of the bake kernel's bytecode, or zero for a value alone.</summary>
    public UInt128 BakeKernel { get; }
    /// <summary>Gets the code's members with their hashes (<see cref="SdfMeshBakeClosure.Members"/>); none for a value
    /// alone.</summary>
    public IReadOnlyDictionary<string, UInt128> Members { get; }

    /// <summary>Returns the identity of a bake that runs a closure's code on a kernel set, with any hot-reload update
    /// applied to a type the closure reads (<see cref="SdfMeshBakeClosure.Reads"/>) folded into its code.</summary>
    /// <param name="code">The code the bake runs.</param>
    /// <param name="kernels">The kernel set the bake dispatches.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> or <paramref name="kernels"/> is
    /// <see langword="null"/>.</exception>
    public static SdfMeshBakeIdentity Of(SdfMeshBakeClosure code, SdfKernelSet kernels) {
        ArgumentNullException.ThrowIfNull(argument: code);
        ArgumentNullException.ThrowIfNull(argument: kernels);

        return new SdfMeshBakeIdentity(
            applied: Puck.Abstractions.CodeRevision.AppliedTo(reads: code.Reads),
            bakeKernel: XxHash128.HashToUInt128(source: kernels[SdfKernel.MeshBake].Span),
            code: code,
            maskKernel: XxHash128.HashToUInt128(source: kernels[SdfKernel.MeshBakeMask].Span)
        );
    }
    /// <summary>Returns this identity as the running code derives it: itself while no update was applied since it was
    /// derived, otherwise the same closure and kernels with the applied update folded in again
    /// (<see cref="Puck.Abstractions.CodeRevision.AppliedTo"/>).</summary>
    /// <param name="code">The code the bake runs.</param>
    /// <returns>The identity under the running code.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is <see langword="null"/>.</exception>
    public SdfMeshBakeIdentity UnderRunningCode(SdfMeshBakeClosure code) {
        ArgumentNullException.ThrowIfNull(argument: code);

        return ((!HasParts || (CodeRevision == Puck.Abstractions.CodeRevision.Current))
            ? this
            : new SdfMeshBakeIdentity(
                applied: Puck.Abstractions.CodeRevision.AppliedTo(reads: code.Reads),
                bakeKernel: BakeKernel,
                code: code,
                maskKernel: MaskKernel
            ));
    }
}
