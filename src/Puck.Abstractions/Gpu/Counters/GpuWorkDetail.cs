namespace Puck.Abstractions.Gpu;

/// <summary>One kernel-detail row belonging to a physical pass. The ledger captures the name with its submission.</summary>
/// <param name="Pass">The zero-based owning pass index.</param>
/// <param name="Detail">The authored layer or body name.</param>
public readonly record struct GpuWorkDetail(int Pass, string Detail);
