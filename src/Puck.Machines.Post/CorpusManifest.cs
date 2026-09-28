using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Machines.Post;

/// <summary>
/// The external corpora a battery reads, declared in a <c>corpora.json</c> beside its program: each names a release
/// archive by URL, version, and SHA-256, or a URL prefix and individually hashed relative files. A corpus
/// resolves, in order, to an explicit command-line root, else to its entry in the local cache
/// (under the OS local application-data directory unless a cache root is supplied), else to nothing — in which case the stages that
/// need it skip. <see cref="Fetch()"/> fills the cache, refusing a payload whose hash is not the
/// declared one. The manifest is the only place a corpus's identity lives: bumping a version there is what changes
/// which bytes every machine, including a build agent, measures against.
/// </summary>
public sealed partial class CorpusManifest {
    private sealed class CorpusDto {
        public string? Archive {
            get;
            init;
        }
        public FileEntry[]? Files {
            get;
            init;
        }
        public string? Hardware {
            get;
            init;
        }
        public string? License {
            get;
            init;
        }
        public required string Name {
            get;
            init;
        }
        public string? Revision {
            get;
            init;
        }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Root {
            get;
            init;
        }
        public string? Sha256 {
            get;
            init;
        }
        public string? UrlPrefix {
            get;
            init;
        }
        public required string Version {
            get;
            init;
        }
    }
    [JsonSerializable(typeof(CorpusDto[]))]
    private sealed partial class CorpusJsonContext : JsonSerializerContext;

    /// <summary>One declared corpus.</summary>
    /// <param name="Name">The corpus's name, which is also its cache directory and its command-line identity.</param>
    /// <param name="Version">The release version or commit the archive is pinned to.</param>
    /// <param name="Archive">The archive's URL, or null for individually pinned files.</param>
    /// <param name="Sha256">The archive's SHA-256, or null for individually pinned files.</param>
    /// <param name="Root">The directory inside the archive that is the corpus root, or <see langword="null"/> for the archive's own top level.</param>
    public sealed record Corpus(string Name, string Version, string? Archive, string? Sha256, string? Root) {
        /// <summary>Gets the URL directory to which each file's relative path is appended.</summary>
        public string? UrlPrefix {
            get;
            init;
        }
        /// <summary>Gets the complete file inventory; null selects the archive form.</summary>
        public IReadOnlyList<FileEntry>? Files {
            get;
            init;
        }
        /// <summary>Gets the upstream licence statement, when supplied.</summary>
        public string? License {
            get;
            init;
        }
        /// <summary>Gets the source revision, when supplied separately from the cache version.</summary>
        public string? Revision {
            get;
            init;
        }
        /// <summary>Gets the hardware applicability statement.</summary>
        public string? Hardware {
            get;
            init;
        }
    }
    /// <summary>One file in a corpus; every cached and newly downloaded file is hash-verified.</summary>
    /// <param name="Path">The slash-separated path relative to the URL prefix and corpus root.</param>
    /// <param name="Sha256">The exact file's SHA-256, as 64 hexadecimal digits.</param>
    public sealed record FileEntry(string Path, string Sha256);

    private readonly IReadOnlyList<Corpus> m_corpora;

    private CorpusManifest(IReadOnlyList<Corpus> corpora, string? cacheRoot) {
        m_corpora = corpora;
        EffectiveCacheRoot = (cacheRoot ?? CacheRoot);
    }

    /// <summary>Gets this manifest's explicitly configured cache root, or the OS default.</summary>
    public string EffectiveCacheRoot {
        get;
    }
    /// <summary>Gets the declared corpora.</summary>
    public IReadOnlyList<Corpus> Corpora =>
        m_corpora;
    /// <summary>Gets the directory the corpora are cached under: the <c>corpora</c> subdirectory of the per-user
    /// Puck directory (<see cref="Puck.Abstractions.PuckUserDirectory"/>).</summary>
    public static string CacheRoot =>
        Puck.Abstractions.PuckUserDirectory.Resolve(name: "corpora");

