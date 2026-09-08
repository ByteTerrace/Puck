using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Xunit;

namespace Puck.Cli.Tests;

public sealed class FormatSubmissionTests {
    [Fact]
    public async Task AFormattingCommitUsesAnExpectedHeadAndDispatchesAllChecks() {
        using var handler = new GitHubHandler(scenario: "apply");
        using var client = new HttpClient(handler: handler) { BaseAddress = new Uri(uriString: "https://api.invalid/") };

        await new FormatSubmission(client: client).RunAsync(repository: "owner/Puck", runId: 12, graphUrl: "https://api.invalid/graphql");
        Assert.Single(collection: handler.Commits);
        var input = handler.Commits[0]["variables"]!["input"]!;

        Assert.Equal(expected: GitHubHandler.Head, actual: ((string?)input["expectedHeadOid"]));
        Assert.Equal(expected: "feature", actual: ((string?)input["branch"]!["refName"]));
        Assert.Single(collection: input["fileChanges"]!["additions"]!.AsArray());
        Assert.Equal(expected: 4, actual: handler.Dispatches.Count);
        Assert.Equal(expected: "false", actual: ((string?)handler.Dispatches.Single(predicate: item => item.Path.Contains(comparisonType: StringComparison.Ordinal, value: "azure.yml")).Body["inputs"]!["deploy"]));
    }
    [InlineData("stale")]
    [InlineData("fork")]
    [InlineData("failed")]
    [InlineData("clean")]
    [InlineData("base-moved")]
    [Theory]
    public async Task InapplicableResultsNeverWriteOrDispatch(string scenario) {
        using var handler = new GitHubHandler(scenario: scenario);
        using var client = new HttpClient(handler: handler) { BaseAddress = new Uri(uriString: "https://api.invalid/") };

        await new FormatSubmission(client: client).RunAsync(repository: "owner/Puck", runId: 12, graphUrl: "https://api.invalid/graphql");
        Assert.Empty(collection: handler.Commits);
        Assert.Empty(collection: handler.Dispatches);
    }
    [InlineData("non-pr-file")]
    [InlineData("duplicate")]
    [InlineData("traversal")]
    [InlineData("symlink")]
    [InlineData("wrong-workflow")]
    [InlineData("default-branch")]
    [Theory]
    public async Task UntrustedArtifactsCannotExpandTheWriteScope(string scenario) {
        using var handler = new GitHubHandler(scenario: scenario);
        using var client = new HttpClient(handler: handler) { BaseAddress = new Uri(uriString: "https://api.invalid/") };

        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => new FormatSubmission(client: client).RunAsync(repository: "owner/Puck", runId: 12, graphUrl: "https://api.invalid/graphql"));
        Assert.Empty(collection: handler.Commits);
        Assert.Empty(collection: handler.Dispatches);
    }
    [Fact]
    public async Task ACommitRaceDoesNotDispatchChecksForTheWrongHead() {
        using var handler = new GitHubHandler(scenario: "race");
        using var client = new HttpClient(handler: handler) { BaseAddress = new Uri(uriString: "https://api.invalid/") };

        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => new FormatSubmission(client: client).RunAsync(repository: "owner/Puck", runId: 12, graphUrl: "https://api.invalid/graphql"));
        Assert.Empty(collection: handler.Dispatches);
    }
    [Fact]
    public async Task RetryingAfterACommitResumesDispatchWithoutAnotherCommit() {
        using var handler = new GitHubHandler(scenario: "retry");
        using var client = new HttpClient(handler: handler) { BaseAddress = new Uri(uriString: "https://api.invalid/") };

        await new FormatSubmission(client: client).RunAsync(repository: "owner/Puck", runId: 12, graphUrl: "https://api.invalid/graphql");
        Assert.Empty(collection: handler.Commits);
        Assert.Equal(expected: 4, actual: handler.Dispatches.Count);
    }

    private sealed class GitHubHandler : HttpMessageHandler {
        internal const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const string Base = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string NewHead = "cccccccccccccccccccccccccccccccccccccccc";

        private readonly string m_scenario;

        internal List<JsonNode> Commits { get; } = [];
        internal List<(string Path, JsonNode Body)> Dispatches { get; } = [];

        internal GitHubHandler(string scenario) { m_scenario = scenario; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var path = request.RequestUri!.PathAndQuery;

            if (request.Method == HttpMethod.Post) {
                var body = JsonNode.Parse(json: await request.Content!.ReadAsStringAsync(cancellationToken: cancellationToken))!;

                if (path == "/graphql") {
                    Commits.Add(item: body);
                    return Json(value: ((m_scenario == "race") ? new JsonObject { ["errors"] = new JsonArray("head advanced") } : new JsonObject { ["data"] = new JsonObject { ["createCommitOnBranch"] = new JsonObject { ["commit"] = new JsonObject { ["oid"] = NewHead } } } }));
                }
                Dispatches.Add(item: (path, body));
                return new HttpResponseMessage(statusCode: HttpStatusCode.NoContent);
            }
            var relative = path.Replace(comparisonType: StringComparison.Ordinal, newValue: "", oldValue: "/repos/owner/Puck");

            return relative switch {
                "/actions/runs/12" => Json(value: new JsonObject { ["workflow_id"] = ((m_scenario == "wrong-workflow") ? 2 : 1), ["event"] = "pull_request", ["head_sha"] = Head, ["run_attempt"] = 1 }),
                "/actions/workflows/format.yml" => Json(value: new JsonObject { ["id"] = 1 }),
                "/actions/runs/12/attempts/1/jobs?per_page=100" => Json(value: new JsonObject { ["jobs"] = new JsonArray(new JsonObject { ["name"] = "Prepare formatting", ["conclusion"] = ((m_scenario == "failed") ? "failure" : "success") }) }),
                var p when (p == $"/commits/{Head}/pulls?per_page=100") => Json(value: new JsonArray(Pull())),
                "/pulls/7" => Json(value: Pull()),
                "" => Json(value: new JsonObject { ["default_branch"] = "main" }),
                var p when (p == $"/commits/{NewHead}") => Json(value: new JsonObject { ["commit"] = new JsonObject { ["message"] = ((m_scenario == "retry") ? "style: apply Puck formatting\n\nPuck-Format-Run: 12" : "contributor change") }, ["author"] = new JsonObject { ["login"] = "github-actions[bot]" }, ["parents"] = new JsonArray(new JsonObject { ["sha"] = Head }) }),
                "/actions/runs/12/artifacts?per_page=100" => Json(value: new JsonObject { ["artifacts"] = new JsonArray(new JsonObject { ["name"] = "puck-format", ["expired"] = false, ["id"] = 9 }) }),
                "/actions/artifacts/9/zip" => Artifact(),
                "/pulls/7/files?per_page=100&page=1" => Json(value: new JsonArray(new JsonObject { ["filename"] = "src/One.cs", ["status"] = "modified" }, new JsonObject { ["filename"] = "../One.cs", ["status"] = "modified" })),
                var p when (p == $"/git/trees/{Head}") => Json(value: new JsonObject { ["tree"] = new JsonArray(new JsonObject { ["path"] = "src", ["type"] = "tree", ["sha"] = "tree-src" }) }),
                "/git/trees/tree-src" => Json(value: new JsonObject { ["tree"] = new JsonArray(new JsonObject { ["path"] = "One.cs", ["type"] = "blob", ["mode"] = ((m_scenario == "symlink") ? "120000" : "100644") }) }),
                _ => throw new InvalidOperationException(message: $"Unexpected mock API request: {path}"),
            };
        }

        private JsonObject Pull() => new() {
            ["number"] = 7,
            ["state"] = "open",
            ["head"] = new JsonObject { ["sha"] = ((m_scenario is "stale" or "retry") ? NewHead : Head), ["ref"] = ((m_scenario == "default-branch") ? "main" : "feature"), ["repo"] = new JsonObject { ["full_name"] = ((m_scenario == "fork") ? "contributor/Puck" : "owner/Puck") } },
            ["base"] = new JsonObject { ["sha"] = Base, ["ref"] = "main", ["repo"] = new JsonObject { ["full_name"] = "owner/Puck" } },
        };
        private HttpResponseMessage Artifact() {
            var path = m_scenario switch { "non-pr-file" => "src/Other.cs", "traversal" => "../One.cs", _ => "src/One.cs" };
            var change = new JsonObject { ["path"] = path, ["contents"] = Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: "class One { }\n")) };
            var files = ((m_scenario == "clean") ? new JsonArray() : new JsonArray(change));

            if (m_scenario == "duplicate") { files.Add(item: change.DeepClone()); }
            var report = new JsonObject { ["base"] = ((m_scenario == "base-moved") ? NewHead : Base), ["head"] = Head, ["files"] = files };
            using var bytes = new MemoryStream();

            using (var zip = new ZipArchive(leaveOpen: true, mode: ZipArchiveMode.Create, stream: bytes)) {
                using var writer = new StreamWriter(stream: zip.CreateEntry(entryName: "format.json").Open());

                writer.Write(value: report.ToJsonString());
            }
            return new HttpResponseMessage(statusCode: HttpStatusCode.OK) { Content = new ByteArrayContent(content: bytes.ToArray()) };
        }
        private static HttpResponseMessage Json(JsonNode value) =>
            new(statusCode: HttpStatusCode.OK) { Content = new StringContent(content: value.ToJsonString(), encoding: Encoding.UTF8, mediaType: "application/json") };
    }
}
