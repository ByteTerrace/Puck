using System.Collections.ObjectModel;

namespace Puck.Shaders;

/// <summary>An immutable, backend-neutral shader compilation candidate.</summary>
public sealed record CompiledShader
{
    public CompiledShader(
        string name,
        string sourcePath,
        string sourceHash,
        IReadOnlyDictionary<ShaderStage, ReadOnlyMemory<byte>> spirv,
        IReadOnlyDictionary<ShaderStage, ReadOnlyMemory<byte>> dxil,
        IReadOnlyList<ShaderDiagnostic> diagnostics,
        IReadOnlyList<ShaderSourceDependency>? dependencies = null)
    {
        Name = name;
        SourcePath = sourcePath;
        SourceHash = sourceHash;
        SpirvByStage = new ReadOnlyDictionary<ShaderStage, ReadOnlyMemory<byte>>(
            new Dictionary<ShaderStage, ReadOnlyMemory<byte>>(spirv));
        DxilByStage = new ReadOnlyDictionary<ShaderStage, ReadOnlyMemory<byte>>(
            new Dictionary<ShaderStage, ReadOnlyMemory<byte>>(dxil));
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        Dependencies = Array.AsReadOnly((dependencies ?? []).ToArray());
    }

    public string Name { get; }
    public string SourcePath { get; }
    public string SourceHash { get; }
    public IReadOnlyDictionary<ShaderStage, ReadOnlyMemory<byte>> SpirvByStage { get; }
    public IReadOnlyDictionary<ShaderStage, ReadOnlyMemory<byte>> DxilByStage { get; }
    public IReadOnlyList<ShaderDiagnostic> Diagnostics { get; }
    public IReadOnlyList<ShaderSourceDependency> Dependencies { get; }
    public bool IsError => Diagnostics.Any(static diagnostic => diagnostic.IsError);
    public bool IsSuccess => !IsError && SpirvByStage.Count > 0 && SpirvByStage.Count == DxilByStage.Count &&
        SpirvByStage.Keys.All(DxilByStage.ContainsKey);

    // Convenience members for the common one-compute-stage pipeline.
    public ReadOnlyMemory<byte> Spirv => SpirvByStage.GetValueOrDefault(ShaderStage.Compute);
    public ReadOnlyMemory<byte> Dxil => DxilByStage.GetValueOrDefault(ShaderStage.Compute);
}
