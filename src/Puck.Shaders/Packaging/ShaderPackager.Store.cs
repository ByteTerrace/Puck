using System.Text;
using System.Text.Json;
using Puck.Assets;

namespace Puck.Shaders;

public sealed partial class ShaderPackager {
    /// <summary>The name of the directory a build writes its package store to: one package for each pipeline source a
    /// shipped world names, which a <see cref="ShaderPackager"/> given the directory as its <see cref="Store"/> loads in
    /// place of compiling the source.</summary>
    public const string StoreDirectoryName = "packages";
    // The leading hexadecimal digits of a key that name its package's directory in a store. The directory name only
    // finds a candidate: a load holds the candidate's whole key to the source's before using it.
    private const int StoreNameLength = 16;

    /// <summary>Gets the full path of the build's package store this packager loads a source's package from, or
    /// <see langword="null"/> when it has none.</summary>
    public string? Store { get; }

    /// <summary>Returns the key a source's package is stored under: a content pin over the logical path of the document,
    /// the name the pipeline plans under, every file of its closure with its pin, and each pass's name, interface hash
    /// and generated declarations' pin, in execution order. It is the key of exactly the package
    /// <see cref="BuildAsync"/> writes from the source with the same name and the default root, so a store holds one
    /// package per source and name. Nothing compiles: the closure is collected the way a build collects it.</summary>
    /// <param name="source">The pipeline document or one-off shader source.</param>
    /// <param name="name">The instance name, which names a one-off shader's pipeline and its one pass. A pipeline
    /// document names itself.</param>
    /// <returns>The key, 64 lowercase hexadecimal digits.</returns>
    /// <exception cref="ShaderClosureRefusedException">The closure reaches outside the source's directory or exceeds a
    /// limit.</exception>
    /// <exception cref="IOException">A file of the closure cannot be read.</exception>
    /// <exception cref="JsonException">A pipeline document is malformed.</exception>
    /// <exception cref="ShaderPipelineCompilationException">The document does not plan.</exception>
    public string KeyOf(string source, string name) => SurveyKey(
        name: name,
        source: source
    ).Key;
    /// <summary>Returns the key a package's manifest records its source under: the same key
    /// <see cref="KeyOf(string, string)"/> computes from the source it was built from.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The key, 64 lowercase hexadecimal digits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    public static string KeyOf(ShaderPackageManifest manifest) {
        ArgumentNullException.ThrowIfNull(argument: manifest);

