using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Holds the language server's diagnostic scheduling to counts rather than timings: an edit never diagnoses,
/// a burst of edits diagnoses once at its last version when the input goes quiet (one source-tier unit, and one
/// semantic-tier unit at full depth), a request never diagnoses, and a result for a superseded version is never
/// published.</summary>
public class LspDiagnosticSchedulingTests {
    private const string Source = "schema: \"puck.world.definition.v1\"\ndocumentId: \"scheduling\"\n";
    private const string Uri = "file:///scheduling.puck";

    private static JsonObject Initialize(string? depth) => LanguageServerClient.Request(
        id: 1,
        method: "initialize",
        @params: ((depth is null)
            ? []
            : new JsonObject { ["initializationOptions"] = new JsonObject { ["diagnostics"] = depth } }
        )
    );
    private static JsonObject Open(string text, int version, string uri = Uri) => LanguageServerClient.Notification(
        method: "textDocument/didOpen",
        @params: new JsonObject {
            ["textDocument"] = new JsonObject {
                ["uri"] = uri,
                ["languageId"] = "puck",
                ["version"] = version,
                ["text"] = text,
            },
        }
    );
    private static JsonObject Change(string text, int version, string uri = Uri) => LanguageServerClient.Notification(
        method: "textDocument/didChange",
        @params: new JsonObject {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
        }
    );
    private static void Send(PuckLanguageServer server, JsonObject message, List<JsonObject> written) => server.Handle(
        message: message.ToJsonString(),
        send: written.Add
    );
    private static int Quiet(PuckLanguageServer server, List<JsonObject> written) {
        var units = 0;

        while (server.RunPendingDiagnosis(
            cancellationToken: TestContext.Current.CancellationToken,
            send: written.Add
        )) {
            ++units;
        }

        return units;
    }
    private static JsonObject[] Published(IEnumerable<JsonObject> written) => [.. written.Where(predicate: static message => (message["method"]?.ToString() == "textDocument/publishDiagnostics"))];
    private static int? Version(JsonObject published) => published["params"]?["version"]?.GetValue<int>();

