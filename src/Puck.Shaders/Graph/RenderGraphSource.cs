using System.Diagnostics.CodeAnalysis;

namespace Puck.Shaders;

/// <summary>Reads and plans the source a world's graph row names.</summary>
public static class RenderGraphSource {
    /// <summary>Reads the source at <paramref name="path"/> the way every reader of a pipeline source reads it
    /// (<see cref="ShaderPipelineSource.TryRead"/>: a graph document, a one-off shader or a package directory) and plans
    /// its definition against the engine's packages.</summary>
    /// <param name="name">The instance name, which names a one-off shader's pipeline and its one pass.</param>
    /// <param name="path">The full path of the source.</param>
    /// <param name="packages">The packages the host offers.</param>
    /// <param name="plan">The plan, when this returns <see langword="true"/>.</param>
    /// <param name="reason">Why the source could not be read or planned, naming every diagnostic.</param>
    /// <returns><see langword="true"/> when the source planned.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="path"/> is
    /// <see langword="null"/> or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="packages"/> is <see langword="null"/>.</exception>
    public static bool TryPlan(string name, string path, RenderGraphPackageCatalog packages, [NotNullWhen(returnValue: true)] out RenderGraphPlan? plan, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: packages);

        plan = null;

        if (!ShaderPipelineSource.TryRead(
            name: name,
            path: path,
            reason: out reason,
            source: out var source
        )) {
            return false;
        }

        if (!new RenderGraphCompiler(packages: packages).TryCompile(
            definition: source.Definition,
            diagnostics: out var diagnostics,
            plan: out plan
        )) {
            reason = string.Join(
                separator: "; ",
                values: diagnostics.Select(selector: static diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}")
            );

            return false;
        }

        reason = string.Empty;

        return true;
    }
}
