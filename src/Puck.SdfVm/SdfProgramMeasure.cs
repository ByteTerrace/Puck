using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Measures a candidate program built into a throwaway <see cref="SdfProgramBuilder"/> — the shared tail
/// every capacity-probe/dry-run caller here takes: emit, build, read back the packed word/instance counts.</summary>
public static class SdfProgramMeasure {
    /// <summary>Emits into a fresh builder and measures the built program.</summary>
    /// <param name="emit">Emits the candidate content into the throwaway builder.</param>
    /// <returns>The built program's packed word count and instance count.</returns>
    public static (int Words, int Instances) Measure(Action<SdfProgramBuilder> emit) {
        var builder = new SdfProgramBuilder();

        emit(builder);

        var built = builder.Build();

        return (Words: built.Words.Length, Instances: built.Instances.Count);
    }
}
