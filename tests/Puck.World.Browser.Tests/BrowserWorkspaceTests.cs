using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>The browser engine's authoring surface over a workspace rooted in a temporary directory: the same
/// <see cref="BrowserWorkspace"/> and <see cref="BrowserLanguageServer"/> the AppBundle runs over its in-memory
/// <c>/worlds</c> mount.</summary>
public sealed class BrowserWorkspaceTests : IDisposable {
    private const string Broken = "schema: \"puck.world.definition.v1\"\nbasis: \"../base\"\n\nhost: {\n  width: 320\n}\n";
    private const string Counter = "games/counter.puck";

    private readonly DirectoryInfo m_directory = Directory.CreateTempSubdirectory(prefix: "puck-browser-workspace-");
    private readonly BrowserWorkspace m_workspace;

    public BrowserWorkspaceTests() {
        m_workspace = new BrowserWorkspace(root: Path.Combine(
            path1: m_directory.FullName,
            path2: "worlds"
        ));
    }

    public void Dispose() => m_directory.Delete(recursive: true);

    private static Dictionary<string, string> Fixtures() {
        var root = RepositoryPaths.Resolve(relativePath: "tests/Puck.World.Browser.Tests/Fixtures/sources");

        return Directory.EnumerateFiles(
            path: root,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*.puck"
        ).ToDictionary(
            elementSelector: static path => File.ReadAllText(path: path),
            keySelector: path => Path.GetRelativePath(
                path: path,
                relativeTo: root
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            ),
            comparer: StringComparer.Ordinal
        );
    }
    private void MountFixtures() => Assert.Null(@object: m_workspace.Mount(files: Fixtures()));
    private static JsonObject Message(string method, JsonObject? @params = null, int? id = null) {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };

        if (id is { } number) {
            message["id"] = number;
        }
        if (@params is not null) {
            message["params"] = @params;
        }

