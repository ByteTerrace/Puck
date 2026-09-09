using System.IO.Compression;
using System.Text.Json.Nodes;
using static Puck.Cli.NuGet.NuGetCommand;
using Xunit;

namespace Puck.Cli.Tests.NuGet;

public sealed class NuGetCommandTests {
    [Fact]
    public async Task PackRejectsDuplicateOrUnknownBuildOptionsBeforeProducingArtifacts() {
        Assert.Equal(1, await RunAsync(["pack", "--no-build", "--no-build"]));
        Assert.Equal(1, await RunAsync(["pack", "--unknown"]));
    }
    [Fact]
    public void CatalogFindsNestedOptInsAndIgnoresBuildOutputs() {
        var root = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-package-catalog-{Guid.NewGuid():N}");

        try {
            foreach (var directory in new[] { "Nested/Library", "Puck.Azure.Functions", "Nested/obj", "Nested/BIN" }) {
                var path = Path.Combine(path1: root, path2: "src", path3: directory);

                Directory.CreateDirectory(path: path);
                File.WriteAllText(Path.Combine(path1: path, path2: "Project.csproj"), "<Project><PropertyGroup><IsPackable> True </IsPackable></PropertyGroup><Target Name=\"Ignored\"><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Target></Project>");
            }
            var projects = Puck.Cli.Packaging.PackableProjects.Discover(root).ToArray();

            Assert.Equal(2, projects.Length);
            Assert.Contains(projects, project => project.File.Contains("Puck.Azure.Functions", StringComparison.Ordinal));
            Assert.Contains(projects, project => project.File.Contains("Library", StringComparison.Ordinal));
        } finally { if (Directory.Exists(path: root)) { Directory.Delete(root, recursive: true); } }
    }
    [Fact]
    public async Task ReleaseSelectionAndProvenanceAsync() {
        var directory = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-nuget-release-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path: directory);
        var checks = 0;
        const string Version = "1.2.3-preview.1";
        var commit = new string(c: 'a', count: 40);
        var lookups = 0;
        string[] available = [];

