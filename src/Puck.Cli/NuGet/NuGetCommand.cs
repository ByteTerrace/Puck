using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Puck.Cli.NuGet;

internal static class NuGetCommand {
    private static readonly StringComparer PackageComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] arguments) {
        try {
            return await ExecuteAsync(arguments: arguments);
        } catch (Exception error) {
            Console.Error.WriteLine(value: $"nuget: {error.Message}");
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(string[] arguments) {
        if ((arguments.Length == 0) || (arguments[0] is "help" or "--help" or "-h")) {
            Console.WriteLine(value: """
                puck nuget <command> [arguments]
                  version                           Read shared version; set GITHUB_OUTPUT when present
                  pack [output-directory] [--no-build] Pack and validate every opted-in project
                  prepare <input> <output> <ids>     Select all or comma-separated full package IDs
                  verify <directory>                Verify manifest, source, and artifact checksums
                  push <directory>                  Verify and upload using NUGET_API_KEY
                prepare/verify/push use VERSION (or the shared version) and GITHUB_SHA (or git HEAD).
                """);
            return ((arguments.Length == 0) ? 1 : 0);
        }
        var root = (Puck.RepositoryPaths.FindRoot() ?? throw new DirectoryNotFoundException(message: "Run puck nuget from within the Puck checkout."));
        var command = arguments[0];
        var count = arguments.Length;

        switch (command) {
            case "version" when (count == 1):
                var version = ReadVersion(root: root);
                Console.WriteLine(value: version);
                if (Environment.GetEnvironmentVariable(variable: "GITHUB_OUTPUT") is { Length: > 0 } githubOutput) {
                    File.AppendAllText(contents: $"version={version}\n", path: githubOutput);
                }
                break;
            case "pack" when (count is >= 1 and <= 3):
                var packArguments = arguments.Skip(1).ToArray();
                var noBuild = packArguments.Contains("--no-build", StringComparer.Ordinal);
                var directories = packArguments.Where(argument => argument != "--no-build").ToArray();
                if (directories.Length > 1 || packArguments.Count(argument => argument == "--no-build") > 1 || directories.Any(argument => argument.StartsWith('-'))) {
                    throw new ArgumentException("Expected pack [output-directory] [--no-build].");
                }
                await PackAsync(root, Path.GetFullPath(directories.SingleOrDefault() ?? "artifacts/packages"), noBuild);
                break;
            case "prepare" when (count == 4):
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds: 60) }) {
                    var manifest = await PrepareAsync(arguments[1], arguments[2], arguments[3],
                        ExpectedVersion(root: root), await ExpectedCommitAsync(root: root), id => PublishedVersionsAsync(http: http, id: id));
                    var summary = Summary(manifest: manifest);

                    Console.WriteLine(value: summary);
                    if (Environment.GetEnvironmentVariable(variable: "GITHUB_STEP_SUMMARY") is { Length: > 0 } summaryPath) {
                        File.AppendAllText(contents: summary, path: summaryPath);
                    }
                }
                break;
            case "verify" when (count == 2):
                Verify(arguments[1], ExpectedVersion(root: root), await ExpectedCommitAsync(root: root));
                Console.WriteLine(value: "Release manifest and artifact checksums verified.");
                break;
            case "push" when (count == 2):
                var release = Verify(arguments[1], ExpectedVersion(root: root), await ExpectedCommitAsync(root: root));
                if (string.IsNullOrEmpty(value: Environment.GetEnvironmentVariable(variable: "NUGET_API_KEY"))) {
                    throw new InvalidOperationException(message: "NUGET_API_KEY is required for publishing.");
                }
                // The SDK reads NUGET_API_KEY from the environment. Never log it
                // or interpolate shell commands containing package input.
                foreach (var push in PushArguments(release, arguments[1])) {
                    await CliProcess.RunCheckedAsync(root, "dotnet", push);
                }
                break;
            default:
                throw new ArgumentException(message: "Unknown command or wrong argument count. Use help.");
        }
        return 0;
    }
    private static string ExpectedVersion(string root) {
        var version = ReadVersion(root: root);

        if ((Environment.GetEnvironmentVariable(variable: "VERSION") is { Length: > 0 } expected) && (expected != version)) {
            throw new InvalidDataException(message: "VERSION differs from the shared source version.");
        }
        return version;
    }
    private static async Task<string> ExpectedCommitAsync(string root) {
        return ((Environment.GetEnvironmentVariable(variable: "GITHUB_SHA") is { Length: > 0 } commit)
            ? commit : (await CliProcess.RunCheckedAsync(root, "git", ["rev-parse", "HEAD"], capture: true)).Trim());
    }
    private static string ReadVersion(string root) {
        var nodes = (XDocument.Load(uri: Path.Combine(path1: root, path2: "build", path3: "Packaging.targets"))
            .Element(name: "Project")?.Elements(name: "PropertyGroup").Elements(name: "Version").ToArray() ?? []);

        if (nodes.Length != 1) { throw new InvalidDataException(message: "Expected one shared Version in build/Packaging.targets."); }
        return ValidateVersion(version: nodes[0].Value);
    }

    internal static string ValidateVersion(string version) {
        // Reject spellings NuGet normalizes to the same package identity, so a
        // second source tag cannot claim an already published version.
        if (!Regex.IsMatch(input: version, options: RegexOptions.CultureInvariant, pattern: @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9a-z-]+(?:\.[0-9a-z-]+)*))?\z")) {
            throw new InvalidDataException(message: $"Invalid shared version '{version}': use major.minor.patch with an optional lowercase prerelease suffix.");
        }
        if ((version.IndexOf(value: '-') is var dash) && (dash >= 0)) {
            foreach (var part in version[(dash + 1)..].Split('.')) {
                if (Regex.IsMatch(input: part, options: RegexOptions.CultureInvariant, pattern: @"\A0[0-9]+\z")) {
                    throw new InvalidDataException(message: $"Numeric prerelease identifiers cannot have leading zeroes: {version}.");
                }
            }
        }
        return version;
    }

    private static void ValidateCommit(string commit) {
        if (!Regex.IsMatch(input: commit, options: RegexOptions.CultureInvariant, pattern: @"\A[0-9a-f]{40}\z")) {
            throw new InvalidDataException(message: "Expected a full lowercase Git commit SHA.");
        }
    }
    private static async Task PackAsync(string root, string output, bool noBuild) {
        RequireEmpty(output: output);
        var version = ReadVersion(root: root);
        var projects = Packaging.PackableProjects.Discover(root: root).Select(selector: project => project.File).ToArray();

        if (projects.Length == 0) { throw new InvalidDataException(message: "No packable projects found."); }
        var expected = new HashSet<string>(comparer: PackageComparer);

        foreach (var project in projects) {
            using var metadata = JsonDocument.Parse(await CliProcess.RunCheckedAsync(root, "dotnet",
                ["msbuild", project, "-nologo", "-getProperty:PackageId,Version"], capture: true));
            var properties = metadata.RootElement.GetProperty(propertyName: "Properties");
            var id = properties.GetProperty(propertyName: "PackageId").GetString()!;

            if (properties.GetProperty(propertyName: "Version").GetString() != version) {
                throw new InvalidDataException(message: $"{id} overrides the shared version {version}.");
            }
            if (!expected.Add(item: id)) { throw new InvalidDataException(message: $"Duplicate package ID: {id}."); }
            if (!noBuild) { await CliProcess.RunCheckedAsync(root, "dotnet", ["restore", project, "--locked-mode"]); }
            List<string> pack = ["pack", project, "--configuration", "Release", "--no-restore", "--output", output];
            if (noBuild) { pack.Add("--no-build"); }
            await CliProcess.RunCheckedAsync(root, "dotnet", pack);
        }
        var catalog = ReadPackages(input: output, version: version);

        if (!expected.SetEquals(other: catalog.Keys)) { throw new InvalidDataException(message: "The package set is incomplete or unexpected."); }
        // An all-package selection validates every internal dependency and its
        // range without any NuGet.org lookup or release artifact copies.
        await SelectAsync(catalog, "all", version, _ => throw new InvalidOperationException(message: "Unexpected feed lookup."));
        Console.WriteLine(value: $"Validated {catalog.Count} packages and their internal dependency closure in {output}.");
    }
    private static Dictionary<string, Package> ReadPackages(string input, string version) {
        var catalog = new Dictionary<string, Package>(comparer: PackageComparer);

        foreach (var path in Directory.EnumerateFiles(path: input, searchPattern: "*.nupkg").Order(comparer: StringComparer.Ordinal)) {
            using var zip = ZipFile.OpenRead(archiveFileName: path);
            var entries = zip.Entries.Where(predicate: entry => entry.FullName.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".nuspec")).ToArray();

            if (entries.Length != 1) { throw new InvalidDataException(message: $"Expected one nuspec in {path}."); }
            using var stream = entries[0].Open();
            var metadata = (XDocument.Load(stream: stream).Root?.Elements().Single(predicate: element => (element.Name.LocalName == "metadata"))
                ?? throw new InvalidDataException(message: $"Missing metadata: {path}."));

            string Value(string name) => metadata.Elements().Single(predicate: element => (element.Name.LocalName == name)).Value;
            var id = Value(name: "id");

            if (Value(name: "version") != version) { throw new InvalidDataException(message: $"{id}: expected shared version {version}."); }
            if (Path.GetFileName(path: path) != $"{id}.{version}.nupkg") { throw new InvalidDataException(message: $"Unexpected package filename: {path}."); }
            foreach (var name in new[] { "README.md", "LICENSE.md", "LICENSING.md", "icon.png" }) {
                if (zip.GetEntry(entryName: name) is null) { throw new InvalidDataException(message: $"{id} is missing {name}."); }
            }
            var symbols = Path.Combine(path1: input, path2: $"{id}.{version}.snupkg");

            if (!File.Exists(path: symbols)) { throw new InvalidDataException(message: $"{id} is missing its symbol package."); }
            var dependencies = metadata.Descendants().Where(predicate: element => (element.Name.LocalName == "dependency"))
                .Select(selector: element => new Dependency(Id: (((string?)element.Attribute(name: "id")) ?? ""), Version: (((string?)element.Attribute(name: "version")) ?? ""))).ToArray();

            if (!catalog.TryAdd(key: id, value: new Package(id, Path.GetFullPath(path: path), Path.GetFullPath(path: symbols), dependencies))) {
                throw new InvalidDataException(message: $"Duplicate package ID: {id}.");
            }
        }
        if (catalog.Count == 0) { throw new InvalidDataException(message: "No input packages found."); }
        return catalog;
    }
    private static async Task<string[]> PublishedVersionsAsync(HttpClient http, string id) {
        var uri = $"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/index.json";

        for (var attempt = 0; ; attempt++) {
            using var response = await http.GetAsync(requestUri: uri);

            if (response.StatusCode == HttpStatusCode.NotFound) { return []; }
            if ((attempt < 3) && ((response.StatusCode == HttpStatusCode.TooManyRequests) || (((int)response.StatusCode) >= 500))) {
                await Task.Delay(delay: TimeSpan.FromSeconds(seconds: 2));
                continue;
            }
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            return json.RootElement.GetProperty(propertyName: "versions").EnumerateArray().Select(selector: value => value.GetString()!).ToArray();
        }
    }
    private static async Task<(Package[] Ordered, Dependency[] Published)> SelectAsync(
        Dictionary<string, Package> catalog, string selection, string version, Func<string, Task<string[]>> publishedVersions
    ) {
        var selected = new Dictionary<string, Package>(comparer: PackageComparer);

        foreach (var requested in ((selection.Trim() == "all") ? catalog.Keys : selection.Split(',').Select(selector: id => id.Trim()))) {
            if (!catalog.TryGetValue(key: requested, value: out var package)) {
                throw new InvalidDataException(message: $"Unknown package '{requested}'. Use all or comma-separated package IDs: {string.Join(separator: ", ", values: catalog.Keys.Order(comparer: StringComparer.Ordinal))}.");
            }
            if (!selected.TryAdd(key: package.Id, value: package)) { throw new InvalidDataException(message: $"Duplicate selection: {requested}."); }
        }
        var published = new Dictionary<string, Dependency>(comparer: PackageComparer);

        foreach (var package in selected.Values) {
            foreach (var dependency in package.Dependencies.Where(predicate: dependency => IsInternal(id: dependency.Id))) {
                if (!catalog.ContainsKey(key: dependency.Id)) { throw new InvalidDataException(message: $"{package.Id} depends on unknown internal package {dependency.Id}."); }
                if ((dependency.Version != version) && (dependency.Version != $"[{version}, )") && (dependency.Version != $"[{version}]")) {
                    throw new InvalidDataException(message: $"{package.Id} requires {dependency.Id} {dependency.Version}, outside shared version {version}.");
                }
                if (selected.ContainsKey(key: dependency.Id) || published.ContainsKey(key: dependency.Id)) { continue; }
                if (!(await publishedVersions(dependency.Id)).Contains(version, StringComparer.Ordinal)) {
                    throw new InvalidDataException(message: $"{package.Id} requires omitted package {dependency.Id} {version}, which is not on NuGet.org. Include it and its dependencies, or choose all.");
                }
                published.Add(key: dependency.Id, value: new Dependency(Id: dependency.Id, Version: version));
            }
        }
        // Resolve the full order before copying files or acquiring credentials.
        var remaining = new Dictionary<string, Package>(comparer: PackageComparer, dictionary: selected);
        var ordered = new List<Package>();

        while (remaining.Count != 0) {
            var ready = remaining.Values.Where(predicate: package => !package.Dependencies.Any(predicate: dependency => remaining.ContainsKey(key: dependency.Id)))
                .OrderBy(package => package.Id, StringComparer.Ordinal).ToArray();

            if (ready.Length == 0) { throw new InvalidDataException(message: "Selected packages contain a dependency cycle."); }
            foreach (var package in ready) { ordered.Add(item: package); remaining.Remove(key: package.Id); }
        }
        return (ordered.ToArray(), published.Values.OrderBy(dependency => dependency.Id, StringComparer.Ordinal).ToArray());
    }
    private static bool IsInternal(string id) {
        return (id.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "ByteTerrace.Puck.") || id.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "Puck."));
    }
    private static void RequireEmpty(string output) {
        if (Directory.Exists(path: output) && Directory.EnumerateFileSystemEntries(path: output).Any()) {
            throw new InvalidDataException(message: $"Release output must be empty: {output}.");
        }
    }

    internal static string Hash(string path) {
        using var stream = File.OpenRead(path: path);

        return Convert.ToHexStringLower(inArray: SHA256.HashData(source: stream));
    }
    internal static async Task<JsonObject> PrepareAsync(string input, string output, string selection, string version, string commit, Func<string, Task<string[]>> publishedVersions) {
        ValidateVersion(version: version);
        ValidateCommit(commit: commit);
        RequireEmpty(output: output);
        var (ordered, published) = await SelectAsync(ReadPackages(input: input, version: version), selection, version, publishedVersions);
        Directory.CreateDirectory(path: output);
        var packages = new JsonArray();

        foreach (var package in ordered) {
            var files = new JsonArray();

            foreach (var path in new[] { package.File, package.Symbols }) {
                var name = Path.GetFileName(path: path);

                File.Copy(path, Path.Combine(path1: output, path2: name));
                files.Add(item: ((JsonNode)new JsonObject { ["name"] = name, ["sha256"] = Hash(path: path) }));
            }
            packages.Add(item: ((JsonNode)new JsonObject { ["id"] = package.Id, ["version"] = version, ["files"] = files }));
        }
        var dependencies = new JsonArray();

        foreach (var dependency in published) { dependencies.Add(item: ((JsonNode)new JsonObject { ["id"] = dependency.Id, ["version"] = dependency.Version })); }
        var manifest = new JsonObject { ["version"] = version, ["commit"] = commit, ["packages"] = packages, ["publishedDependencies"] = dependencies };

        File.WriteAllText(Path.Combine(path1: output, path2: "release.json"), (manifest.ToJsonString(options: JsonOptions) + "\n"));
        return manifest;
    }
    internal static JsonObject Verify(string directory, string version, string commit) {
        ValidateVersion(version: version);
        ValidateCommit(commit: commit);
        var manifest = JsonNode.Parse(File.ReadAllText(path: Path.Combine(path1: directory, path2: "release.json")))!.AsObject();

        if ((((string?)manifest["version"]) != version) || (((string?)manifest["commit"]) != commit) || (manifest["packages"] is not JsonArray { Count: > 0 } packages)) {
            throw new InvalidDataException(message: "Release manifest does not match this run.");
        }
        var ids = new HashSet<string>(comparer: PackageComparer);

        foreach (var package in packages) {
            var id = (((string?)package!["id"]) ?? "");

            if (!ids.Add(item: id) || (((string?)package["version"]) != version) || (package["files"] is not JsonArray { Count: 2 } files)) {
                throw new InvalidDataException(message: "Invalid package manifest entry.");
            }
            var expected = new[] { $"{id}.{version}.nupkg", $"{id}.{version}.snupkg" };

            for (var index = 0; (index < files.Count); index++) {
                var name = (((string?)files[index]!["name"]) ?? "");

                if ((name != expected[index]) || name.Contains(value: '/') || name.Contains(value: '\\')) { throw new InvalidDataException(message: "Invalid artifact filename."); }
                if (!string.Equals(a: Hash(path: Path.Combine(path1: directory, path2: name)), b: ((string?)files[index]!["sha256"]), comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException(message: $"Artifact checksum mismatch: {name}.");
                }
            }
        }
        return manifest;
    }
    internal static IEnumerable<string[]> PushArguments(JsonObject manifest, string directory) {
        foreach (var package in manifest["packages"]!.AsArray()) {
            foreach (var file in package!["files"]!.AsArray()) {
                var name = ((string)file!["name"]!);
                List<string> arguments = ["nuget", "push", Path.GetFullPath(path: Path.Combine(path1: directory, path2: name)), "--source", "https://api.nuget.org/v3/index.json", "--skip-duplicate"];
                // Duplicate nupkgs skip automatic symbol uploads. Explicitly
                // push snupkgs on every attempt, with symbol handling enabled.
                if (name.EndsWith(comparisonType: StringComparison.Ordinal, value: ".nupkg")) { arguments.Add(item: "--no-symbols"); }
                yield return arguments.ToArray();
            }
        }
    }

    private static string Summary(JsonObject manifest) {
        var text = new StringBuilder(value: $"## NuGet {manifest["version"]}\nSource: {manifest["commit"]}\n\n| Package | Version |\n| --- | --- |\n");

        foreach (var package in manifest["packages"]!.AsArray()) { text.AppendLine(handler: $"| {package!["id"]} | {package["version"]} |"); }
        text.AppendLine(handler: $"\nAlready published dependencies: {string.Join(separator: ", ", values: manifest["publishedDependencies"]!.AsArray().Select(selector: value => ((string)value!["id"]!)))}");
        return text.ToString();
    }

    private sealed record Dependency(string Id, string Version);
    private sealed record Package(string Id, string File, string Symbols, Dependency[] Dependencies);
}