        return message;
    }
    private static JsonArray Exchange(BrowserLanguageServer server, JsonObject message) => Assert.IsType<JsonArray>(@object: JsonNode.Parse(json: server.Exchange(message: message.ToJsonString())));
    private JsonObject Open(string path, string text, int version = 1) => Message(
        method: "textDocument/didOpen",
        @params: new JsonObject {
            ["textDocument"] = new JsonObject {
                ["uri"] = m_workspace.DocumentUri(path: path),
                ["languageId"] = "puck",
                ["version"] = version,
                ["text"] = text,
            },
        }
    );
    private JsonObject Change(string path, string text, int version) => Message(
        method: "textDocument/didChange",
        @params: new JsonObject {
            ["textDocument"] = new JsonObject { ["uri"] = m_workspace.DocumentUri(path: path), ["version"] = version },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
        }
    );
    private static List<JsonObject> Quiet(BrowserLanguageServer server) {
        var published = new List<JsonObject>();

        while (true) {
            var idle = Assert.IsType<JsonObject>(@object: JsonNode.Parse(json: server.Idle()));

            if (!idle["ran"]!.GetValue<bool>()) {
                return published;
            }
            published.AddRange(collection: Assert.IsType<JsonArray>(@object: idle["messages"]).Select(selector: static message => Assert.IsType<JsonObject>(@object: message)));
        }
    }

    [Fact]
    public void AGameWhoseBasisIsASourceCompilesWithItsSourceMap() {
        MountFixtures();

        var result = m_workspace.Compile(path: Counter);

        Assert.True(
            condition: result.Ok,
            userMessage: string.Join(separator: Environment.NewLine, values: result.Diagnostics.Select(selector: static diagnostic => $"{diagnostic.Path}({diagnostic.Line},{diagnostic.Column}) {diagnostic.Code} {diagnostic.Message}"))
        );
        Assert.DoesNotContain(collection: result.Diagnostics, filter: static diagnostic => (diagnostic.Severity == "error"));
        Assert.Contains(expectedSubstring: "\"basis\"", actualString: result.Document);
        Assert.Empty(collection: result.Worlds);
        Assert.Contains(
            collection: result.SourceMap.Values,
            filter: static origin => ((origin.Path == Counter) && (origin.Line > 0))
        );
    }
    [Fact]
    public void ABrokenSourceReportsItsDiagnosticWhereItWasWritten() {
        MountFixtures();
        Assert.Null(@object: m_workspace.Write(path: "games/broken.puck", text: Broken));

        var result = m_workspace.Compile(path: "games/broken.puck");

        Assert.False(condition: result.Ok);
        Assert.Null(@object: result.Document);

        var diagnostic = Assert.Single(collection: result.Diagnostics, predicate: static diagnostic => (diagnostic.Code == "PUCK040"));

        Assert.Equal(actual: diagnostic.Severity, expected: "error");
        Assert.Equal(actual: diagnostic.Path, expected: "games/broken.puck");
        Assert.Equal(actual: diagnostic.Line, expected: 4);
    }
    [Fact]
    public void ComposingAGameFoldsItsSourceBasisIn() {
        MountFixtures();

        var result = m_workspace.Compose(path: Counter);

        Assert.True(condition: result.Ok, userMessage: string.Join(separator: "; ", values: result.Diagnostics.Select(selector: static diagnostic => $"{diagnostic.Path}({diagnostic.Line},{diagnostic.Column}) {diagnostic.Code} {diagnostic.Message}")));

        var composed = Assert.IsType<JsonObject>(@object: JsonNode.Parse(json: result.Composed!));

        Assert.Null(@object: composed["basis"]);
        Assert.Equal(actual: composed["host"]?["width"]?.GetValue<int>(), expected: 640);
        Assert.Contains(expectedSubstring: "\"score\"", actualString: result.Composed);
    }

    // A JSON root and a JSON basis compose through the same document source a .puck chain does.
    private const string Fragment = "schema: \"puck.world.definition.v1\"\n\nstate {\n  world {\n    table bonus {\n      stars = 3\n    }\n  }\n}\n";
    private const string JsonBasis = """{"schema":"puck.world.definition.v1","host":{"presentation":"Windowed","backend":"auto","width":800,"height":600,"surfaceFormat":"r8g8b8a8","fullscreen":false,"presentMode":"Immediate","targetHertz":60,"exitAfterSeconds":0}}""";
    private const string JsonRoot = """{"schema":"puck.world.definition.v1","basis":"base","imports":[{"document":"games/bonus"}]}""";
    // A fragment whose two rules share one name: it compiles alone, and only the composed world refuses it.
    private const string Duplicated = "schema: \"puck.world.definition.v1\"\n\nstate {\n  world {\n    table ticks {\n      count = 0\n    }\n  }\n}\n\nrule \"tick\" {\n  ticks[count] += 1\n}\n\nrule \"tick\" {\n  ticks[count] += 2\n}\n";

    private void MountMixed() => Assert.Null(@object: m_workspace.Mount(files: new Dictionary<string, string>(dictionary: Fixtures()) {
        ["games/bonus.puck"] = Fragment,
        ["games/over-json.puck"] = "schema: \"puck.world.definition.v1\"\nbasis: \"../plain\"\n",
        ["hub.world.json"] = JsonRoot,
        ["plain.world.json"] = JsonBasis,
        ["games/duplicated.puck"] = Duplicated,
        ["duplicated-host.world.json"] = """{"schema":"puck.world.definition.v1","basis":"base","imports":[{"document":"games/duplicated"}]}""",
        ["duplicated-host.puck"] = "schema: \"puck.world.definition.v1\"\nbasis: \"base\"\nimport \"games/duplicated\"\n",
    }));

    // Composing validates what it composes: a fragment that compiles alone but breaks the composed world is refused,
    // and the composed document is still handed back to look at.
    [InlineData("duplicated-host.world.json")]
    [InlineData("duplicated-host.puck")]
    [Theory]
    public void ComposingValidatesTheComposedWorld(string root) {
        MountMixed();
        Assert.True(condition: Puck.World.Transpiler.WorldCompiler.CompileFile(
            cancellationToken: TestContext.Current.CancellationToken,
            path: Path.Combine(path1: m_workspace.Root, path2: "games/duplicated.puck")
        ).Success);

        var result = m_workspace.Compose(path: root);

        Assert.False(condition: result.Ok);
        Assert.NotNull(@object: result.Composed);
        Assert.Contains(
            collection: result.Diagnostics,
            filter: static diagnostic => ((diagnostic.Code == "PUCK030") && (diagnostic.Severity == "error") && diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "tick"))
        );
    }
    // The shipped island, mounted from a copy of the worlds tree, composes and validates: the checks this engine
    // cannot answer (it carries no machine catalog) are deferral notices, never findings against the author.
    [Fact]
    public void TheShippedIslandComposesWithItsDeferralsListed() {
        var worlds = RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/worlds");
        var files = Directory.EnumerateFiles(
            path: worlds,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        ).Where(predicate: static path => (path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".json") || path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".puck"))).ToDictionary(
            elementSelector: static path => File.ReadAllText(path: path),
            keySelector: path => Path.GetRelativePath(path: path, relativeTo: worlds).Replace(newChar: '/', oldChar: '\\'),
            comparer: StringComparer.Ordinal
        );

        Assert.Null(@object: m_workspace.Mount(files: files));

        var result = m_workspace.Compose(path: "puck.world.json");

        Assert.True(condition: result.Ok, userMessage: string.Join(separator: "\n", values: result.Diagnostics.Where(predicate: static diagnostic => (diagnostic.Severity == "error")).Select(selector: static diagnostic => $"{diagnostic.Path}({diagnostic.Line},{diagnostic.Column}) {diagnostic.Code} {diagnostic.Message}")));
        Assert.NotNull(@object: result.Composed);
        Assert.DoesNotContain(collection: result.Diagnostics, filter: static diagnostic => (diagnostic.Severity == "error"));
        Assert.Contains(
            collection: result.Diagnostics,
            filter: static diagnostic => ((diagnostic.Code == "PUCK118") && (diagnostic.Severity == "information") && diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "no machine catalog"))
        );
    }
    [Fact]
    public void AJsonRootThatImportsASourceComposes() {
        MountMixed();

        var result = m_workspace.Compose(path: "hub.world.json");

        Assert.True(condition: result.Ok, userMessage: string.Join(separator: "; ", values: result.Diagnostics.Select(selector: static diagnostic => $"{diagnostic.Path}({diagnostic.Line},{diagnostic.Column}) {diagnostic.Code} {diagnostic.Message}")));

        var composed = Assert.IsType<JsonObject>(@object: JsonNode.Parse(json: result.Composed!));

        Assert.Null(@object: composed["imports"]);
        Assert.Equal(actual: composed["host"]?["width"]?.GetValue<int>(), expected: 640);
        Assert.Contains(expectedSubstring: "\"bonus\"", actualString: result.Composed);
    }
    [Fact]
    public void ASourceWithAJsonBasisComposes() {
        MountMixed();

        var result = m_workspace.Compose(path: "games/over-json.puck");

        Assert.True(condition: result.Ok, userMessage: string.Join(separator: "; ", values: result.Diagnostics.Select(selector: static diagnostic => $"{diagnostic.Path}({diagnostic.Line},{diagnostic.Column}) {diagnostic.Code} {diagnostic.Message}")));
        Assert.Equal(actual: JsonNode.Parse(json: result.Composed!)?["host"]?["width"]?.GetValue<int>(), expected: 800);
    }
    [Fact]
    public void CompilingAJsonDocumentReturnsItAsItStands() {
        MountMixed();

        var result = m_workspace.Compile(path: "plain.world.json");

        Assert.True(condition: result.Ok);
        Assert.Empty(collection: result.Diagnostics);
        Assert.Equal(actual: JsonNode.Parse(json: result.Document!)?["host"]?["height"]?.GetValue<int>(), expected: 600);
        Assert.Null(@object: m_workspace.Write(path: "broken.world.json", text: "{ not json"));
        Assert.False(condition: m_workspace.Compile(path: "broken.world.json").Ok);
        Assert.False(condition: m_workspace.Compose(path: "broken.world.json").Ok);
    }
    [Fact]
    public void MountingReplacesTheWholeWorkspace() {
        MountFixtures();
        Assert.Null(@object: m_workspace.Mount(files: new Dictionary<string, string> { ["only.puck"] = "schema: \"puck.world.definition.v1\"\n" }));

        var result = m_workspace.Compile(path: Counter);

        Assert.False(condition: result.Ok);
        Assert.Contains(expectedSubstring: "no source is mounted", actualString: Assert.Single(collection: result.Diagnostics).Message);
    }
    [InlineData("../escape.puck")]
    [InlineData("/worlds/absolute.puck")]
    [InlineData("games\\back.puck")]
    [InlineData("games//empty.puck")]
    [InlineData("C:/drive.puck")]
    [Theory]
    public void APathOutsideTheWorkspaceIsRefused(string path) {
        Assert.NotNull(@object: m_workspace.Write(path: path, text: ""));
        Assert.NotNull(@object: m_workspace.Mount(files: new Dictionary<string, string> { [path] = "" }));
    }
    [Fact]
    public void TheLanguageServerDiagnosesOnlyWhenIdleAndWritesEditsThrough() {
        MountFixtures();

        var server = new BrowserLanguageServer(workspace: m_workspace);
        var text = File.ReadAllText(path: Path.Combine(path1: m_workspace.Root, path2: Counter));

        Assert.Single(collection: Exchange(server: server, message: Message(id: 1, method: "initialize", @params: [])));
        Assert.Empty(collection: Exchange(server: server, message: Open(path: Counter, text: text)));
        Assert.Empty(collection: Exchange(server: server, message: Change(path: Counter, text: Broken, version: 2)));
        Assert.Equal(actual: File.ReadAllText(path: Path.Combine(path1: m_workspace.Root, path2: Counter)), expected: Broken);

        var tokens = Assert.Single(collection: Exchange(
            server: server,
            message: Message(
                id: 2,
                method: "textDocument/semanticTokens/full",
                @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = m_workspace.DocumentUri(path: Counter) } }
            )
        ));

        Assert.NotEmpty(collection: Assert.IsType<JsonArray>(@object: tokens?["result"]?["data"]));
        Assert.Equal(actual: server.Server.SourceDiagnoses, expected: 0);

        var published = Assert.Single(collection: Quiet(server: server));

        Assert.Equal(actual: server.Server.SourceDiagnoses, expected: 1);
        Assert.Equal(actual: server.Server.SemanticDiagnoses, expected: 0);
        Assert.Equal(actual: published["params"]?["version"]?.GetValue<int>(), expected: 2);
        Assert.Contains(
            collection: Assert.IsType<JsonArray>(@object: published["params"]?["diagnostics"]),
            filter: static diagnostic => (diagnostic?["code"]?.ToString() == "PUCK040")
        );
    }
    [Fact]
    public void CompilingASourceCompilesItOnce() {
        MountFixtures();

        // The semantic tier composes the basis through the compile cache; held there first, it compiles nothing, so
        // every compile the ledger counts is the root's.
        Assert.True(condition: Puck.World.Transpiler.Composition.WorldCompileCache.Shared.TryCompile(
            compiled: out _,
            failure: out _,
            path: Path.Combine(path1: m_workspace.Root, path2: "base.puck")
        ));

        var work = new WorldBootWork();

        using (WorldBootWork.Attribute(work: work)) {
            Assert.True(condition: m_workspace.Compile(path: Counter).Ok);
        }

        Assert.Equal(actual: work.Read(kind: WorldBootWork.Compiles), expected: 1L);
    }
    [Fact]
    public void AtSourceDepthTheLanguageServerRunsNoComposition() {
        MountFixtures();

        var server = new BrowserLanguageServer(workspace: m_workspace);
        var text = File.ReadAllText(path: Path.Combine(path1: m_workspace.Root, path2: Counter));

        _ = Exchange(server: server, message: Message(id: 1, method: "initialize", @params: new JsonObject { ["initializationOptions"] = new JsonObject { ["diagnostics"] = "source" } }));
        _ = Exchange(server: server, message: Open(path: Counter, text: text));

        var published = Assert.Single(collection: Quiet(server: server));

        Assert.Equal(actual: (server.Server.SourceDiagnoses, server.Server.SemanticDiagnoses), expected: (1, 0));
        Assert.Equal(actual: published["params"]?["version"]?.GetValue<int>(), expected: 1);
    }
    [Fact]
    public void EditingABasisRediagnosesTheGamesThatReadItAfterIt() {
        MountFixtures();

        var server = new BrowserLanguageServer(workspace: m_workspace);
        var basis = File.ReadAllText(path: Path.Combine(path1: m_workspace.Root, path2: "base.puck"));
        var counter = File.ReadAllText(path: Path.Combine(path1: m_workspace.Root, path2: Counter));

        _ = Exchange(server: server, message: Open(path: Counter, text: counter));
        _ = Exchange(server: server, message: Open(path: "base.puck", text: basis));
        Assert.Equal(actual: Quiet(server: server).Count, expected: 4);

        _ = Exchange(server: server, message: Change(path: "base.puck", text: $"{basis}// edited\n", version: 2));

        Assert.Equal(
            actual: Quiet(server: server).Select(selector: static published => published["params"]?["uri"]?.ToString()),
            expected: [m_workspace.DocumentUri(path: "base.puck"), m_workspace.DocumentUri(path: "base.puck"), m_workspace.DocumentUri(path: Counter), m_workspace.DocumentUri(path: Counter)]
        );
    }
}
