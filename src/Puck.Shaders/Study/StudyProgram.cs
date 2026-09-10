namespace Puck.Shaders.Study;

/// <summary>A study compiled by <see cref="StudyShaderCompiler"/>: the Shadertoy-dialect source as one compute kernel
/// (see <see cref="StudyPrelude"/>) in both backend bytecodes, plus every diagnostic the toolchain produced. A failed
/// compile — a user-source error, never a missing tool — carries empty <see cref="Spirv"/>/<see cref="Dxil"/> and at
/// least one diagnostic with <c>IsError</c> true.</summary>
/// <param name="Name">The study's name, as the caller registered it.</param>
/// <param name="SourcePath">The author's file, carried through for diagnostics and caller bookkeeping.</param>
/// <param name="SourceHash">SHA-256 hex of the author's ORIGINAL source text (unwrapped).</param>
/// <param name="Spirv">The kernel's SPIR-V, or empty for a failed compile.</param>
/// <param name="Dxil">The kernel's DXIL, or empty for a failed compile.</param>
/// <param name="Diagnostics">Every diagnostic, lines already mapped back onto the author's file.</param>
public sealed record StudyProgram(
    string Name,
    string SourcePath,
    string SourceHash,
    ReadOnlyMemory<byte> Spirv,
    ReadOnlyMemory<byte> Dxil,
    IReadOnlyList<StudyDiagnostic> Diagnostics
) {
    /// <summary>Gets a value indicating whether compilation failed — at least one diagnostic has <c>IsError</c> true,
    /// and both bytecode buffers are empty.</summary>
    public bool IsError => Diagnostics.Any(predicate: static diagnostic => diagnostic.IsError);
}
