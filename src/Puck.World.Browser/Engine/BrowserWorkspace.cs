using System.Runtime.CompilerServices;
using System.Text;
using Puck.Abstractions.Documents;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Composition;
using Puck.World.Transpiler.Validation;

namespace Puck.World.Browser.Engine;

/// <summary>One diagnostic of a compiled source, located in the source that carries it.</summary>
/// <param name="Code">The diagnostic's code (<c>PUCKnnn</c>).</param>
/// <param name="Severity"><c>error</c>, <c>warning</c> or <c>information</c>.</param>
/// <param name="Message">The diagnostic's text.</param>
/// <param name="Path">The workspace-relative path of the source the span lies in.</param>
/// <param name="Line">The 1-based line, or 0 for a finding about the whole document.</param>
/// <param name="Column">The 1-based column, or 0 for a finding about the whole document.</param>
/// <param name="Length">The span's length in characters.</param>
public sealed record BrowserSourceDiagnostic(string Code, string Severity, string Message, string? Path, int Line, int Column, int Length);
/// <summary>Where one member of a compiled document was authored.</summary>
/// <param name="Path">The workspace-relative path of the source that authored it.</param>
/// <param name="Line">The 1-based line.</param>
/// <param name="Column">The 1-based column.</param>
/// <param name="Length">The span's length in characters.</param>
/// <param name="Module">The slash-separated module instance the member was expanded in, or <see langword="null"/>
/// outside one.</param>
public sealed record BrowserSourceOrigin(string? Path, int Line, int Column, int Length, string? Module);
/// <summary>One world a composition source declares, compiled.</summary>
/// <param name="Name">The declared world name.</param>
/// <param name="Document">The world's canonical document text.</param>
/// <param name="Entry">Whether the world is the composition's entry.</param>
/// <param name="SourceMap">The world's JSON pointers mapped to where each was authored.</param>
public sealed record BrowserCompiledWorld(string Name, string Document, bool Entry, IReadOnlyDictionary<string, BrowserSourceOrigin> SourceMap);
/// <summary>A <c>CompileSource</c> outcome.</summary>
/// <param name="Ok">Whether the source compiled and nothing it reported is an error.</param>
/// <param name="Document">The canonical document text for a source that emits one document, or
/// <see langword="null"/>.</param>
/// <param name="Worlds">Every world a composition source declares, compiled; empty otherwise.</param>
/// <param name="Diagnostics">Everything the language server publishes for the source.</param>
/// <param name="SourceMap">The document's JSON pointers mapped to where each was authored.</param>
public sealed record BrowserSourceCompileResult(bool Ok, string? Document, IReadOnlyList<BrowserCompiledWorld> Worlds, IReadOnlyList<BrowserSourceDiagnostic> Diagnostics, IReadOnlyDictionary<string, BrowserSourceOrigin> SourceMap);
/// <summary>A <c>ComposeSource</c> outcome.</summary>
/// <param name="Ok">Whether the file's basis-and-imports graph composed and nothing its diagnosis reported is an
/// error.</param>
/// <param name="Composed">The composed standalone document text whenever composition itself succeeded, valid or
/// not.</param>
/// <param name="Diagnostics">The source tier's findings (for a source) and the semantic tier's findings over the
/// composed world.</param>
public sealed record BrowserSourceComposeResult(bool Ok, string? Composed, IReadOnlyList<BrowserSourceDiagnostic> Diagnostics);
/// <summary>The authoring workspace the browser engine compiles from: a directory of <c>.puck</c> sources (and any
/// <c>.world.json</c> documents beside them) that the engine's own toolchain reads exactly as it reads a checkout. In
/// the AppBundle the directory is <see cref="MountRoot"/> in the runtime's in-memory file system; a test roots one in
/// a temporary directory.</summary>
/// <remarks>Paths crossing this type are workspace-relative, forward-slashed, and may not climb out of the workspace
/// (<c>games/klondike.puck</c>). A <c>basis</c> or import name resolves to a mounted source through the document
/// source every Puck host installs (<see cref="WorldDefinitionFileSource.UseLocalDocuments"/> with
/// <see cref="PuckDocumentComposer"/>), which this assembly installs when it loads.</remarks>
public sealed class BrowserWorkspace {
    /// <summary>The directory the AppBundle mounts its workspace at.</summary>
    public const string MountRoot = "/worlds";

