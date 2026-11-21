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

        return (Words: built.WordCount, Instances: built.Instances.Count);
    }
}
