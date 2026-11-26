using Puck.Abstractions.Memory;

namespace Puck.SignedDistance;

/// <summary>Where a program built by <see cref="SdfProgramBuilder.Build(SdfProgram?, SdfProgramStorage?)"/> holds its
/// words, its appearance section and its instructions: native blocks of this allocator, charged to this observer and freed
/// by the program's last claim (<see cref="SdfProgram.Release"/>). A program built with none keeps managed arrays and needs
/// no release.</summary>
/// <param name="Allocator">The native allocator the blocks come from.</param>
/// <param name="Observer">The owner the blocks are charged to, or <see langword="null"/> for unmetered storage.</param>
public sealed record SdfProgramStorage(IAllocator Allocator, IAllocationObserver? Observer = null);
