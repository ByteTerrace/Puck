using System.Collections.ObjectModel;

namespace Puck.Shaders;

/// <summary>An immutable, backend-neutral shader compilation candidate.</summary>
public sealed record CompiledShader {
    public CompiledShader(
        string name,
        string sourcePath,
        string sourceHash,
        IReadOnlyDictionary<ShaderStage, ReadOnlyMemory<byte>> spirv,
        IReadOnlyDictionary<ShaderStage, ReadOnlyMemory<byte>> dxil,
        IReadOnlyList<ShaderDiagnostic> diagnostics,
        IReadOnlyList<ShaderSourceDependency>? dependencies = null) {
        Name = name;
        SourcePath = sourcePath;
        SourceHash = sourceHash;
        SpirvByStage = new ReadOnlyDictionary<ShaderStage, ReadOnlyMemory<byte>>(dictionary: spirv.ToDictionary(
            static pair => pair.Key,
            static pair => ((ReadOnlyMemory<byte>)pair.Value.ToArray())
        ));
        DxilByStage = new ReadOnlyDictionary<ShaderStage, ReadOnlyMemory<byte>>(dictionary: dxil.ToDictionary(
            static pair => pair.Key,
            static pair => ((ReadOnlyMemory<byte>)pair.Value.ToArray())
        ));
        Diagnostics = Array.AsReadOnly(array: diagnostics.ToArray());
        Dependencies = Array.AsReadOnly(array: (dependencies ?? []).ToArray());
    }

    public IReadOnlyList<ShaderSourceDependency> Dependencies { get; }
    public IReadOnlyList<ShaderDiagnostic> Diagnostics { get; }
    public ReadOnlyMemory<byte> Dxil => DxilByStage.GetValueOrDefault(key: ShaderStage.Compute);
    public IReadOnlyDictionary<ShaderStage, ReadOnlyMemory<byte>> DxilByStage { get; }
    public bool IsError => Diagnostics.Any(predicate: static diagnostic => diagnostic.IsError);
    public bool IsSuccess => (!IsError && (SpirvByStage.Count > 0) && (SpirvByStage.Count == DxilByStage.Count) &&
        SpirvByStage.Keys.All(predicate: DxilByStage.ContainsKey));
    public string Name { get; }
    public string SourceHash { get; }
    public string SourcePath { get; }
    // Convenience members for the common one-compute-stage pipeline.
    public ReadOnlyMemory<byte> Spirv => SpirvByStage.GetValueOrDefault(key: ShaderStage.Compute);
    public IReadOnlyDictionary<ShaderStage, ReadOnlyMemory<byte>> SpirvByStage { get; }
}
