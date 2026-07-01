namespace Puck.Abstractions.Gpu;

/// <summary>
/// Describes one descriptor binding of a compute pipeline's single descriptor set (set 0).
/// </summary>
/// <param name="Binding">The binding index within set 0. An array binding (<see cref="Count"/> &gt; 1) occupies <c>Binding</c>..<c>Binding + Count - 1</c>; the next binding's index must leave that room.</param>
/// <param name="Kind">The <see cref="GpuComputeBindingKind"/> of the descriptor.</param>
/// <param name="Count">The array length of the binding (a descriptor array, e.g. <c>RWTexture2D&lt;float4&gt; sources[4]</c>); 1 for a scalar binding. Each element is written at its <c>arrayElement</c>.</param>
public readonly record struct GpuComputeBinding(uint Binding, uint Kind, uint Count = 1);