    [InlineData(null, 1)]
    [InlineData("full", 1)]
    [InlineData("source", 0)]
    [Theory]
    public void ABurstOfEditsDiagnosesOnceAtItsLastVersionWhenTheInputGoesQuiet(string? depth, int semanticUnits) {
        const int Edits = 12;
        var server = new PuckLanguageServer();
        var written = new List<JsonObject>();

        Send(message: Initialize(depth: depth), server: server, written: written);
        written.Clear();
        Send(message: Open(text: Source, version: 1), server: server, written: written);

        for (var version = 2; (version <= Edits); ++version) {
            Send(message: Change(text: $"{Source}// edit {version}\n", version: version), server: server, written: written);
        }

        Assert.Equal(actual: (server.SourceDiagnoses, server.SemanticDiagnoses), expected: (0, 0));
        Assert.Empty(collection: written);
        Assert.Equal(
            actual: Quiet(server: server, written: written),
            expected: (1 + semanticUnits)
        );
        Assert.Equal(actual: (server.SourceDiagnoses, server.SemanticDiagnoses), expected: (1, semanticUnits));

        var published = Published(written: written);

        Assert.Equal(actual: published.Length, expected: (1 + semanticUnits));
        Assert.All(collection: published, action: static message => Assert.Equal(actual: Version(published: message), expected: Edits));
        Assert.All(collection: published, action: static message => Assert.Equal(actual: message["params"]?["uri"]?.ToString(), expected: Uri));
    }
    [Fact]
    public void RequestsDuringPendingWorkRunNoDiagnosis() {
        var server = new PuckLanguageServer();
        var written = new List<JsonObject>();
        var document = new JsonObject { ["uri"] = Uri };
        var id = 1;

        void Requests() {
            foreach (var method in new[] { "textDocument/completion", "textDocument/hover" }) {
                Send(
                    message: LanguageServerClient.PositionRequest(
                        character: 2,
                        id: ++id,
                        line: 2,
                        method: method,
                        uri: Uri
                    ),
                    server: server,
                    written: written
                );
            }
            foreach (var method in new[] { "textDocument/semanticTokens/full", "textDocument/formatting", "textDocument/documentSymbol" }) {
                Send(
                    message: LanguageServerClient.Request(
                        id: ++id,
                        method: method,
                        @params: new JsonObject { ["textDocument"] = document.DeepClone() }
                    ),
                    server: server,
                    written: written
                );
            }
        }

        Send(message: Open(text: Source, version: 1), server: server, written: written);
        Send(message: Change(text: $"{Source}host {{\n}}\n", version: 2), server: server, written: written);
        Requests();

        Assert.Equal(actual: (server.SourceDiagnoses, server.SemanticDiagnoses), expected: (0, 0));
        Assert.Empty(collection: Published(written: written));
        Assert.Equal(
            actual: written.Count(predicate: static message => message.ContainsKey(propertyName: "id")),
            expected: (id - 1)
        );

        // One unit — the source tier — then requests again: they interleave before the semantic tier, and run none.
        Assert.True(condition: server.RunPendingDiagnosis(cancellationToken: TestContext.Current.CancellationToken, send: written.Add));
        Assert.True(condition: server.HasPendingDiagnostics);
        Requests();
        Assert.Equal(actual: (server.SourceDiagnoses, server.SemanticDiagnoses), expected: (1, 0));
        Assert.Equal(actual: Quiet(server: server, written: written), expected: 1);
        Assert.Equal(actual: (server.SourceDiagnoses, server.SemanticDiagnoses), expected: (1, 1));
    }
    [Fact]
    public void AResultForASupersededVersionIsNeverPublished() {
        var server = new PuckLanguageServer();
        var written = new List<JsonObject>();

        Send(message: Open(text: Source, version: 1), server: server, written: written);

        var request = Assert.IsType<PuckLanguageServer.DiagnosisRequest>(@object: server.TakePendingDiagnosis());

        Send(message: Change(text: $"{Source}// newer\n", version: 2), server: server, written: written);
        Assert.False(condition: server.IsCurrent(request: request));
        Assert.False(condition: server.CompleteDiagnosis(
            diagnosis: server.Diagnose(
                cancellationToken: TestContext.Current.CancellationToken,
                request: request
            ),
            send: written.Add
        ));
        Assert.Empty(collection: written);
        Assert.Equal(actual: Quiet(server: server, written: written), expected: 2);
        Assert.All(collection: Published(written: written), action: static message => Assert.Equal(actual: Version(published: message), expected: 2));
        Assert.Equal(actual: (server.SourceDiagnoses, server.SemanticDiagnoses), expected: (2, 1));
    }
    [Fact]
    public void AnEditBetweenTheTiersForgetsTheSupersededSemanticTier() {
        var server = new PuckLanguageServer();
        var written = new List<JsonObject>();

        Send(message: Open(text: Source, version: 1), server: server, written: written);
        Assert.True(condition: server.RunPendingDiagnosis(cancellationToken: TestContext.Current.CancellationToken, send: written.Add));
        Send(message: Change(text: $"{Source}// newer\n", version: 2), server: server, written: written);
        Assert.Equal(actual: Quiet(server: server, written: written), expected: 2);

        // Version 1's semantic tier never ran: one per version that reached quiet.
        Assert.Equal(actual: (server.SourceDiagnoses, server.SemanticDiagnoses), expected: (2, 1));
        Assert.Equal(actual: Published(written: written).Select(selector: Version), expected: [1, 2, 2]);
    }
    [Fact]
    public void TheSemanticPublishCarriesBothTiers() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-lsp-tiers-");

        try {
            // The source tier finds an unused `let` (a lint warning); the semantic tier finds that the basis names no
            // document.
            var path = Path.Combine(path1: directory.FullName, path2: "root.puck");
            var text = "schema: \"puck.world.definition.v1\"\nbasis: \"missing\"\nlet unused = 1\n";
            var uri = new System.Uri(uriString: path).AbsoluteUri;

            File.WriteAllText(contents: text, path: path);

            var server = new PuckLanguageServer();
            var written = new List<JsonObject>();

            Send(message: Open(text: text, uri: uri, version: 1), server: server, written: written);
            Assert.Equal(actual: Quiet(server: server, written: written), expected: 2);

            var published = Published(written: written);
            var codes = published.Select(selector: static message => Assert.IsType<JsonArray>(@object: message["params"]?["diagnostics"])
                .Select(selector: static diagnostic => diagnostic?["code"]?.ToString()).ToHashSet()).ToArray();

            Assert.Equal(actual: published.Length, expected: 2);
            Assert.True(condition: codes[0].IsProperSubsetOf(other: codes[1]), userMessage: $"source tier [{string.Join(separator: ", ", values: codes[0])}], both tiers [{string.Join(separator: ", ", values: codes[1])}]");
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void AClosedDocumentIsNeverDiagnosed() {
        var server = new PuckLanguageServer();
        var written = new List<JsonObject>();

        Send(message: Open(text: Source, version: 1), server: server, written: written);
        Send(
            message: LanguageServerClient.Notification(
                method: "textDocument/didClose",
                @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri } }
            ),
            server: server,
            written: written
        );

