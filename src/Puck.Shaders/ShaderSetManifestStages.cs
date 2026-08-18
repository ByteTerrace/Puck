namespace Puck.Shaders;

/// <summary>The per-stage source-file stems (no extension) of one <see cref="ShaderSetManifest"/> — each present
/// stem names a sibling <c>&lt;stem&gt;.hlsl</c> and its compiled <c>&lt;stem&gt;.spv</c> / optional
/// <c>&lt;stem&gt;.dxil</c>. A set declares either <see cref="Vertex"/>+<see cref="Fragment"/> (a graphics set) or
/// <see cref="Compute"/> (a compute set), never both kinds.</summary>
/// <param name="Vertex">The vertex-stage source stem, or <see langword="null"/> for a compute-only set.</param>
/// <param name="Fragment">The fragment-stage source stem, or <see langword="null"/> for a compute-only set.</param>
/// <param name="Compute">The compute-stage source stem, or <see langword="null"/> for a graphics set.</param>
public sealed record ShaderSetManifestStages(string? Vertex, string? Fragment, string? Compute);
