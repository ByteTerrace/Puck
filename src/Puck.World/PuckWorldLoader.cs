using System.Text;
using Puck.Abstractions.Machines;
using Puck.World.Transpiler.Composition;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;

namespace Puck.World;

/// <summary>Transparent runtime boot loader and compiler for Puck DSL (.puck) files.</summary>
internal static class PuckWorldLoader {
    /// <summary>Resolves a world definition from a .puck or .world.json file, transparently compiling .puck sources in-memory.</summary>
    /// <param name="explicitPath">The authored world path, or null for the shipped default.</param>
    /// <param name="source">The loaded definition and source path.</param>
    /// <param name="failure">The named refusal reason, or empty on success.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for the selected host catalog.</param>
    /// <param name="catalog">The selected host machine catalog used for provider composition and validation.</param>
    public static bool TryResolveWorld(string? explicitPath, out WorldDefinitionSource source, out string failure, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) {
        var explicitly = !string.IsNullOrWhiteSpace(value: explicitPath);
        string path;

        try {
            path = (explicitly
                ? Path.GetFullPath(path: explicitPath!)
                : Path.Combine(path1: AppContext.BaseDirectory, path2: WorldDefinitionLoader.DefaultRelativePath)
            );
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            source = null!;
            failure = $"[world] definition refused: cannot resolve path '{explicitPath}' ({ex.Message})";
            return false;
        }

        if (!path.EndsWith(".puck", StringComparison.OrdinalIgnoreCase)) {
            return WorldDefinitionLoader.TryResolve(explicitPath: explicitPath, failure: out failure, source: out source, catalogFingerprint: catalogFingerprint, catalog: catalog);
        }

        if (!File.Exists(path: path)) {
            source = null!;
            failure = $"[world] definition refused: path '{path}' not found.";
            return false;
        }

        string puckSource;
        try {
            puckSource = File.ReadAllText(path: path);
        } catch (Exception ex) {
            source = null!;
            failure = $"[world] definition refused: cannot read '{path}' ({ex.Message})";
            return false;
        }

        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source: puckSource);
        if (parseResult.Diagnostics.HasErrors || parseResult.Value is null) {
            source = null!;
            failure = $"[world] definition refused: syntax errors in '{path}':\n" +
                      parseResult.Diagnostics.FormatReport(sourceText: puckSource, filePath: path);
            return false;
        }

        var loweringDiags = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            basePath: Path.GetDirectoryName(path: path),
            diagnostics: loweringDiags,
            document: parseResult.Value
        );

        if (loweringDiags.HasErrors || loweringResult.Value is null) {
            source = null!;
            failure = $"[world] definition refused: lowering errors in '{path}':\n" +
                      loweringDiags.FormatReport(sourceText: puckSource, filePath: path);
            return false;
        }

        var jsonObject = loweringResult.Value;
        var rawBytes = Encoding.UTF8.GetBytes(s: jsonObject.ToJsonString());
        var finalBytes = rawBytes;

        if (!PuckDocumentComposer.TryComposeWorldDocument(
            rootResolvedPath: path,
            rootBytes: rawBytes,
            composed: out var composed,
            chainBytes: out _,
            reason: out var composeReason,
            catalogFingerprint: catalogFingerprint,
            catalog: catalog
        )) {
            source = null!;
            failure = $"[world] definition refused: {path} composition refused: {composeReason}";
            return false;
        }

        if (composed is not null) {
            finalBytes = Encoding.UTF8.GetBytes(s: composed.ToJsonString());
        }

        var directory = (Path.GetDirectoryName(path: path) is { Length: > 0 } dir ? dir : AppContext.BaseDirectory);
        var neighbours = new WorldFileNeighbourResolver(baseDirectory: () => directory, catalogFingerprint: catalogFingerprint, catalog: catalog);

        if (!WorldDefinitionLoader.TryLoad(
            definition: out var loadedDef,
            instanceIdentity: WorldDefinitionLoader.BootInstanceName,
            neighbours: neighbours,
            reason: out var loadReason,
            sourceName: path,
            utf8: finalBytes,
            catalogFingerprint: catalogFingerprint,
            catalog: catalog
        )) {
            source = null!;
            failure = $"[world] definition refused: {loadReason}";
            return false;
        }

        Console.Error.WriteLine(value: $"[world] definition: {path} ({(explicitly ? "--world .puck transpiled" : "shipped default")})");
        source = new WorldDefinitionSource(
            Definition: loadedDef!,
            SourcePath: path
        );
        failure = string.Empty;
        return true;
    }
}
