using System.Collections.Concurrent;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Measures a candidate program emitted into a throwaway <see cref="SdfProgramBuilder"/>, the shared tail of
/// every capacity probe and dry run. A world's worst-case candidate runs to millions of instructions and is measured
/// once per render-relevant mutation (each responding placement a light switch flips is one), so the builders emit into
/// storage kept here for the next measure instead of growing large-object arrays from empty each time.</summary>
public static class SdfProgramMeasure {
    // The storage measures emit into; one per concurrent measure, each kept at the largest program it held.
    private static readonly ConcurrentBag<SdfProgramBuilderStorage> HeldStorage = [];

    /// <summary>Emits into a fresh builder and builds the program: the packed word count and instance count, and every
    /// refusal <see cref="SdfProgramBuilder.Build"/> makes.</summary>
    /// <param name="emit">Emits the candidate content into the throwaway builder.</param>
    /// <returns>The built program's packed word count and instance count.</returns>
    public static (int Words, int Instances) Measure(Action<SdfProgramBuilder> emit) {
        ArgumentNullException.ThrowIfNull(argument: emit);

        var storage = Rent();

        try {
            var builder = new SdfProgramBuilder(storage: storage);

            emit(builder);

            var built = builder.Build();

            return (Words: built.Words.Length, Instances: built.Instances.Count);
        } finally {
            Return(storage: storage);
        }
    }
    /// <summary>Emits into a fresh builder and counts its instances without building the program: the measure of a
    /// consumer that grows its buffers, which reads no packed word count. Emission still makes every refusal the
    /// builder makes as it goes, the instance and material ceilings among them (<see cref="SdfProgramCapacityException"/>).</summary>
    /// <param name="emit">Emits the candidate content into the throwaway builder.</param>
    /// <returns>The emitted program's instance count.</returns>
    public static int Instances(Action<SdfProgramBuilder> emit) {
        ArgumentNullException.ThrowIfNull(argument: emit);

        var storage = Rent();

        try {
            var builder = new SdfProgramBuilder(storage: storage);

            emit(builder);

            return builder.InstanceCount;
        } finally {
            Return(storage: storage);
        }
    }

    private static SdfProgramBuilderStorage Rent() => (HeldStorage.TryTake(result: out var held) ? held : new SdfProgramBuilderStorage());
    private static void Return(SdfProgramBuilderStorage storage) {
        storage.Clear();
        HeldStorage.Add(item: storage);
    }
}
