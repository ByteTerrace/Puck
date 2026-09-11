using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    // KEEP IN SYNC with the `SpaCacheHashed` rule in src/Puck.Azure.Resources/main.bicep.
    private static readonly string[] HashedWebsiteDirectories = ["assets", "portal/assets"];
    private static readonly string[] WebsiteEntrypoints = ["host-entry.js", "sw.js", "portal/portal-entry.js", "portal/mf-manifest.json"];

    private static async Task PublishStaticAsync(string bundle) {
        var release = CliFiles.ReadJson(path: $"{bundle}/release.json");

        if (((string?)release["channel"]) != "stable") { throw new InvalidDataException(message: "Production requires the stable content channel."); }
        var site = LocalPath(path: $"{bundle}/dashboard-storage");
        var missing = WebsiteEntrypoints.Append(element: "index.html").Where(predicate: name => !File.Exists(path: $"{site}/{name}"))
            .Concat(second: HashedWebsiteDirectories.Where(predicate: name => !Directory.Exists(path: $"{site}/{name}"))).ToArray();

        if (missing.Length != 0) { throw new InvalidDataException(message: $"The staged website is missing {string.Join(separator: ", ", values: missing)}."); }
        var outputs = Outputs();
        var container = Text(value: Value(key: "officialContentContainerName", outputs: outputs));

        if (!Regex.IsMatch(input: container, pattern: "\\A[a-f0-9-]{36}\\z")) { throw new InvalidDataException(message: "Missing official-content container."); }
        var staticSite = new Uri(uriString: Text(value: Value(key: "staticSiteEndpoint", outputs: outputs)));
        var endpoint = staticSite.GetLeftPart(part: UriPartial.Authority);
        var website = $"{endpoint}/{((staticSite.AbsolutePath.Split(separator: '/') is [_, { Length: > 0 } name, ..]) ? name : throw new InvalidDataException(message: "Missing static-site container."))}";
        var address = WebsiteAddress(outputs: outputs);
        var marker = await ReleaseMarkerAsync(uri: (address + "/release.json"));
        var previousMarker = await ReleaseMarkerAsync(uri: (address + "/release-previous.json"));
        var retained = HashedWebsiteFiles(marker: marker).Concat(second: HashedWebsiteFiles(marker: previousMarker)).Distinct(comparer: StringComparer.Ordinal).ToArray();
        var manifest = CliFiles.ReadJson(path: $"{bundle}/official/stable/manifest.json");
        var types = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var objects = new[] { manifest["worldSchemaBundle"] }.Concat(second: manifest["documents"]!.AsArray()).Concat(second: manifest["composed"]!.AsArray()).Concat(second: manifest["assets"]!.AsArray()).Concat(second: manifest["engine"]!["files"]!.AsArray());

        foreach (var item in objects) {
            var path = Text(value: item!["path"]);
            var type = Text(value: item["contentType"]);

            if (!Regex.IsMatch(input: path, pattern: "\\Aobjects/sha256/[a-f0-9]{2}/[a-f0-9]{64}\\z")) { throw new InvalidDataException(message: "Invalid official object path."); }
            if (types.TryGetValue(key: path, value: out var previousType) && (previousType != type)) { throw new InvalidDataException(message: "Conflicting official media types."); }
            types[path] = type;
        }
        var commit = Text(value: release["commit"]);
        var official = $"{endpoint}/{container}/public/puck/official";
        var staging = Directory.CreateTempSubdirectory(prefix: "puck-official-");

        try {
            // A copy shares one media type, and hash paths carry none, so each type is staged as its own tree.
            foreach (var (group, index) in types.GroupBy(keySelector: item => item.Value).Select(selector: (group, index) => (group, index))) {
                var tree = $"{LocalPath(path: staging.FullName)}/{index}";

                foreach (var path in group.Select(selector: item => item.Key)) {
                    var target = $"{tree}/{path}";

                    Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
                    File.Copy(destFileName: target, sourceFileName: $"{bundle}/official/{path}");
                }
                await CopyBlobsAsync(commit: commit, contentType: group.Key, destination: official, immutable: true, source: tree);
            }
        } finally { staging.Delete(recursive: true); }
        await CopyBlobsAsync(commit: commit, contentType: "application/json", destination: $"{official}/stable/manifest.json", source: $"{bundle}/official/stable/manifest.json");
        // Front Door's `portal` rule set owns the website's caching and encoding headers
        // (src/Puck.Azure.Resources/main.bicep); sync only carries bytes and inferred media types,
        // and a hashed blob must not carry a non-cacheable Cache-Control or the edge override is skipped.
        // Hash-named directories land first, the whole tree is mirrored so the new shell is live,
        // and only then are retired files deleted. The hashed files of the release being replaced
        // survive one more release: a session that loaded them, workers included, keeps working,
        // and an older session recovers through the shell's reload on a failed chunk load. The
        // replaced marker is kept as release-previous.json, unchanged by a rerun of the same
        // release, so the protected set is always the last distinct release.
        foreach (var directory in HashedWebsiteDirectories) {
            await SyncBlobsAsync(delete: false, destination: $"{website}/{directory}", source: $"{site}/{directory}");
        }
        await SyncBlobsAsync(delete: false, destination: website, source: site);
        await SyncBlobsAsync(delete: true, destination: website, exclude: ["release.json", "release-previous.json", .. retained], source: site);
        if ((marker is not null) && (Text(value: marker["commit"]) != commit)) {
            var replaced = Path.GetTempFileName();

            try {
                File.WriteAllText(contents: marker.ToJsonString(), path: replaced);
                await CopyBlobsAsync(cache: "no-store", commit: commit, contentType: "application/json", destination: $"{website}/release-previous.json", source: replaced);
            } finally { File.Delete(path: replaced); }
        }
        await CopyBlobsAsync(cache: "no-store", commit: commit, contentType: "application/json", destination: $"{website}/release.json", source: $"{bundle}/release.json");
    }
    private static async Task<JsonNode?> ReleaseMarkerAsync(string uri) {
        using var response = await Http.GetAsync(requestUri: uri);

        if (response.StatusCode == HttpStatusCode.NotFound) { return null; }
        response.EnsureSuccessStatusCode();
        return (JsonNode.Parse(json: await response.Content.ReadAsStringAsync()) ?? throw new InvalidDataException(message: $"Empty release marker at {uri}."));
    }
    private static IEnumerable<string> HashedWebsiteFiles(JsonNode? marker) {
        const string Prefix = "dashboard-storage/";

        return (marker?["files"]?.AsArray() ?? [])
            .Select(selector: file => Text(value: file!["path"]))
            .Where(predicate: path => path.StartsWith(comparisonType: StringComparison.Ordinal, value: Prefix))
            .Select(selector: path => path[Prefix.Length..])
            .Where(predicate: path => HashedWebsiteDirectories.Any(predicate: directory => path.StartsWith(comparisonType: StringComparison.Ordinal, value: (directory + "/"))));
    }
    private static async Task SyncBlobsAsync(bool delete, string destination, string source, IEnumerable<string>? exclude = null) {
        var hashes = Directory.CreateTempSubdirectory(prefix: "puck-azcopy-hashes-");

        try {
            // MD5 comparison: artifact extraction timestamps identify neither a release nor a rollback.
            var arguments = new List<string> {
                "sync", LocalPath(path: source), destination, "--from-to=LocalBlob", "--recursive=true", "--compare-hash=MD5", "--put-md5",
                $"--delete-destination={(delete ? "true" : "false")}", "--local-hash-storage-mode=HiddenFiles",
                $"--hash-meta-dir={hashes.FullName}", "--log-level=ERROR", "--output-level=essential",
            };

            // Excluded paths are neither uploaded nor deleted; the match is a relative-path prefix.
            if (exclude is not null) { arguments.Add(item: $"--exclude-path={string.Join(separator: ';', values: exclude)}"); }
            await RunAsync(arguments: arguments, executable: "azcopy");
        } finally { hashes.Delete(recursive: true); }
    }
    private static Task CopyBlobsAsync(string commit, string contentType, string destination, string source, string cache = "no-cache", bool immutable = false) =>
        // AzCopy owns concurrency, retries and transfer validation. Mutable paths always
        // overwrite: extraction timestamps do not identify releases or safe rollbacks.
        RunAsync(arguments: [
            "copy", LocalPath(path: source), destination, "--from-to=LocalBlob", "--as-subdir=false", "--recursive=true",
            $"--overwrite={(immutable ? "false" : "true")}", $"--content-type={contentType}",
            $"--cache-control={(immutable ? "public,max-age=31536000,immutable" : cache)}",
            $"--metadata=commit={commit}", "--log-level=ERROR", "--output-level=essential",
        ], executable: "azcopy");
    // AzCopy refuses a local path that mixes separators.
    private static string LocalPath(string path) => Path.GetFullPath(path: path).Replace(newChar: '/', oldChar: '\\');
    private static string WebsiteAddress(JsonNode outputs) => Regex.Replace(input: Text(value: Value(key: "officialContentBaseUrl", outputs: outputs)), pattern: "/official/?$", replacement: "");
}
