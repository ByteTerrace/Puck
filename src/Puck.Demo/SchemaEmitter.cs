using Puck.Scene;

namespace Puck.Demo;

/// <summary>
/// The headless <c>--emit-schema</c> utility, extracted from the retired composition root so it survives the Arc 3
/// Beat B teardown. Arc 3 Beat A (OQ-16) relocates this to a <c>tools/</c> generator; until then it is a standing
/// island with no caller.
/// </summary>
internal static class SchemaEmitter {
    /// <summary>Writes the run-document JSON Schema to a path: the schema is exported from the SAME source-gen options
    /// that read documents, so it cannot drift from the model.</summary>
    /// <param name="path">The output path; parent directories are created.</param>
    /// <returns>0 on success.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public static int EmitSchema(string path) {
        ArgumentNullException.ThrowIfNull(path);

        var schema = RunDocumentSchema.Export();
        var directory = Path.GetDirectoryName(path: path);

        if (!string.IsNullOrEmpty(value: directory)) {
            _ = Directory.CreateDirectory(path: directory);
        }

        File.WriteAllText(contents: schema, path: path);
        Console.WriteLine(value: $"[emit-schema] wrote the run-document schema ({schema.Length} chars) to '{path}'.");

        return 0;
    }
}