    /// <summary>Installs the transpiler's document composer as the local document source, as the game, the CLI and the
    /// test hosts do, so a document name resolves to its <c>.puck</c> source when one is mounted.</summary>
    [ModuleInitializer]
    internal static void InstallDocumentSource() => WorldDefinitionFileSource.UseLocalDocuments(source: PuckDocumentComposer.Instance);

    private static string Normalize(string path) => Path.GetFullPath(path: path).Replace(
        newChar: '/',
        oldChar: '\\'
    );

    /// <summary>Creates a workspace over <paramref name="root"/>.</summary>
    /// <param name="root">The workspace directory; it need not exist yet.</param>
    public BrowserWorkspace(string root) {
        ArgumentException.ThrowIfNullOrEmpty(argument: root);
        Root = Normalize(path: root).TrimEnd(trimChar: '/');
    }

    /// <summary>Gets the workspace directory, full and forward-slashed.</summary>
    public string Root { get; }

    /// <summary>Returns the full path of a workspace-relative path.</summary>
    /// <param name="path">The workspace-relative path.</param>
    /// <param name="fullPath">The full, forward-slashed path on success.</param>
    /// <param name="error">Why the path is refused, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the path names a file inside the workspace.</returns>
    public bool TryResolve(string path, out string fullPath, out string? error) {
        fullPath = string.Empty;

        if (string.IsNullOrEmpty(value: path)) {
            error = "a workspace path is empty.";
            return false;
        }
        if (path.Contains(value: '\\') || path.StartsWith(value: '/') || (path.Contains(value: ':'))) {
            error = $"'{path}' is not a workspace-relative path: write it forward-slashed, from the workspace root.";
            return false;
        }
        foreach (var segment in path.Split(separator: '/')) {
            if (segment is "" or "." or "..") {
                error = $"'{path}' is not a workspace-relative path: every segment names a directory or a file.";
                return false;
            }
        }

        fullPath = $"{Root}/{path}";
        error = null;
        return true;
    }
    /// <summary>Returns a full path as a workspace-relative one, or unchanged when it lies outside the workspace.</summary>
    /// <param name="fullPath">The full path.</param>
    /// <returns>The workspace-relative path.</returns>
    public string Relative(string fullPath) {
        ArgumentNullException.ThrowIfNull(argument: fullPath);

        var normalized = fullPath.Replace(
            newChar: '/',
            oldChar: '\\'
        );

        return (normalized.StartsWith(
            comparisonType: (OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
            ),
            value: $"{Root}/"
        )
            ? normalized[(Root.Length + 1)..]
            : normalized
        );
    }
    /// <summary>Returns the <c>file:</c> URI a language client opens a workspace file under.</summary>
    /// <param name="path">The workspace-relative path.</param>
    /// <returns>The URI.</returns>
    public string DocumentUri(string path) => new Uri(uriString: $"{Root}/{path}").AbsoluteUri;
    /// <summary>Replaces the whole workspace with <paramref name="files"/>: every file not listed is removed.</summary>
    /// <param name="files">Each file's workspace-relative path and UTF-8 text.</param>
    /// <returns>Why the mount was refused, or <see langword="null"/>; a refused mount changes nothing.</returns>
    public string? Mount(IReadOnlyDictionary<string, string> files) {
        ArgumentNullException.ThrowIfNull(argument: files);

        var resolved = new List<(string FullPath, string Text)>(capacity: files.Count);

        foreach (var (path, text) in files) {
            if (!TryResolve(
                error: out var error,
                fullPath: out var fullPath,
                path: path
            )) {
                return error;
            }
            resolved.Add(item: (fullPath, (text ?? string.Empty)));
        }

        try {
            if (Directory.Exists(path: Root)) {
                Directory.Delete(
                    path: Root,
                    recursive: true
                );
            }
            _ = Directory.CreateDirectory(path: Root);

            foreach (var (fullPath, text) in resolved) {
                WriteFile(
                    fullPath: fullPath,
                    text: text
                );
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return $"the workspace could not be mounted: {exception.Message}";
        }

        return null;
    }
    /// <summary>Writes one workspace file, creating its directories.</summary>
    /// <param name="path">The workspace-relative path.</param>
    /// <param name="text">The UTF-8 text.</param>
    /// <returns>Why the write was refused, or <see langword="null"/>.</returns>
    public string? Write(string path, string text) {
        if (!TryResolve(
            error: out var error,
            fullPath: out var fullPath,
            path: path
        )) {
            return error;
        }

        try {
            WriteFile(
                fullPath: fullPath,
                text: (text ?? string.Empty)
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return $"'{path}' could not be written: {exception.Message}";
        }

        return null;
    }

    private static void WriteFile(string fullPath, string text) {
        if (Path.GetDirectoryName(path: fullPath) is { Length: > 0 } directory) {
            _ = Directory.CreateDirectory(path: directory);
        }
        File.WriteAllText(
            contents: text,
            path: fullPath
        );
    }

    /// <summary>Compiles one workspace source once and diagnoses it through both tiers, exactly as the language server
    /// does at full depth (<see cref="WorldSourceDiagnostics.DiagnoseSource"/>, then
    /// <see cref="WorldSourceDiagnostics.DiagnoseSemantic"/> over the same compilation).</summary>
    /// <param name="path">The source's workspace-relative path.</param>
    /// <returns>The compile result.</returns>
    public BrowserSourceCompileResult Compile(string path) {
        if (!TryRead(
            error: out var error,
            fullPath: out var fullPath,
            path: path,
            text: out var text
        )) {
            return new BrowserSourceCompileResult(
                Diagnostics: [Refusal(message: error!, path: path)],
                Document: null,
                Ok: false,
                SourceMap: new Dictionary<string, BrowserSourceOrigin>(),
                Worlds: []
            );
        }

        // A world document is JSON already: there is nothing to compile, so it comes back as the canonical document it
        // is, or refused when it is not one.
        if (WorldDocumentName.IsDocumentFile(path: fullPath) || !WorldDocumentName.IsSourceFile(path: fullPath)) {
            var document = (WorldDocumentName.IsDocumentFile(path: fullPath)
                ? ReadDocument(error: out error, fullPath: fullPath, text: text)
                : null
            );

            return new BrowserSourceCompileResult(
                Diagnostics: ((document is null)
                    ? [Refusal(message: (error ?? $"'{path}' is neither a .puck source nor a {WorldDocumentName.DocumentSuffix} document."), path: path)]
                    : []
                ),
                Document: ((document is null)
                    ? null
                    : Canonical(json: document)
                ),
                Ok: (document is not null),
                SourceMap: new Dictionary<string, BrowserSourceOrigin>(comparer: StringComparer.Ordinal),
                Worlds: []
            );
        }

        // One compile: the source tier's own compilation is the document this result carries, and the semantic tier
        // reads it rather than compiling the source again.
        var diagnosis = WorldSourceDiagnostics.DiagnoseSource(
            source: text,
            sourcePath: fullPath
        );
        var diagnostics = new DiagnosticBag();

        diagnostics.AddRange(diagnostics: diagnosis.Diagnostics);
        diagnostics.AddRange(diagnostics: WorldSourceDiagnostics.DiagnoseSemantic(diagnosis: diagnosis));

        var compilation = diagnosis.Compilation;
        var compiled = (compilation?.Success ?? false);

        return new BrowserSourceCompileResult(
            Diagnostics: Wire(diagnostics: diagnostics, fullPath: fullPath),
            Document: ((compiled && (compilation!.Json is { } json))
                ? Canonical(json: json)
                : null
            ),
            Ok: (compiled && !diagnostics.HasErrors),
            SourceMap: ((compilation is null)
                ? new Dictionary<string, BrowserSourceOrigin>(comparer: StringComparer.Ordinal)
                : Map(sourceMap: compilation.SourceMap)
            ),
            Worlds: (compiled
                ? [.. compilation!.Worlds.Select(selector: world => new BrowserCompiledWorld(
                    Document: Canonical(json: world.Json),
                    Entry: world.Entry,
                    Name: world.Name,
                    SourceMap: Map(sourceMap: world.SourceMap)
                ))]
                : []
            )
        );
    }
    /// <summary>Composes one workspace file's whole basis-and-imports graph through the installed document source and
    /// validates the composed world: a <c>.puck</c> source is compiled first (every mounted source it names is compiled
    /// on the way), and a <c>.world.json</c> document composes as the JSON it is. The findings are the source tier's
    /// (for a source) and the semantic tier's (<see cref="WorldSourceDiagnostics.DiagnoseSemantic"/>: composition,
    /// the engine's validation, the reference lint) — the same code the language server runs, not a validator of its
    /// own. A composition source composes its entry world.</summary>
    /// <param name="path">The file's workspace-relative path.</param>
    /// <returns>The compose result: <c>ok</c> when the graph composed and nothing it reported is an error, with the
    /// composed document whenever composition itself succeeded.</returns>
    public BrowserSourceComposeResult Compose(string path) {
        if (!TryRead(
            error: out var error,
            fullPath: out var fullPath,
            path: path,
            text: out var text
        )) {
            return Refused(message: error!, path: path);
        }

        WorldSourceDiagnosis diagnosis;
        System.Text.Json.Nodes.JsonObject root;

        if (WorldDocumentName.IsSourceFile(path: fullPath)) {
            diagnosis = WorldSourceDiagnostics.DiagnoseSource(
                source: text,
                sourcePath: fullPath
            );

            if ((diagnosis.Compilation is not { Success: true } compilation) || diagnosis.Diagnostics.HasErrors) {
                return new BrowserSourceComposeResult(
                    Composed: null,
                    Diagnostics: Wire(diagnostics: diagnosis.Diagnostics, fullPath: fullPath),
                    Ok: false
                );
            }
            if (compilation.Worlds.Count > 0) {
                if (compilation.Worlds.FirstOrDefault(predicate: static world => world.Entry) is not { } entry) {
                    return Refused(message: $"'{path}' declares {compilation.Worlds.Count} worlds and none is its entry (`entry world`), so it composes to no one document.", path: path);
                }
                root = entry.Json;
            } else {
                root = compilation.RequireJson();
            }
        } else if (WorldDocumentName.IsDocumentFile(path: fullPath)) {
            // A world document composes as the JSON it is, through the same document source a source's chain resolves
            // through, so a JSON root can import a .puck fragment and a .puck root can name a JSON basis.
            if (ReadDocument(error: out error, fullPath: fullPath, text: text) is not { } document) {
                return Refused(message: error!, path: path);
            }

            root = document;
            diagnosis = new WorldSourceDiagnosis(
                Compilation: new WorldCompilation(
                    Diagnostics: new DiagnosticBag(),
                    DiscoveredEmbeddings: new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal),
                    Document: null,
                    Json: document,
                    SourceMap: new SourceMap(),
                    TestWorlds: []
                ),
                Diagnostics: new DiagnosticBag(),
                Document: null,
                SourcePath: fullPath
            );
        } else {
            return Refused(message: $"'{path}' is neither a .puck source nor a {WorldDocumentName.DocumentSuffix} document.", path: path);
        }

        var composed = (PuckDocumentComposer.TryComposeWorldDocument(
            chainBytes: out _,
            composed: out var tree,
            reason: out _,
            rootBytes: CanonicalJsonDocument.Serialize(node: root),
            rootResolvedPath: fullPath
        )
            ? Canonical(json: (tree ?? root))
            : null
        );
        var diagnostics = new DiagnosticBag();

        // A composition refusal is the semantic tier's own first finding, reported where the basis is named.
        diagnostics.AddRange(diagnostics: diagnosis.Diagnostics);
        diagnostics.AddRange(diagnostics: WorldSourceDiagnostics.DiagnoseSemantic(diagnosis: diagnosis));

        return new BrowserSourceComposeResult(
            Composed: composed,
            Diagnostics: Wire(diagnostics: diagnostics, fullPath: fullPath),
            Ok: ((composed is not null) && !diagnostics.HasErrors)
        );
    }

    private string? ReadDocumentError(string fullPath, string detail) => $"'{Relative(fullPath: fullPath)}' is not a JSON world document: {detail}";
    private System.Text.Json.Nodes.JsonObject? ReadDocument(string fullPath, string text, out string? error) {
        try {
            if (System.Text.Json.Nodes.JsonNode.Parse(json: text) is System.Text.Json.Nodes.JsonObject document) {
                error = null;

                return document;
            }

            error = ReadDocumentError(detail: "it does not hold a JSON object.", fullPath: fullPath);
        } catch (System.Text.Json.JsonException exception) {
            error = ReadDocumentError(detail: exception.Message, fullPath: fullPath);
        }

        return null;
    }
    // Each diagnostic located in the workspace file that carries its span.
    private IReadOnlyList<BrowserSourceDiagnostic> Wire(DiagnosticBag diagnostics, string fullPath) => [.. diagnostics.Select(selector: diagnostic => new BrowserSourceDiagnostic(
        Code: diagnostic.Code,
        Column: diagnostic.Span.Column,
        Length: diagnostic.Span.Length,
        Line: diagnostic.Span.Line,
        Message: diagnostic.Message,
        Path: Relative(fullPath: (diagnostic.SourcePath ?? fullPath)),
        Severity: diagnostic.Severity switch {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            _ => "information",
        }
    ))];
    private static BrowserSourceDiagnostic Refusal(string message, string path) => new(
        Code: PuckDiagnosticCodes.InvalidValue,
        Column: 0,
        Length: 0,
        Line: 0,
        Message: message,
        Path: path,
        Severity: "error"
    );
    private static BrowserSourceComposeResult Refused(string message, string path) => new(
        Composed: null,
        Diagnostics: [Refusal(message: message, path: path)],
        Ok: false
    );
    private static string Canonical(System.Text.Json.Nodes.JsonObject json) => Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: json));
    private bool TryRead(string path, out string fullPath, out string text, out string? error) {
        text = string.Empty;

        if (!TryResolve(
            error: out error,
            fullPath: out fullPath,
            path: path
        )) {
            return false;
        }
        if (!File.Exists(path: fullPath)) {
            error = $"no source is mounted at '{path}'.";
            return false;
        }

        text = File.ReadAllText(path: fullPath);
        return true;
    }
    private Dictionary<string, BrowserSourceOrigin> Map(SourceMap sourceMap) {
        var map = new Dictionary<string, BrowserSourceOrigin>(comparer: StringComparer.Ordinal);

        foreach (var (pointer, origin) in sourceMap.Snapshot().OrderBy(keySelector: static entry => entry.Key, comparer: StringComparer.Ordinal)) {
            map[pointer] = new BrowserSourceOrigin(
                Column: origin.Span.Column,
                Length: origin.Span.Length,
                Line: origin.Span.Line,
                Module: origin.ModuleInstancePath,
                Path: ((origin.SourcePath is { } sourcePath)
                    ? Relative(fullPath: sourcePath)
                    : null
                )
            );
        }

        return map;
    }
}