    /// <summary>Resolves a battery's committed <c>corpora.json</c> in the running checkout, independently of
    /// compiler source-path mapping.</summary>
    /// <param name="projectName">The battery's directory name under <c>src</c>.</param>
    /// <returns>The manifest's path.</returns>
    /// <exception cref="DirectoryNotFoundException">No checkout is found above the executable or working directory.</exception>
    public static string InRepository(string projectName) =>
        RepositoryPaths.Resolve(relativePath: Path.Combine(
            path1: "src",
            path2: projectName,
            path3: "corpora.json"
        ));
    /// <summary>Loads a manifest.</summary>
    /// <param name="path">The manifest file's path.</param>
    /// <param name="cacheRoot">The cache directory, or <see langword="null"/> for the OS local application-data directory.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidDataException">A corpus path, payload form, or SHA-256 pin is invalid.</exception>
    /// <exception cref="JsonException">The file is not a valid corpus manifest document.</exception>
    /// <exception cref="IOException">The manifest file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The manifest file cannot be accessed.</exception>
    public static CorpusManifest Load(string path, string? cacheRoot = null) {
        var dtos = (JsonSerializer.Deserialize(
            json: File.ReadAllText(path: path),
            jsonTypeInfo: CorpusJsonContext.Default.CorpusDtoArray
        ) ?? []);

        return new CorpusManifest(
            corpora: dtos
            .Select(selector: static dto => Validate(corpus: new Corpus(
                Archive: dto.Archive,
                Name: dto.Name,
                Root: dto.Root,
                Sha256: dto.Sha256,
                Version: dto.Version
            ) {
                UrlPrefix = dto.UrlPrefix,
                Files = ((dto.Files is null) ? null : Array.AsReadOnly(array: dto.Files)),
                License = dto.License,
                Revision = dto.Revision,
                Hardware = dto.Hardware,
            }))
            .ToArray(),
            cacheRoot: cacheRoot
        );
    }
    /// <summary>Returns the cached root directory a corpus resolves to, whether or not it exists yet.</summary>
    /// <param name="corpus">The corpus.</param>
    /// <param name="cacheRoot">The cache directory, or <see langword="null"/> for the OS default.</param>
    /// <returns>The directory.</returns>
    public static string CachedRoot(Corpus corpus, string? cacheRoot = null) {
        var version = Path.Combine(
            path1: (cacheRoot ?? CacheRoot),
            path2: corpus.Name,
            path3: corpus.Version
        );

        return ((corpus.Root is null)
            ? version
            : Path.Combine(
                path1: version,
                path2: corpus.Root
            )
        );
    }
    /// <summary>Downloads and unpacks every corpus not already in the cache.</summary>
    /// <returns>The number of corpora fetched.</returns>
    /// <exception cref="InvalidDataException">Downloaded or cached bytes disagree with a declared hash, or an archive is invalid.</exception>
    /// <exception cref="HttpRequestException">A corpus payload cannot be downloaded.</exception>
    /// <exception cref="OperationCanceledException">A download times out.</exception>
    /// <exception cref="IOException">A cache file or directory cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">A cache file or directory cannot be accessed.</exception>
    public int Fetch() {
        using var client = new HttpClient();

        return Fetch(client: client);
    }
    /// <summary>Fetches the manifest using a caller-owned transport, verifying every declared file before publication.</summary>
    /// <param name="client">The HTTP transport; the caller retains ownership.</param>
    /// <returns>The number of corpora with newly fetched content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="client"/> is disposed.</exception>
    /// <exception cref="InvalidDataException">Downloaded or cached bytes disagree with a declared hash, or an archive is invalid.</exception>
    /// <exception cref="HttpRequestException">A corpus payload cannot be downloaded.</exception>
    /// <exception cref="OperationCanceledException">A download times out or is canceled.</exception>
    /// <exception cref="IOException">A cache file or directory cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">A cache file or directory cannot be accessed.</exception>
    public int Fetch(HttpClient client) {
        ArgumentNullException.ThrowIfNull(argument: client);
        var fetched = 0;

        foreach (var corpus in m_corpora) {
            var root = CachedRoot(
                corpus: corpus,
                cacheRoot: EffectiveCacheRoot
            );

            if (corpus.Files is not null) {
                if (FetchFiles(client: client, corpus: corpus, root: root)) {
                    ++fetched;
                }
                continue;
            }

            if (Directory.Exists(path: root)) {
                Console.Out.WriteLine(value: $"{corpus.Name} {corpus.Version}: cached at {root}");

                continue;
            }

            Console.Out.WriteLine(value: $"{corpus.Name} {corpus.Version}: fetching {corpus.Archive}");

            var bytes = client.GetByteArrayAsync(requestUri: corpus.Archive).GetAwaiter().GetResult();
            var actual = Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes));

