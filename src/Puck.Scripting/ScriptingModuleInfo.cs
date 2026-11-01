using Puck.Assets;

using Module = Wasmtime.Module;

namespace Puck.Scripting;

/// <summary>The immutable result of loading and compiling an addon module — the compiled
/// <see cref="Wasmtime.Module"/> paired with its content identity. Mirrors <c>ShaderStageInfo</c>.</summary>
/// <param name="Path">The fully-qualified path the module was read from.</param>
/// <param name="ContentHash">The full SHA-256 identity that pins source bytes and keys the compile cache.</param>
/// <param name="ByteLength">The length in bytes of the module source (wasm binary or WAT text).</param>
/// <param name="Module">The compiled Wasmtime module, cached and shared across addons with identical bytes.</param>
/// <param name="Memories">Every linear memory the module defines or imports, read from its binary before it was
/// compiled.</param>
public sealed record ScriptingModuleInfo(string Path, ContentPin ContentHash, int ByteLength, Module Module, IReadOnlyList<WasmMemoryDeclaration> Memories) {
    /// <summary>The exact source bytes whose content pin identifies this module, retained for durable recovery.</summary>
    public ReadOnlyMemory<byte> Source { get; init; }
    /// <summary>Host-only exports for the module's globals; null when the loader did not prepare checkpoint access.</summary>
    public string[]? CheckpointGlobals { get; init; }
    /// <summary>A declared resource whose private state cannot be captured, or null.</summary>
    public string? CheckpointRefusal { get; init; }
}