        return Key(
            document: manifest.Document,
            files: manifest.Files.Select(selector: static file => (file.Path, file.Pin)),
            name: manifest.Name,
            passes: manifest.Passes.Select(selector: static pass => (pass.Name, pass.Interface.Pin, pass.Declarations.Pin))
        );
    }
    /// <summary>Returns the directory a store holds the package of a key in.</summary>
    /// <param name="store">The store directory.</param>
    /// <param name="key">The key, as <see cref="KeyOf(string, string)"/> returns it.</param>
    /// <returns>The package directory's full path.</returns>
    /// <exception cref="ArgumentException"><paramref name="store"/> is empty or white space, or <paramref name="key"/> is
    /// shorter than a store directory's name.</exception>
    public static string StorePathOf(string store, string key) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: store);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: StoreNameLength,
            value: (key?.Length ?? 0)
        );

        return Path.GetFullPath(path: Path.Combine(
            path1: store,
            path2: key![..StoreNameLength]
        ));
    }
    /// <summary>Writes a source's package into the <see cref="Store"/> under its key, compiling it with this packager's
    /// compiler, unless the store already holds a package with that key, which is verified and kept.</summary>
    /// <param name="source">The pipeline document or one-off shader source.</param>
    /// <param name="name">The instance name, which names a one-off shader's pipeline and its one pass.</param>
    /// <param name="cancellationToken">The token that cancels the build.</param>
    /// <returns>The outcome and the package's directory. A source whose closure does not fit a package, or that does
    /// not compile, is the build's refusal or failure.</returns>
    /// <exception cref="InvalidOperationException">This packager has no store.</exception>
    public async Task<(ShaderPackageResult Result, string Package)> StoreAsync(string source, string name, CancellationToken cancellationToken = default) {
        var store = (Store ?? throw new InvalidOperationException(message: "The packager has no store to write a package into."));
        string key;

        try {
            key = KeyOf(
                name: name,
                source: source
            );
        } catch (Exception exception) when (IsSourceRefusal(exception: exception)) {
            return (Refusal(exception: exception), string.Empty);
        }

        var package = StorePathOf(
            key: key,
            store: store
        );

        if (IsPackage(path: package)) {
            var kept = await LoadAsync(
                cancellationToken: cancellationToken,
                package: package
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (kept.Manifest is not null) &&
                string.Equals(
                    a: KeyOf(manifest: kept.Manifest),
                    b: key,
                    comparisonType: StringComparison.Ordinal
                )
            ) {
                return (kept, package);
            }
        }

        var built = await BuildAsync(
            cancellationToken: cancellationToken,
            name: name,
            output: package,
            source: source
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (built, package);
    }

    private static bool IsSourceRefusal(Exception exception) =>
        (exception is ShaderClosureRefusedException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or ShaderPipelineCompilationException);
    private static string Key(string document, string name, IEnumerable<(string Path, string Pin)> files, IEnumerable<(string Name, string Interface, string Declarations)> passes) {
        var text = new StringBuilder()
            .Append(value: ShaderPackageManifest.SchemaName).Append(value: '\n')
            .Append(value: "document ").Append(value: document).Append(value: '\n')
            .Append(value: "name ").Append(value: name).Append(value: '\n');

        foreach (var (path, pin) in files.OrderBy(
            comparer: StringComparer.Ordinal,
            keySelector: static file => file.Path
        )) {
            _ = text.Append(value: "file ").Append(value: path).Append(value: ' ').Append(value: pin).Append(value: '\n');
        }

        foreach (var (pass, shaderInterface, declarations) in passes) {
            _ = text.Append(value: "pass ").Append(value: pass).Append(value: ' ').Append(value: shaderInterface).Append(value: ' ').Append(value: declarations).Append(value: '\n');
        }

        return ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: text.ToString())).Hex;
    }
    private static ShaderPackageResult Refusal(Exception exception) => new(
        Code: ((exception as ShaderClosureRefusedException)?.Code),
        Manifest: null,
        Message: exception.Message,
        Pipeline: null,
        Status: ShaderPipelineLoadStatus.Failed
    );
    // A source file's load: its store package when the store holds one keyed by it, which compiles nothing, and the
    // loader's compile otherwise. A store package that fails its load is the load's outcome; nothing compiles in its
    // place. A source that does not survey is left to the loader, which reports the same fault by name.
    private ShaderPipelineLoadResult LoadSourceFile(string name, string path, CancellationToken cancellationToken) {
        if (Store is not { } store) {
            return m_loader.Load(
                cancellationToken: cancellationToken,
                name: name,
                path: path
            );
        }

        string? key = null;

        try {
            var (surveyed, files) = SurveyKey(
                name: name,
                source: path
            );
            var package = StorePathOf(
                key: surveyed,
                store: store
            );

            key = surveyed;

            if (IsPackage(path: package)) {
                var loaded = LoadAsync(
                    cancellationToken: cancellationToken,
                    package: package
                ).GetAwaiter().GetResult();

                if (
                    (loaded.Manifest is null) ||
                    string.Equals(
                        a: KeyOf(manifest: loaded.Manifest),
                        b: surveyed,
                        comparisonType: StringComparison.Ordinal
                    )
                ) {
                    return new ShaderPipelineLoadResult(
                        Dependencies: [.. files],
                        Message: ((loaded.Status == ShaderPipelineLoadStatus.Compiled)
                            ? $"from the build's package {Path.GetFileName(path: package)}: {loaded.Message}"
                            : loaded.Message),
                        Pipeline: loaded.Pipeline,
                        Status: loaded.Status
                    );
                }
            }
        } catch (Exception exception) when (IsSourceRefusal(exception: exception)) {
            key = null;
        }

        var compiled = m_loader.Load(
            cancellationToken: cancellationToken,
            name: name,
            path: path
        );

        return ((compiled.Status == ShaderPipelineLoadStatus.Unsupported)
            ? compiled with {
                Message = $"[{ShaderClosureRefusedException.PackageAbsent}] '{path}' has no package in the build's store '{store}'{((key is null) ? string.Empty : $" under key {key}")}, and nothing can compile it here: {compiled.Message}",
            }
            : compiled);
    }
    // The key of a source and the full paths of its closure's files.
    private (string Key, IReadOnlyCollection<string> Files) SurveyKey(string source, string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: source);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);

        var documentPath = Path.GetFullPath(path: source);
        var rootPath = Path.GetDirectoryName(path: documentPath)!;
        var plan = Plan(
            documentPath: documentPath,
            name: name
        );
        var survey = Survey(
            documentPath: documentPath,
            plan: plan,
            rootPath: rootPath
        );

        return (Key(
            document: LogicalPath(
                path: documentPath,
                rootPath: rootPath
            ),
            files: survey.Files.Select(selector: file => (LogicalPath(
                path: file.Key,
                rootPath: rootPath
            ), (ContentPin.Prefix + file.Value.Hash))),
            name: plan.Definition.Name,
            passes: plan.Passes.Select(selector: pass => (pass.Name, pass.Parameters.Interface.Hash.ToString(), (ContentPin.Prefix + ShaderSourceClosure.HashOf(text: survey.Generated[pass.Name].DeclarationsText))))
        ), survey.Files.Keys.ToArray());
    }
}
