namespace Puck.Abstractions.Gpu;

/// <summary>
/// The refusal of a candidate whose descriptor pools do not fit its device's shader-visible heaps, its message carrying
/// <see cref="GpuDescriptorHeapBudget.RefusalCode"/> and naming the owner. Heap space is the one input such a refusal
/// depends on that the owner's own inputs do not name, so an owner refused with it tries again when the heap's
/// <see cref="IGpuBindings.HeapReleaseRevision"/> changes, and a refusal of any other kind does not.
/// </summary>
/// <param name="message">The refusal, as <see cref="GpuDescriptorHeapBudget.TryAdmit"/> wrote it.</param>
public sealed class GpuDescriptorHeapRefusalException(string message) : InvalidOperationException(message: message);
