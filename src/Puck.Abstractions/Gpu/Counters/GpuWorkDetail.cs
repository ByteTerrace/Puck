namespace Puck.Abstractions.Gpu;

/// <summary>A named row of work within a configured pass.</summary>
/// <param name="Pass">The zero-based configured pass index.</param>
/// <param name="Detail">The label within that pass. The reserved label <c>plain</c> holds work outside named details.</param>
public readonly record struct GpuWorkDetail(int Pass, string Detail);
