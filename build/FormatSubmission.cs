using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck;

// This is deliberately SDK-only: the privileged workflow must not bootstrap or execute the PR's
// CLI, MSBuild files, hooks, or artifact contents. GitHub receives validated source bytes as data.
internal sealed class FormatSubmission {
    private const int MaximumBytes = ((16 * 1024) * 1024);

    private readonly HttpClient m_client;
    private readonly Action<string> m_report;

    internal FormatSubmission(HttpClient client, Action<string> report) { m_client = client; m_report = report; }

    internal async Task RunAsync(string repository, long runId, string graphUrl) {
        var prefix = $"repos/{repository}";
        var run = await GetAsync(path: $"{prefix}/actions/runs/{runId}");
        var workflow = await GetAsync(path: $"{prefix}/actions/workflows/format.yml");

        if ((((long?)run["workflow_id"]) != ((long?)workflow["id"])) || (((string?)run["event"]) is not ("pull_request" or "workflow_dispatch"))) {
            throw new InvalidDataException(message: "The artifact must come from the repository's Check source formatting workflow.");
        }
        var jobs = await GetAsync(path: $"{prefix}/actions/runs/{runId}/attempts/{((int)run["run_attempt"]!)}/jobs?per_page=100");

        if (!jobs["jobs"]!.AsArray().Any(predicate: static job => ((((string?)job!["name"]) == "Prepare formatting") && (((string?)job["conclusion"]) == "success")))) {
            Report(message: "Formatting did not finish and compile successfully; no commit was applied.");
            return;
        }
        var head = ((string)run["head_sha"]!);
        var pulls = (await GetAsync(path: $"{prefix}/commits/{head}/pulls?per_page=100")).AsArray();
        var candidates = pulls.Where(predicate: pull => ((((string?)pull!["state"]) == "open") && (((string?)pull["base"]?["repo"]?["full_name"]) == repository))).ToArray();

        // A branch shared by multiple open PRs has no unambiguous base/target. Keep the patch available.
        if (candidates.Length != 1) {
            Report(message: "No single open PR owns this run. The puck-format artifact remains available; no branch was changed.");
            return;
        }
        var number = ((int)candidates[0]!["number"]!);
        var pull = await GetAsync(path: $"{prefix}/pulls/{number}");
        var branch = ((string)pull["head"]!["ref"]!);

        if (((string?)pull["head"]!["repo"]?["full_name"]) != repository) {
            Report(message: $"PR #{number} is from a fork. Download puck-format from run {runId} and apply format.patch with git apply; the repository token cannot push to the contributor's repository.");
            return;
        }
        var repositoryInfo = await GetAsync(path: prefix);

        if ((branch == ((string?)repositoryInfo["default_branch"])) || (branch == ((string?)pull["base"]!["ref"])) || (branch == "codex/azure-ci")) {
            throw new InvalidDataException(message: "Refusing to autoformat a default, base, or deployment branch.");
        }
        var currentHead = ((string)pull["head"]!["sha"]!);
        var message = $"style: apply Puck formatting\n\nPuck-Format-Run: {runId}";

        if (currentHead != head) {
            // A retry after a successful commit but a failed dispatch must resume validation, never
            // create a second commit or overwrite a contributor's intervening push.
            var commit = await GetAsync(path: $"{prefix}/commits/{currentHead}");

            if ((((string?)commit["commit"]?["message"]) == message)
                && (((string?)commit["author"]?["login"]) == "github-actions[bot]")
                && (commit["parents"]!.AsArray().Count == 1)
                && (((string?)commit["parents"]![0]?["sha"]) == head)) {
                await DispatchAsync(prefix: prefix, branch: branch, baseSha: ((string)pull["base"]!["sha"]!));
                return;
            }
            Report(message: $"PR #{number} advanced while formatting ran; the stale result was discarded.");
            return;
        }
        var artifacts = await GetAsync(path: $"{prefix}/actions/runs/{runId}/artifacts?per_page=100");
        var artifact = artifacts["artifacts"]!.AsArray().Single(predicate: static item => ((((string?)item!["name"]) == "puck-format") && (((bool?)item["expired"]) == false)))!;
        using var response = await m_client.GetAsync(requestUri: $"{prefix}/actions/artifacts/{((long)artifact["id"]!)}/zip", completionOption: HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();
        using var archiveBytes = new MemoryStream(buffer: await ReadBoundedAsync(stream: await response.Content.ReadAsStreamAsync()));
        using var archive = new ZipArchive(mode: ZipArchiveMode.Read, stream: archiveBytes);
        var entry = archive.Entries.Single(predicate: static item => (item.FullName == "format.json"));
        using var stream = entry.Open();
        var report = JsonNode.Parse(utf8Json: await ReadBoundedAsync(stream: stream))!;

        if ((((string?)report["head"]) != head) || (((string?)report["base"]) != ((string?)pull["base"]!["sha"]))) {
            Report(message: $"PR #{number}'s base or head changed; the stale result was discarded.");
            return;
        }
        var files = report["files"]!.AsArray();

        if (files.Count == 0) { Report(message: $"PR #{number} is formatted; no commit needed."); return; }
        if (files.Count > 500) { throw new InvalidDataException(message: "Formatting batch exceeds 500 files; split this PR."); }
        var allowed = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var page = 1; (page <= 30); ++page) {
            var changed = (await GetAsync(path: $"{prefix}/pulls/{number}/files?per_page=100&page={page}")).AsArray();

            foreach (var item in changed) {
                if (((string?)item!["status"]) != "removed") { allowed.Add(item: ((string)item["filename"]!)); }
            }
            if (changed.Count < 100) { break; }
        }
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var additions = new JsonArray();
        var trees = new Dictionary<string, JsonNode>(comparer: StringComparer.Ordinal) {
            [""] = await GetAsync(path: $"{prefix}/git/trees/{head}"),
        };

        foreach (var item in files) {
            var path = ((string)item!["path"]!);

            if (!allowed.Contains(item: path) || !seen.Add(item: path) || !IsSourcePath(path: path)) {
                throw new InvalidDataException(message: "The artifact contains a duplicate, excluded, or non-PR source path.");
            }
            var contents = ((string)item["contents"]!);
            var bytes = Convert.FromBase64String(s: contents);

            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes: bytes);
            // The Contents API can follow a symlink and describe its target as a file. Inspect Git's
            // tree modes instead, including every parent directory, without a truncated recursive walk.
            var segments = path.Split('/');
            var directory = "";

            for (var index = 0; (index < segments.Length); ++index) {
                var tree = trees[directory];

                if (((bool?)tree["truncated"]) == true) { throw new InvalidDataException(message: "GitHub returned an incomplete tree."); }
                var node = tree["tree"]!.AsArray().Single(predicate: node => (((string?)node!["path"]) == segments[index]))!;

                if (index == (segments.Length - 1)) {
                    if ((((string?)node["mode"]) is not ("100644" or "100755")) || (((string?)node["type"]) != "blob")) {
                        throw new InvalidDataException(message: "Only ordinary source files may be replaced.");
                    }
                } else {
                    if (((string?)node["type"]) != "tree") { throw new InvalidDataException(message: "Source parents must be ordinary directories."); }
                    directory += ("/" + segments[index]);
                    if (!trees.ContainsKey(key: directory)) { trees[directory] = await GetAsync(path: $"{prefix}/git/trees/{((string)node["sha"]!)}"); }
                }
            }
            additions.Add(item: new JsonObject { ["path"] = path, ["contents"] = contents });
        }
        var mutation = new JsonObject {
            ["query"] = "mutation($input: CreateCommitOnBranchInput!) { createCommitOnBranch(input: $input) { commit { oid } } }",
            ["variables"] = new JsonObject {
                ["input"] = new JsonObject {
                    ["branch"] = new JsonObject { ["repositoryNameWithOwner"] = repository, ["refName"] = branch },
                    ["expectedHeadOid"] = head,
                    ["message"] = new JsonObject { ["headline"] = "style: apply Puck formatting", ["body"] = $"Puck-Format-Run: {runId}" },
                    ["fileChanges"] = new JsonObject { ["additions"] = additions },
                },
            },
        };
        var result = await PostAsync(path: graphUrl, value: mutation);