        Task<string[]> FeedAsync(string id) { lookups++; return Task.FromResult(result: available); }
        void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message: message); } checks++; }
        async Task FailsAsync(Func<Task> action, string message) {
            try { await action(); } catch (Exception error) {
                Check(condition: error.Message.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: message), message: $"Expected '{message}', got '{error.Message}'.");
                return;
            }
            throw new InvalidOperationException(message: $"Expected failure: {message}.");
        }
        Task FailsSyncAsync(Action action, string message) => FailsAsync(action: () => { action(); return Task.CompletedTask; }, message: message);
        void Fixture(string input, string id, string dependencies = "") {
            Directory.CreateDirectory(path: input);
            using (var zip = ZipFile.Open(archiveFileName: Path.Combine(path1: input, path2: $"{id}.{Version}.nupkg"), mode: ZipArchiveMode.Create)) {
                using (var writer = new StreamWriter(stream: zip.CreateEntry(entryName: $"{id}.nuspec").Open())) {
                    writer.Write(value: $"<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>{id}</id><version>{Version}</version><dependencies><group targetFramework=\"net10.0\">{dependencies}</group></dependencies></metadata></package>");
                }
                foreach (var name in new[] { "README.md", "LICENSE.md", "LICENSING.md", "icon.png" }) { zip.CreateEntry(entryName: name); }
            }
            File.WriteAllText(Path.Combine(path1: input, path2: $"{id}.{Version}.snupkg"), "symbol fixture");
        }
        try {
            var input = Path.Combine(path1: directory, path2: "input");
            const string Basis = "ByteTerrace.Puck.ZBase";
            const string Consumer = "ByteTerrace.Puck.AConsumer";
            const string Independent = "ByteTerrace.Puck.Independent";

            Fixture(input, Basis);
            Fixture(input, Independent);
            Fixture(dependencies: $"<dependency id=\"{Basis}\" version=\"[{Version}, )\" /><dependency id=\"External.Library\" version=\"9.0.0\" />", id: Consumer, input: input);
            Task<JsonObject> BatchAsync(string name, string selection = "all") => PrepareAsync(input, Path.Combine(path1: directory, path2: name), selection, Version, commit, FeedAsync);
            var all = await BatchAsync("all");
            var ids = all["packages"]!.AsArray().Select(selector: value => ((string)value!["id"]!)).ToArray();

            Check(condition: (ids.Length == 3), message: "all must include every package.");
            Check(condition: (Array.IndexOf(array: ids, value: Basis) < Array.IndexOf(array: ids, value: Consumer)), message: "Dependencies must precede consumers.");
            Check(condition: (lookups == 0), message: "A complete batch must not query NuGet.org.");
            Verify(Path.Combine(path1: directory, path2: "all"), Version, commit);
            Check(condition: (((string?)all["commit"]) == commit), message: "Manifest must retain source commit.");
            foreach (var package in all["packages"]!.AsArray()) {
                Check(condition: (package!["files"]!.AsArray().Count == 2), message: "Missing symbols.");
                foreach (var file in package["files"]!.AsArray()) {
                    Check(condition: (Hash(path: Path.Combine(path1: directory, path2: "all", path3: ((string)file!["name"]!))) == ((string?)file["sha256"])), message: "Copied artifact checksum differs.");
                }
            }
            var one = await BatchAsync(name: "one", selection: Independent);

            Check(condition: ((one["packages"]!.AsArray().Count == 1) && (((string?)one["packages"]![0]!["id"]) == Independent)), message: "Single selection leaked a package.");
            Check(condition: (Directory.GetFiles(path: Path.Combine(path1: directory, path2: "one"), searchPattern: "*.nupkg").Length == 1), message: "Single selection copied extra packages.");
            Verify(Path.Combine(path1: directory, path2: "one"), Version, commit);
            var pair = await BatchAsync(name: "pair", selection: $" {Basis}, {Consumer} ");

            Check(condition: (pair["packages"]!.AsArray().Count == 2), message: "Multiple selection must preserve the requested set.");
            await FailsAsync(action: () => BatchAsync(name: "missing", selection: Consumer), message: "requires omitted package");
            Check(condition: !Directory.Exists(path: Path.Combine(path1: directory, path2: "missing")), message: "Missing dependency must fail before copying artifacts.");
            available = ["1.2.2", "1.2.4"];
            await FailsAsync(action: () => BatchAsync(name: "missing", selection: Consumer), message: "requires omitted package");
            available = [Version];
            var partial = await BatchAsync(name: "partial", selection: Consumer);

            Check(condition: ((partial["packages"]!.AsArray().Count == 1) && (((string?)partial["publishedDependencies"]![0]!["id"]) == Basis)), message: "Published dependency must be recorded without being copied.");
            foreach (var selection in new[] { "", $"all,{Basis}", "Puck.Maths", $"{Basis}," }) {
                await FailsAsync(action: () => BatchAsync(name: "invalid", selection: selection), message: "Unknown package");
            }
            await FailsAsync(action: () => BatchAsync(name: "duplicate", selection: $"{Basis},{Basis.ToLowerInvariant()}"), message: "Duplicate selection");
            await FailsAsync(action: () => BatchAsync("all"), message: "must be empty");
            await FailsAsync(action: () => PrepareAsync(input, Path.Combine(path1: directory, path2: "wrong"), "all", "2.0.0", commit, FeedAsync), message: "expected shared version");
            foreach (var (name, dependency, error) in new[] {
                ("unknown", "Puck.Unpackable", "unknown internal package"),
                ("range", Basis, "outside shared version"),
            }) {
                var bad = Path.Combine(path1: directory, path2: name);

                Fixture(bad, Basis);
                Fixture(dependencies: $"<dependency id=\"{dependency}\" version=\"1.0.0\" />", id: Consumer, input: bad);
                await FailsAsync(action: () => PrepareAsync(bad, Path.Combine(path1: directory, path2: "bad-output"), "all", Version, commit, FeedAsync), message: error);
            }
            var cycle = Path.Combine(path1: directory, path2: "cycle");

            Fixture(dependencies: $"<dependency id=\"{Consumer}\" version=\"{Version}\" />", id: Basis, input: cycle);
            Fixture(dependencies: $"<dependency id=\"{Basis}\" version=\"{Version}\" />", id: Consumer, input: cycle);
            await FailsAsync(action: () => PrepareAsync(cycle, Path.Combine(path1: directory, path2: "bad-output"), "all", Version, commit, FeedAsync), message: "dependency cycle");
            foreach (var candidate in new[] { "0.1.0", "1.2.3-rc.1", "1.2.3-alpha-beta" }) { Check(condition: (ValidateVersion(version: candidate) == candidate), message: "Rejected valid version."); }
            foreach (var candidate in new[] { "01.2.3", "1.2", "1.2.3+build", "1.2.3-RC.1", "1.2.3;echo", "v1.2.3" }) {
                await FailsSyncAsync(action: () => ValidateVersion(version: candidate), message: "Invalid shared version");
            }
            await FailsSyncAsync(action: () => ValidateVersion(version: "1.2.3-01"), message: "leading zeroes");
            await FailsSyncAsync(action: () => Verify(Path.Combine(path1: directory, path2: "all"), Version, new string(c: 'b', count: 40)), message: "does not match this run");
            var pushes = PushArguments(all, Path.Combine(path1: directory, path2: "all")).Concat(second: PushArguments(all, Path.Combine(path1: directory, path2: "all"))).ToArray();

            Check(condition: (pushes.Length == 12), message: "Both attempts must push all packages and symbols.");
            foreach (var push in pushes) {
                Check(condition: push.Contains(value: "--skip-duplicate"), message: "Retry must tolerate existing versions.");
                Check(condition: (push[2].EndsWith(comparisonType: StringComparison.Ordinal, value: ".nupkg") == push.Contains(value: "--no-symbols")), message: "Explicit symbols must not be disabled.");
            }
            var artifact = Path.Combine(path1: directory, path2: "all", path3: ((string)all["packages"]![0]!["files"]![0]!["name"]!));

            File.AppendAllText(contents: "tampered", path: artifact);
            await FailsSyncAsync(action: () => Verify(Path.Combine(path1: directory, path2: "all"), Version, commit), message: "checksum mismatch");
            await FailsAsync(action: () => CliProcess.RunCheckedAsync(directory, "dotnet", ["--not-a-dotnet-option"], capture: true), message: "exited with code");
            Console.WriteLine(value: $"Passed {checks} NuGet release assertions.");
        } finally {
            // This path is created above from a fixed prefix and fresh GUID;
            // neither release arguments nor manifest data can change it.
            Directory.Delete(directory, recursive: true);
        }
    }

}
