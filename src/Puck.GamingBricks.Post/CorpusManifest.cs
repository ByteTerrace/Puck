using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.GamingBricks.Post;

/// <summary>
/// The external corpora a battery reads, declared in a <c>corpora.json</c> beside its program: each names a release
/// archive by URL, version, and SHA-256, and the directory inside the archive that is the corpus root. A corpus
/// resolves, in order, to an explicit command-line root, else to its entry in the local cache
/// (under the OS local application-data directory unless a cache root is supplied), else to nothing — in which case the stages that
/// need it skip. <see cref="Fetch"/> fills the cache from the archive, refusing an archive whose hash is not the
/// declared one. The manifest is the only place a corpus's identity lives: bumping a version there is what changes
/// which bytes every machine, including a build agent, measures against.
/// </summary>
public sealed partial class CorpusManifest {
    private sealed class CorpusDto {
        public required string Name { get; init; }
        public required string Version { get; init; }
        public required string Archive { get; init; }
        public required string Sha256 { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Root { get; init; }
    }

    [JsonSerializable(typeof(CorpusDto[]))]
    private sealed partial class CorpusJsonContext : JsonSerializerContext;

    /// <summary>One declared corpus.</summary>
    /// <param name="Name">The corpus's name, which is also its cache directory and its command-line identity.</param>
    /// <param name="Version">The release version or commit the archive is pinned to.</param>
    /// <param name="Archive">The archive's URL.</param>
    /// <param name="Sha256">The archive's SHA-256, lowercase hexadecimal.</param>
    /// <param name="Root">The directory inside the archive that is the corpus root, or <see langword="null"/> for the archive's own top level.</param>
    public sealed record Corpus(string Name, string Version, string Archive, string Sha256, string? Root);

    private readonly IReadOnlyList<Corpus> m_corpora;

    private CorpusManifest(IReadOnlyList<Corpus> corpora, string? cacheRoot) {
        m_corpora = corpora;
        EffectiveCacheRoot = cacheRoot ?? CacheRoot;
    }

    /// <summary>Gets this manifest's explicitly configured cache root, or the OS default.</summary>
    public string EffectiveCacheRoot { get; }

    /// <summary>Gets the declared corpora.</summary>
    public IReadOnlyList<Corpus> Corpora =>
        m_corpora;

    /// <summary>Gets the directory the corpora are cached under.</summary>
    public static string CacheRoot =>
        Path.Combine(
        path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData),
        path2: "Puck",
        path3: "corpora"
    );

    /// <summary>Resolves a battery's committed <c>corpora.json</c> in the running checkout, independently of
    /// compiler source-path mapping.</summary>
    /// <param name="projectName">The battery's directory name under <c>src</c>.</param>
    /// <returns>The manifest's path.</returns>
    /// <exception cref="DirectoryNotFoundException">No checkout is found above the executable or working directory.</exception>
    public static string InRepository(string projectName) =>
        RepositoryPaths.Resolve(relativePath: Path.Combine(path1: "src", path2: projectName, path3: "corpora.json"));
    /// <summary>Loads a manifest.</summary>
    /// <param name="path">The manifest file's path.</param>
    /// <param name="cacheRoot">Cache directory; null uses the OS local application-data directory.</param>
    /// <returns>The manifest.</returns>
    public static CorpusManifest Load(string path, string? cacheRoot = null) {
        var dtos = (JsonSerializer.Deserialize(
            json: File.ReadAllText(path: path),
            jsonTypeInfo: CorpusJsonContext.Default.CorpusDtoArray
        ) ?? []);

        return new CorpusManifest(corpora: dtos
            .Select(selector: static dto => new Corpus(
                Archive: dto.Archive,
                Name: dto.Name,
                Root: dto.Root,
                Sha256: dto.Sha256,
                Version: dto.Version
            ))
            .ToArray(), cacheRoot: cacheRoot);
    }

    /// <summary>Returns the cached root directory a corpus resolves to, whether or not it exists yet.</summary>
    /// <param name="corpus">The corpus.</param>
    /// <param name="cacheRoot">Cache directory; null uses the OS default.</param>
    /// <returns>The directory.</returns>
    public static string CachedRoot(Corpus corpus, string? cacheRoot = null) {
        var version = Path.Combine(
            path1: cacheRoot ?? CacheRoot,
            path2: corpus.Name,
            path3: corpus.Version
        );

        return ((corpus.Root is null)
            ? version
            : Path.Combine(
                path1: version,
                path2: corpus.Root
            ));
    }
    /// <summary>Downloads and unpacks every corpus not already in the cache.</summary>
    /// <returns>The number of corpora fetched.</returns>
    /// <exception cref="InvalidDataException">An archive's bytes do not hash to the declared SHA-256.</exception>
    public int Fetch() {
        var fetched = 0;

        using var client = new HttpClient();

        foreach (var corpus in m_corpora) {
            var root = CachedRoot(corpus: corpus, cacheRoot: EffectiveCacheRoot);

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
    public string? Resolve(string[] args, string flag, string name) {
        var explicitRoot = CommandLineArguments.Value(
            args: args,
            name: flag
        );

        if (!string.IsNullOrEmpty(value: explicitRoot)) {
            return explicitRoot;
        }

        var corpus = (m_corpora.FirstOrDefault(predicate: corpus => string.Equals(
            a: corpus.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        )) ?? throw new ArgumentException(message: $"corpora.json declares no corpus named '{name}'."));
        var root = CachedRoot(corpus: corpus, cacheRoot: EffectiveCacheRoot);

        return (Directory.Exists(path: root)
            ? root
            : null);
    }
}
