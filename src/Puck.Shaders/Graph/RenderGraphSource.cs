using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Puck.Shaders;

/// <summary>Reads and plans the graph document a world's graph row names.</summary>
public static class RenderGraphSource {
    /// <summary>Reads the graph document at <paramref name="path"/> and plans it against the engine's packages.</summary>
    /// <param name="path">The full path of a <c>puck.render.graph.v1</c> document.</param>
    /// <param name="packages">The packages the host offers.</param>
    /// <param name="plan">The plan, when this returns <see langword="true"/>.</param>
    /// <param name="reason">Why the document could not be read or planned, naming every diagnostic.</param>
    /// <returns><see langword="true"/> when the document planned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="packages"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryPlan(string path, RenderGraphPackageCatalog packages, [NotNullWhen(returnValue: true)] out RenderGraphPlan? plan, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: path);
        ArgumentNullException.ThrowIfNull(argument: packages);

        plan = null;

        RenderGraphDefinition definition;

        try {
            definition = RenderGraphDefinition.Parse(json: File.ReadAllText(path: path));
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)) {
            reason = exception.Message;

            return false;
        }

        if (!new RenderGraphCompiler(packages: packages).TryCompile(
            definition: definition,
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