        Assert.Equal(actual: Quiet(server: server, written: written), expected: 0);
        Assert.Equal(actual: (server.SourceDiagnoses, server.SemanticDiagnoses), expected: (0, 0));
    }
    [Fact]
    public void AnEditMarksTheOpenDocumentsThatReadItAfterItself() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-lsp-dependents-");

        try {
            var basis = Path.Combine(path1: directory.FullName, path2: "base.puck");
            var middle = Path.Combine(path1: directory.FullName, path2: "middle.puck");
            var root = Path.Combine(path1: directory.FullName, path2: "root.puck");
            var alone = Path.Combine(path1: directory.FullName, path2: "alone.puck");
            var basisText = "schema: \"puck.world.definition.v1\"\n";
            var middleText = "schema: \"puck.world.definition.v1\"\nbasis: \"base\"\n";
            var rootText = "schema: \"puck.world.definition.v1\"\nbasis: \"middle\"\n";

            File.WriteAllText(contents: basisText, path: basis);
            File.WriteAllText(contents: middleText, path: middle);
            File.WriteAllText(contents: rootText, path: root);
            File.WriteAllText(contents: basisText, path: alone);

            var uris = new[] { basis, middle, root, alone }.Select(selector: static path => new System.Uri(uriString: path).AbsoluteUri).ToArray();
            var server = new PuckLanguageServer();
            var written = new List<JsonObject>();

            Send(message: Open(text: rootText, uri: uris[2], version: 1), server: server, written: written);
            Send(message: Open(text: middleText, uri: uris[1], version: 1), server: server, written: written);
            Send(message: Open(text: basisText, uri: uris[0], version: 1), server: server, written: written);
            Send(message: Open(text: basisText, uri: uris[3], version: 1), server: server, written: written);
            _ = Quiet(server: server, written: written);
            Assert.Equal(actual: server.SourceDiagnoses, expected: 4);

            Send(message: Change(text: $"{basisText}// edited\n", uri: uris[0], version: 2), server: server, written: written);

            var order = new List<string>();

            while (server.TakePendingDiagnosis() is { } request) {
                order.Add(item: request.Uri);
            }

            // The edited document first, then what reads it, then what reads that; the unrelated document not at all.
            Assert.Equal(actual: order, expected: [uris[0], uris[1], uris[2]]);
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public async Task TheStdioHostPublishesTheLastVersionOnceItsInputEnds() {
        using var input = new MemoryStream();
        using var output = new MemoryStream();

        await LspFraming.WriteAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            json: Open(text: Source, version: 1).ToJsonString(),
            stream: input
        );

        for (var version = 2; (version <= 5); ++version) {
            await LspFraming.WriteAsync(
                cancellationToken: TestContext.Current.CancellationToken,
                json: Change(text: $"{Source}// edit {version}\n", version: version).ToJsonString(),
                stream: input
            );
        }
        input.Position = 0;

        var server = new PuckLanguageServer();

        await server.RunAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            input: input,
            output: output
        );
        output.Position = 0;

        var written = new List<JsonObject>();

        while (await LspFraming.ReadAsync(cancellationToken: TestContext.Current.CancellationToken, stream: output) is { } body) {
            written.Add(item: Assert.IsType<JsonObject>(@object: JsonNode.Parse(json: body)));
        }

        var versions = Published(written: written).Select(selector: Version).ToArray();

        // However the reads interleave with diagnosis, each published version was current when it was published, so the
        // versions only rise, the last one is the last one sent, and it carries both tiers.
        Assert.NotEmpty(collection: versions);
        Assert.Equal(actual: versions[^1], expected: 5);
        Assert.Equal(actual: versions, expected: versions.Order());
        Assert.Equal(actual: versions.Count(predicate: static version => (version == 5)), expected: 2);
        Assert.InRange(actual: server.SourceDiagnoses, high: 5, low: 1);
    }
    [Fact]
    public void AMessageThatIsNotJsonIsAnsweredWithALogMessageAndTheSessionStaysOpen() {
        var server = new PuckLanguageServer();
        var written = new List<JsonObject>();

        server.Handle(message: "{ not json", send: written.Add);

        var log = Assert.Single(collection: written);

        Assert.Equal(actual: log["method"]?.ToString(), expected: "window/logMessage");
        Assert.True(condition: server.IsRunning);

        Send(message: LanguageServerClient.Request(id: 1, method: "shutdown"), server: server, written: written);
        Assert.False(condition: server.IsRunning);
    }
}
