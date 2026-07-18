using Puck.Assets;

using Module = Wasmtime.Module;

namespace Puck.Scripting;

/// <summary>The immutable result of loading and compiling an addon module — the compiled
/// <see cref="Wasmtime.Module"/> paired with its content identity. Mirrors <c>ShaderStageInfo</c>.</summary>
/// <param name="Path">The fully-qualified path the module was read from.</param>
/// <param name="ContentHash">The content identity of the module bytes (keys the compile cache).</param>
/// <param name="ByteLength">The length in bytes of the module source (wasm binary or WAT text).</param>
/// <param name="Module">The compiled Wasmtime module, cached and shared across addons with identical bytes.</param>
public sealed record ScriptingModuleInfo(string Path, AssetContentHash ContentHash, int ByteLength, Module Module);
