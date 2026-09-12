namespace Puck.Shaders;

/// <summary>Read-only allocation and extent information for one live pipeline resource.</summary>
public sealed record ShaderPipelineResourceStatus(string Name, ShaderPipelineResourceKind Kind, ulong AllocationBytes, uint Width, uint Height, bool External, bool History);

/// <summary>Read-only execution information for one live pipeline pass.</summary>
public sealed record ShaderPipelinePassStatus(string Name, ShaderPipelinePassKind Kind, uint BindingCount, double? LastGpuMilliseconds);