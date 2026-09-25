using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>Read-only allocation and extent information for one live pipeline resource.</summary>
public sealed record ShaderPipelineResourceStatus(string Name, ShaderPipelineResourceKind Kind, ulong AllocationBytes, uint Width, uint Height, bool External, bool History);
