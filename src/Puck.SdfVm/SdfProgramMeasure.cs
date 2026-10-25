using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Measures content emitted into a throwaway <see cref="SdfProgramBuilder"/>: a dry run's packed size, or a
/// reservation's instance and material counts.</summary>
public static class SdfProgramMeasure {
    /// <summary>Emits into a fresh builder and builds the program: the packed word count and instance count, and every
    /// refusal <see cref="SdfProgramBuilder.Build"/> makes.</summary>
    /// <param name="emit">Emits the candidate content into the throwaway builder.</param>
    /// <returns>The built program's packed word count and instance count.</returns>
    public static (int Words, int Instances) Measure(Action<SdfProgramBuilder> emit) {
        ArgumentNullException.ThrowIfNull(argument: emit);

        var builder = new SdfProgramBuilder();

        emit(builder);

        var built = builder.Build();

        return (Words: built.Words.Length, Instances: built.Instances.Count);
    }
    /// <summary>Emits into a fresh builder and counts what it declares without building the program. Emission still
    /// makes every refusal the builder makes as it goes, the instance and material ceilings among them
    /// (<see cref="SdfProgramCapacityException"/>).</summary>
    /// <param name="emit">Emits the content into the throwaway builder.</param>
    /// <returns>The emitted instance and material counts.</returns>
    public static (int Instances, int Materials) Count(Action<SdfProgramBuilder> emit) {
        ArgumentNullException.ThrowIfNull(argument: emit);

        var builder = new SdfProgramBuilder();

        emit(builder);

        return (Instances: builder.InstanceCount, Materials: builder.MaterialCount);
    }
}