        if (result?["errors"] is not null) { throw new InvalidOperationException(message: "GitHub declined the formatting commit (the branch may have advanced or be protected)."); }
        var committed = ((string)result!["data"]!["createCommitOnBranch"]!["commit"]!["oid"]!);

        Report(message: $"Applied {files.Count} formatted file(s) to PR #{number} as {committed}.");
        await DispatchAsync(prefix: prefix, branch: branch, baseSha: ((string)pull["base"]!["sha"]!));
    }

    private static bool IsSourcePath(string path) =>
        (path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".cs")
        && !path.Contains(value: '\\') && !path.Contains(value: ':')
        && !path.Split('/').Any(predicate: static part => (part is "" or "." or ".." or "experimental" or "obj" or "bin" or "artifacts" or ".tmp" or ".git" or "node_modules" or "avm-temp" or "publish"))
        && !path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".g.cs")
        && !path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".generated.cs"));
    private async Task DispatchAsync(string prefix, string branch, string baseSha) {
        // GITHUB_TOKEN pushes do not provide unattended PR checks. Explicit dispatches run with
        // each workflow's own read-only permissions and validate the new head without cloud deployment.
        await PostAsync(path: $"{prefix}/actions/workflows/format.yml/dispatches", value: new JsonObject { ["ref"] = branch, ["inputs"] = new JsonObject { ["base"] = baseSha } });
        await PostAsync(path: $"{prefix}/actions/workflows/azure.yml/dispatches", value: new JsonObject { ["ref"] = branch, ["inputs"] = new JsonObject { ["deploy"] = "false" } });
        Report(message: "Dispatched formatting and the shared Azure validation graph, including packages and documentation, for the updated branch.");
    }
    private async Task<JsonNode> GetAsync(string path) {
        using var response = await m_client.GetAsync(requestUri: path);

        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(json: await response.Content.ReadAsStringAsync())!;
    }
    private async Task<JsonNode?> PostAsync(string path, JsonObject value) {
        using var content = new StringContent(content: value.ToJsonString(), encoding: Encoding.UTF8, mediaType: "application/json");
        using var response = await m_client.PostAsync(content: content, requestUri: path);

        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();

        return (string.IsNullOrWhiteSpace(value: text) ? null : JsonNode.Parse(json: text));
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream) {
        using (stream) {
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;

            while ((read = await stream.ReadAsync(buffer: buffer)) != 0) {
                if ((output.Length + read) > MaximumBytes) { throw new InvalidDataException(message: "Formatting artifact exceeds 16 MiB."); }
                output.Write(buffer: buffer, count: read, offset: 0);
            }
            return output.ToArray();
        }
    }
    private void Report(string message) => m_report(message);
}
