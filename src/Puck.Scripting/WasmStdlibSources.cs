using Puck.Maths;

namespace Puck.Scripting;

/// <summary>
/// One generated artifact contributed to the WASM standard library: where it lives in the repository, and
/// how to produce it.
/// </summary>
/// <param name="RelativePath">The repository-relative path, forward-slash separated (e.g.
/// <c>wasm/puck-stdlib/src/fixed_generated.rs</c>) — every consumer combines this with the repository
/// root rather than hard-coding a directory of its own.</param>
/// <param name="Emit">Produces the complete text of the artifact.</param>
public readonly record struct WasmStdlibSource(string RelativePath, Func<string> Emit);

/// <summary>
/// The ordered registry of every generated WASM standard-library artifact. Callers iterate
/// <see cref="All"/> instead of naming an individual emitter, so adding a future artifact — another ported
/// type, another generated Rust source — is a ONE-LINE addition to the list below: append a
/// <see cref="WasmStdlibSource"/> here and <c>Puck.Cli</c>'s <c>wasm-stdlib</c> verb, the only consumer,
/// needs no change.
/// </summary>
/// <remarks>
/// This registry lives in <c>Puck.Scripting</c> rather than <c>Puck.Maths</c> because two of its three
/// contributors (<see cref="AddonAbiRustPort"/>, mirroring the addon ABI's closed wire sets and
/// <see cref="AddonAbi"/>) read types this assembly owns, and the third
/// (<c>Puck.Maths.FixedQ4816RustPort</c>) reads a type owned by <c>Puck.Maths</c> — an assembly
/// <c>Puck.Scripting</c> already depends on. The reverse arrangement would have required <c>Puck.Maths</c>,
/// a leaf/data project, to depend upward on <c>Puck.Scripting</c>, a shared-substrate project
/// (<c>docs/project-map.md</c>'s layering), which is backwards. <c>FixedQ4816RustPort</c>'s public emitters keep
/// the registry reachable without an assembly friendship.
/// </remarks>
public static class WasmStdlibSources {
    /// <summary>Gets the registry, in the order artifacts are written and reported.</summary>
    public static IReadOnlyList<WasmStdlibSource> All { get; } = [
        new WasmStdlibSource(RelativePath: "wasm/puck-stdlib/src/fixed_generated.rs", Emit: FixedQ4816RustPort.EmitGenerated),
        new WasmStdlibSource(RelativePath: "wasm/puck-stdlib/src/fixed_vectors.rs", Emit: FixedQ4816RustPort.EmitVectors),
        new WasmStdlibSource(RelativePath: "wasm/puck-stdlib/src/abi_generated.rs", Emit: AddonAbiRustPort.EmitGenerated),
    ];
}
