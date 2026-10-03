using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.Machines.Post;
using Puck.Testing;

namespace Puck.Machines.Tests;

/// <summary>Verifies archive and per-file corpus fetching, cache reuse, and hash refusal.</summary>
public sealed class CorpusManifestTests {
    /// <summary>Checks nested cache paths, verified reuse, and fetching only missing files.</summary>
    [Fact]
    public void FileCorpusFetchesVerifiesAndResumesWithTheSameLayout() {
        using var fixture = new Fixture();
        var first = "first file"u8.ToArray();
        var second = "second file"u8.ToArray();
        var manifest = fixture.Files(("00.json", first), ("nested/ff.json", second));
        using var handler = new MemoryHandler(responses: new Dictionary<string, byte[]> {
            ["/base/00.json"] = first,
            ["/base/nested/ff.json"] = second,
        });
        using var client = new HttpClient(handler: handler);

        Assert.Null(@object: manifest.Resolve(args: [], flag: "--vectors", name: "vectors"));
        Assert.Equal(expected: 1, actual: manifest.Fetch(client: client));
        Assert.Equal(expected: 2, actual: handler.Requests);
        var root = manifest.Resolve(args: [], flag: "--vectors", name: "vectors");

        Assert.Equal(expected: Path.Combine(path1: fixture.Cache, path2: "vectors", path3: "v1"), actual: root);
        Assert.Equal(expected: second, actual: File.ReadAllBytes(path: Path.Combine(path1: root!, path2: "nested/ff.json")));
        Assert.Equal(expected: 0, actual: manifest.Fetch(client: client));
        Assert.Equal(expected: 2, actual: handler.Requests);
        File.Delete(path: Path.Combine(path1: root!, path2: "00.json"));
        Assert.Null(@object: manifest.Resolve(args: [], flag: "--vectors", name: "vectors"));
        Assert.Equal(expected: 1, actual: manifest.Fetch(client: client));
        Assert.Equal(expected: 3, actual: handler.Requests);
    }
    /// <summary>Preserves a declared file whose name also looks like another file's download staging path.</summary>
    [Fact]
    public void FileDownloadsCannotOverwriteAnotherDeclaredFile() {
        using var fixture = new Fixture();
        var first = "complete partial-named file"u8.ToArray();
        var second = "complete base file"u8.ToArray();
        var manifest = fixture.Files(("a.partial", first), ("a", second));
        using var handler = new MemoryHandler(responses: new Dictionary<string, byte[]> {
            ["/base/a.partial"] = first,
            ["/base/a"] = second,
        });
        using var client = new HttpClient(handler: handler);

        Assert.Equal(expected: 1, actual: manifest.Fetch(client: client));
        var root = manifest.Resolve(args: [], flag: "--vectors", name: "vectors");

        Assert.NotNull(@object: root);
        Assert.Equal(expected: first, actual: File.ReadAllBytes(path: Path.Combine(path1: root, path2: "a.partial")));
        Assert.Equal(expected: second, actual: File.ReadAllBytes(path: Path.Combine(path1: root, path2: "a")));
        Assert.Equal(expected: 2, actual: Directory.GetFiles(path: root).Length);
    }
    /// <summary>Checks that a failed file hash leaves neither a published file nor a staging file.</summary>
    [Fact]
    public void ANewBadHashIsRefusedAndNeverPublished() {
        using var fixture = new Fixture();
        var manifest = fixture.Files(("00.json", "expected"u8.ToArray()));
        using var handler = new MemoryHandler(responses: new Dictionary<string, byte[]> { ["/base/00.json"] = "damaged"u8.ToArray() });
        using var client = new HttpClient(handler: handler);
        var failure = Assert.Throws<InvalidDataException>(testCode: () => manifest.Fetch(client: client));

        Assert.Contains(expectedSubstring: "00.json SHA-256", actualString: failure.Message);
        Assert.False(condition: File.Exists(path: Path.Combine(path1: fixture.Cache, path2: "vectors/v1/00.json")));
        Assert.Empty(collection: Directory.GetFiles(path: Path.Combine(path1: fixture.Cache, path2: "vectors/v1")));
        Assert.Null(@object: manifest.Resolve(args: [], flag: "--vectors", name: "vectors"));
    }
    /// <summary>Checks that both cache consumers reject corruption without fetching replacement bytes.</summary>
    [Fact]
    public void CachedCorruptionIsRefusedDuringBothFetchAndResolve() {
        using var fixture = new Fixture();
        var bytes = "correct"u8.ToArray();
        var manifest = fixture.Files(("00.json", bytes));
        using var handler = new MemoryHandler(responses: new Dictionary<string, byte[]> { ["/base/00.json"] = bytes });
        using var client = new HttpClient(handler: handler);

        _ = manifest.Fetch(client: client);
        File.WriteAllText(path: Path.Combine(path1: fixture.Cache, path2: "vectors/v1/00.json"), contents: "wrong");
        Assert.Throws<InvalidDataException>(testCode: () => manifest.Fetch(client: client));
        Assert.Throws<InvalidDataException>(testCode: () => manifest.Resolve(args: [], flag: "--vectors", name: "vectors"));
        Assert.Equal(expected: 1, actual: handler.Requests);
    }
    /// <summary>Rejects paths that can escape or ambiguously name a cache entry.</summary>
    /// <param name="path">The invalid relative path declared by the manifest.</param>
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("a/%2e%2e/b")]
    [Theory]
    public void FilePathsCannotEscapeTheCorpus(string path) {
        using var fixture = new Fixture();

        Assert.Throws<InvalidDataException>(testCode: () => fixture.Files((path, new byte[] { 1 })));
    }
    /// <summary>Checks archive verification, extraction, root resolution, and cache reuse.</summary>
    [Fact]
    public void ArchivesStillVerifyAndUnpackUnderTheirDeclaredRoot() {
        using var fixture = new Fixture();
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(leaveOpen: true, mode: ZipArchiveMode.Create, stream: buffer)) {
            using var entry = archive.CreateEntry(entryName: "release/data.txt").Open();

            entry.Write(buffer: "archive payload"u8);
        }
        var bytes = buffer.ToArray();
        var manifest = fixture.Archive(hash: Hash(bytes: bytes));
        using var handler = new MemoryHandler(responses: new Dictionary<string, byte[]> { ["/archive.zip"] = bytes });
        using var client = new HttpClient(handler: handler);

        Assert.Equal(expected: 1, actual: manifest.Fetch(client: client));
        var root = manifest.Resolve(args: [], flag: "--vectors", name: "vectors");

        Assert.Equal(expected: "archive payload", actual: File.ReadAllText(path: Path.Combine(path1: root!, path2: "data.txt")));
        Assert.Equal(expected: 0, actual: manifest.Fetch(client: client));
    }
    /// <summary>Checks that a failed archive hash never publishes an extracted cache directory.</summary>
    [Fact]
    public void AnArchiveWithABadHashIsStillRefused() {
        using var fixture = new Fixture();
        var manifest = fixture.Archive(hash: new string(c: '0', count: 64));
        using var handler = new MemoryHandler(responses: new Dictionary<string, byte[]> { ["/archive.zip"] = "wrong"u8.ToArray() });
        using var client = new HttpClient(handler: handler);

        Assert.Throws<InvalidDataException>(testCode: () => manifest.Fetch(client: client));
        Assert.False(condition: Directory.Exists(path: Path.Combine(path1: fixture.Cache, path2: "vectors/v1")));
    }

    private static string Hash(byte[] bytes) {
        return Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes));
    }

    /// <summary>Owns a temporary manifest and cache tree for one corpus test.</summary>
    private sealed class Fixture : IDisposable {
        private readonly TemporaryDirectory m_directory = new(prefix: "puck-corpus-tests-");

        /// <summary>Gets the isolated cache root.</summary>
        public string Cache => m_directory.PathOf(name: "corpora");

        /// <summary>Writes and loads a manifest with individually hashed files.</summary>
        /// <param name="files">The relative paths and expected payload bytes.</param>
        /// <returns>The loaded manifest over the isolated cache.</returns>
        /// <exception cref="InvalidDataException">A relative path is invalid.</exception>
        public CorpusManifest Files(params (string Path, byte[] Bytes)[] files) {
            return Write(value: new[] { new {
                Name = "vectors",
                Version = "v1",
                UrlPrefix = "https://corpus.invalid/base/",
                Files = files.Select(selector: file => new { file.Path, Sha256 = Hash(bytes: file.Bytes) }).ToArray(),
            } });
        }
        /// <summary>Writes and loads a manifest for one archive.</summary>
        /// <param name="hash">The expected archive SHA-256 in hexadecimal.</param>
        /// <returns>The loaded manifest over the isolated cache.</returns>
        /// <exception cref="InvalidDataException">The hash is not a 64-digit hexadecimal value.</exception>
        public CorpusManifest Archive(string hash) {
            return Write(value: new[] { new {
                Name = "vectors",
                Version = "v1",
                Archive = "https://corpus.invalid/archive.zip",
                Sha256 = hash,
                Root = "release",
            } });
        }

        private CorpusManifest Write<T>(T value) {
            var path = m_directory.PathOf(name: "manifest.json");

            File.WriteAllText(path: path, contents: JsonSerializer.Serialize(value: value));

            return CorpusManifest.Load(path: path, cacheRoot: Cache);
        }

        /// <inheritdoc/>
        public void Dispose() => m_directory.Dispose();
    }
    /// <summary>Serves declared corpus payloads without network access and counts fetch requests.</summary>
    private sealed class MemoryHandler : HttpMessageHandler {
        private readonly Dictionary<string, byte[]> m_responses;

        /// <summary>Initializes a new instance of the <see cref="MemoryHandler"/> class.</summary>
        /// <param name="responses">The URL paths and response bytes.</param>
        public MemoryHandler(Dictionary<string, byte[]> responses) {
            m_responses = responses;
        }

        /// <summary>Gets the number of requests served.</summary>
        public int Requests {
            get;
            private set;
        }

        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            ++Requests;

            return Task.FromResult(result: new HttpResponseMessage(statusCode: HttpStatusCode.OK) {
                Content = new ByteArrayContent(content: m_responses[request.RequestUri!.AbsolutePath]),
            });
        }
    }
}