            if (!string.Equals(
                a: actual,
                b: corpus.Sha256,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                throw new InvalidDataException(message: $"{corpus.Name} {corpus.Version}: archive SHA-256 is {actual}, manifest declares {corpus.Sha256}; nothing unpacked.");
            }

            var versionDirectory = Path.Combine(
                path1: EffectiveCacheRoot,
                path2: corpus.Name,
                path3: corpus.Version
            );
            var staging = (versionDirectory + ".partial");

            if (Directory.Exists(path: staging)) {
                Directory.Delete(
                    path: staging,
                    recursive: true
                );
            }

            using (var archive = new ZipArchive(
                mode: ZipArchiveMode.Read,
                stream: new MemoryStream(buffer: bytes)
            )) {
                archive.ExtractToDirectory(destinationDirectoryName: staging);
            }

            Directory.Move(
                destDirName: versionDirectory,
                sourceDirName: staging
            );
            Console.Out.WriteLine(value: $"{corpus.Name} {corpus.Version}: unpacked to {root}");
            ++fetched;
        }

        return fetched;
    }
    /// <summary>Resolves a corpus root: the command-line flag's value wins, else the cache when the corpus is there,
    /// else <see langword="null"/>.</summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <param name="flag">The flag naming the root explicitly.</param>
    /// <param name="name">The corpus's manifest name.</param>
    /// <returns>The root, or <see langword="null"/> when the corpus is unavailable.</returns>
    /// <exception cref="ArgumentException">The manifest declares no corpus named <paramref name="name"/>.</exception>
    /// <exception cref="InvalidDataException">An individually pinned file has the wrong hash, or an explicit root is incomplete.</exception>
    /// <exception cref="IOException">An individually pinned file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">An individually pinned file cannot be accessed.</exception>
    public string? Resolve(string[] args, string flag, string name) {
        var explicitRoot = CommandLineArguments.Value(
            args: args,
            name: flag
        );

        var corpus = (m_corpora.FirstOrDefault(predicate: corpus => string.Equals(
            a: corpus.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        )) ?? throw new ArgumentException(message: $"corpora.json declares no corpus named '{name}'."));

        if (!string.IsNullOrEmpty(value: explicitRoot)) {
            if ((corpus.Files is not null) && !VerifyFiles(corpus: corpus, root: explicitRoot)) {
                throw new InvalidDataException(message: $"{corpus.Name}: explicit corpus root '{explicitRoot}' is missing declared files.");
            }
            return explicitRoot;
        }
        var root = CachedRoot(
            corpus: corpus,
            cacheRoot: EffectiveCacheRoot
        );

        if (corpus.Files is not null) {
            return (VerifyFiles(corpus: corpus, root: root) ? root : null);
        }

        return (Directory.Exists(path: root)
            ? root
            : null
        );
    }
}
