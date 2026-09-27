using System.Security.Cryptography;

namespace Puck.Machines.Post;

public sealed partial class CorpusManifest {
    private static Corpus Validate(Corpus corpus) {
        ValidateRelativePath(path: corpus.Name, oneSegment: true);
        ValidateRelativePath(path: corpus.Version, oneSegment: true);
        if (corpus.Root is not null) {
            ValidateRelativePath(path: corpus.Root);
        }
        if (corpus.Files is null) {
            if ((corpus.UrlPrefix is not null) || !Uri.TryCreate(uriString: corpus.Archive, uriKind: UriKind.Absolute, result: out _)) {
                throw new InvalidDataException(message: $"{corpus.Name}: declare an archive or a URL prefix with files, exclusively.");
            }
            ValidateHash(hash: corpus.Sha256);

            return corpus;
        }
        if ((corpus.Archive is not null) || (corpus.Sha256 is not null) || (corpus.Files.Count == 0) ||
            !Uri.TryCreate(uriString: corpus.UrlPrefix, uriKind: UriKind.Absolute, result: out var prefix) ||
            (prefix.Scheme is not ("https" or "http")) || !prefix.AbsolutePath.EndsWith(value: '/')) {
            throw new InvalidDataException(message: $"{corpus.Name}: a file corpus requires an HTTP(S) URL prefix ending in '/', a nonempty file inventory, and no archive fields.");
        }
        var paths = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var file in corpus.Files) {
            ValidateRelativePath(path: file.Path);
            ValidateHash(hash: file.Sha256);
            if (!paths.Add(item: file.Path)) {
                throw new InvalidDataException(message: $"{corpus.Name}: duplicate corpus path '{file.Path}'.");
            }
        }

        return corpus;
    }
    private static void ValidateHash(string? hash) {
        if ((hash is null) || (hash.Length != 64) || hash.Any(predicate: static character => !char.IsAsciiHexDigit(c: character))) {
            throw new InvalidDataException(message: "Every corpus payload requires a computed 64-digit SHA-256 pin.");
        }
    }
    private static void ValidateRelativePath(string path, bool oneSegment = false) {
        if (string.IsNullOrWhiteSpace(value: path) || Path.IsPathRooted(path: path) ||
            (path.IndexOfAny(anyOf: ['\\', ':', '?', '#', '%', '\0']) >= 0) || (oneSegment && path.Contains(value: '/')) ||
            path.Split(separator: '/').Any(predicate: static part => ((part is "" or "." or "..") || part.EndsWith(value: '.') || part.EndsWith(value: ' ')))) {
            throw new InvalidDataException(message: $"Invalid corpus relative path '{path}'.");
        }
    }
    private static void VerifyFile(Corpus corpus, FileEntry file, string path) {
        using var stream = File.OpenRead(path: path);
        var actual = Convert.ToHexStringLower(inArray: SHA256.HashData(source: stream));

        if (!string.Equals(a: actual, b: file.Sha256, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException(message: $"{corpus.Name} {corpus.Version}: {file.Path} SHA-256 is {actual}, manifest declares {file.Sha256}; file refused.");
        }
    }
    private static bool VerifyFiles(Corpus corpus, string root) {
        var complete = true;

        foreach (var file in corpus.Files!) {
            var path = Path.Combine(path1: root, path2: file.Path);

            if (!File.Exists(path: path)) {
                complete = false;
                continue;
            }
            VerifyFile(corpus: corpus, file: file, path: path);
        }

        return complete;
    }
    private static bool FetchFiles(Corpus corpus, string root, HttpClient client) {
        var fetched = false;

        foreach (var file in corpus.Files!) {
            var path = Path.Combine(path1: root, path2: file.Path);

            if (File.Exists(path: path)) {
                VerifyFile(corpus: corpus, file: file, path: path);
                continue;
            }
            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
            var staging = (path + ".partial");

            Console.Out.WriteLine(value: $"{corpus.Name} {corpus.Version}: fetching {file.Path}");
            try {
                using (var source = client.GetStreamAsync(requestUri: new Uri(baseUri: new Uri(uriString: corpus.UrlPrefix!), relativeUri: file.Path)).GetAwaiter().GetResult()) {
                    using var target = File.Create(path: staging);

                    source.CopyTo(destination: target);
                }
                VerifyFile(corpus: corpus, file: file, path: staging);
                File.Move(destFileName: path, sourceFileName: staging);
            } finally {
                if (File.Exists(path: staging)) {
                    File.Delete(path: staging);
                }
            }
            fetched = true;
        }
        Console.Out.WriteLine(value: $"{corpus.Name} {corpus.Version}: all {corpus.Files!.Count} files verified at {root}");

        return fetched;
    }
}
