using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.World.Server;
using Puck.World.Silo;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private sealed record AzureWorldReleaseDeployment(WorldReleaseManifest Manifest, WorldReleaseDeploymentConfiguration Configuration) {
        public JsonObject Parameters => Configuration.Parameters;
        public string Image => Text(Parameters["release"]);
        public int HealthPort => Parameters["configuration"]!["lifecycle"]!["healthPort"]!.GetValue<int>();
        public int ShutdownSeconds => Parameters["configuration"]!["lifecycle"]!["shutdownSeconds"]!.GetValue<int>();
    }

    /// <summary>Runs the shared release transaction against the existing single-worker VMSS and private store.</summary>
    private sealed class AzureWorldReleaseRuntime(
        string resourceGroup, string scaleSet, string temporary,
        WorldReleaseGroupStore groups, WorldAuthorityBlobStore authority,
        AzureWorldReleaseDeployment? source, AzureWorldReleaseDeployment target,
        Func<CancellationToken, Task> initializeBootstrap) : IWorldReleaseRuntime {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly WorldAuthorityIdentity[] m_identities = target.Manifest.Definitions.Keys.Select(ParseIdentity).ToArray();

        public async Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(operation, [WorldReleaseOperationPhase.Drain], cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> roots;
            if (source is null) {
                if ((await WorkersAsync(resourceGroup, scaleSet).ConfigureAwait(false)).Length != 0) {
                    return new(false, "bootstrap refuses an existing worker without an adopted managed release");
                }
                await initializeBootstrap(cancellationToken).ConfigureAwait(false);
                var captured = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var identity in m_identities) {
                    var saved = await authority.CaptureRecoveryRootAsync(identity, operation.PendingOperationId!.Value, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException("bootstrap recovery root is missing");
                    if (saved.Root.FenceToken != Guid.Empty) { throw new InvalidDataException("bootstrap cannot capture an owned world"); }
                    captured[$"{identity.Owner:D}/{identity.World}"] = saved.Pin;
                }
                roots = captured;
            } else {
                var retained = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var identity in m_identities) {
                    var saved = await authority.FindRecoveryRootAsync(identity, operation.PendingOperationId!.Value, cancellationToken).ConfigureAwait(false);
                    if (saved is { } root) { retained[$"{identity.Owner:D}/{identity.World}"] = root.Pin; }
                }
                var workers = await WorkersAsync(resourceGroup, scaleSet).ConfigureAwait(false);
                if (workers.Length > 1) { throw new InvalidOperationException("release drain requires one authoritative worker"); }
                var worker = workers.Length == 1 ? Text(workers[0]!["name"]) : null;
                if (retained.Count == m_identities.Length) {
                    // The drain endpoint writes these only after the whole host is frozen. A retry after source
                    // stop must reuse them; it must neither contact a stopped endpoint nor recapture newer state.
                    roots = retained;
                } else {
                    if (worker is null) { throw new InvalidOperationException("source disappeared before its complete drain roots were retained"); }
                    var response = await RequestAsync(worker, source, "POST", $"/release/drain/{operation.PendingOperationId:D}").ConfigureAwait(false);
                    roots = response.Deserialize<Dictionary<string, string>>(Json) ?? throw new InvalidDataException("source drain returned no protected roots");
                }
                ValidateRoots(roots);
                // The drain endpoint retains the frozen host for retries. Stop only after its immutable roots exist.
                await RequireAsync(operation, [WorldReleaseOperationPhase.Drain], cancellationToken).ConfigureAwait(false);
                if (worker is not null) {
                    var process = await InspectAsync(worker).ConfigureAwait(false);
                    if (process["image"]?.GetValue<string>() is { } image && image != source.Image) {
                        throw new InvalidOperationException("source drain found an unknown image; refusing to stop another deployment");
                    }
                    await WorldGuestAsync(resourceGroup, "systemctl stop puck-world.service", worker).ConfigureAwait(false);
                }
            }
            return new(true, "source drained and protected roots retained", roots);
        }

        public async Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(operation, [WorldReleaseOperationPhase.Activate, WorldReleaseOperationPhase.Verify, WorldReleaseOperationPhase.Commit], cancellationToken).ConfigureAwait(false);
            await EnsureWorkerAsync(target, cancellationToken).ConfigureAwait(false);
            return new(true, "exact target is running and privately healthy");
        }

        public async Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(operation, [WorldReleaseOperationPhase.Recover], cancellationToken).ConfigureAwait(false);
            var workers = await WorkersAsync(resourceGroup, scaleSet).ConfigureAwait(false);
            if (workers.Length > 1) { throw new InvalidOperationException("release recovery requires one worker"); }
            if (workers.Length == 1) {
                var worker = Text(workers[0]!["name"]);
                var process = await InspectAsync(worker).ConfigureAwait(false);
                var image = process["image"]?.GetValue<string>();
                if (image is not null && image != target.Image && image != source?.Image) {
                    throw new InvalidOperationException("the worker contains an unknown image; refusing to stop another deployment");
                }
                // A failed private candidate may be unable to checkpoint. Its protected source roots are already durable.
                await WorldGuestAsync(resourceGroup, "systemctl stop puck-world.service || true\nif docker inspect puck-world >/dev/null 2>&1; then docker rm -f puck-world; fi", worker).ConfigureAwait(false);
            }
            return new(true, "private worker stopped");
        }

        public async Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(operation, [WorldReleaseOperationPhase.Verify, WorldReleaseOperationPhase.Commit], cancellationToken).ConfigureAwait(false);
            await CheckWorkerAsync(await WorkerAsync().ConfigureAwait(false), target).ConfigureAwait(false);
            return new(true, "exact candidate identity and private persistence health verified");
        }

        public async Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(operation, [WorldReleaseOperationPhase.Verify], cancellationToken).ConfigureAwait(false);
            var response = await RequestAsync(await WorkerAsync().ConfigureAwait(false), target, "GET", $"/release/fences/{operation.PendingOperationId:D}").ConfigureAwait(false);
            var census = response.Deserialize<WorldReleaseWorkerFence[]>(Json) ?? throw new InvalidDataException("candidate returned no fence census");
            var result = census.Select(row => (Identity: new WorldAuthorityIdentity(row.Owner, SafeName.Parse(row.World)), Fence: new WorldAuthorityFence(row.Epoch, row.Token, row.RootVersion))).ToArray();
            if (result.Length != m_identities.Length || !result.Select(row => row.Identity).ToHashSet().SetEquals(m_identities)) {
                throw new InvalidDataException("candidate fence census does not match the release inventory");
            }
            _ = WorldReleaseFenceClaim.Compute(result);
            return result;
        }

        public Task<WorldReleaseRuntimePublication> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            PublishAsync(operation, target, WorldReleaseOperationPhase.Commit, cancellationToken);

        public async Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(operation, [WorldReleaseOperationPhase.Recover], cancellationToken).ConfigureAwait(false);
            ValidateRoots(operation.RecoveryRoots);
            // Validate the entire protected inventory before the first maintenance takeover.
            foreach (var identity in m_identities) {
                _ = await authority.LoadRecoveryRootAsync(identity, operation.RecoveryRoots[$"{identity.Owner:D}/{identity.World}"], operation.PendingOperationId!.Value, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("a protected source root is missing");
            }
            foreach (var identity in m_identities) {
                await RequireAsync(operation, [WorldReleaseOperationPhase.Recover], cancellationToken).ConfigureAwait(false);
                var fence = await authority.AcquireActivationAsync(identity, cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException("source recovery could not acquire maintenance ownership");
                var restored = await authority.RestoreRecoveryRootAsync(identity, operation.RecoveryRoots[$"{identity.Owner:D}/{identity.World}"], operation.PendingOperationId!.Value, fence, cancellationToken).ConfigureAwait(false);
                if (!restored.Ok) { throw new IOException($"source recovery failed for '{identity.World}': {restored.Detail}"); }
            }
            return new(true, "protected source roots restored under fresh fences");
        }

        public async Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(operation, [WorldReleaseOperationPhase.RecoverActivate], cancellationToken).ConfigureAwait(false);
            await EnsureWorkerAsync(source ?? throw new InvalidOperationException("bootstrap has no source worker"), cancellationToken).ConfigureAwait(false);
            return new(true, "restored source is privately healthy");
        }

        public Task<WorldReleaseRuntimePublication> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            PublishAsync(operation, source ?? throw new InvalidOperationException("bootstrap has no source worker"), WorldReleaseOperationPhase.RecoverActivate, cancellationToken);

        private async Task<WorldReleaseRuntimePublication> PublishAsync(WorldReleaseGroupRecord operation, AzureWorldReleaseDeployment deployment, WorldReleaseOperationPhase phase, CancellationToken cancellationToken) {
            await RequireAsync(operation, [phase], cancellationToken).ConfigureAwait(false);
            var response = await RequestAsync(await WorkerAsync().ConfigureAwait(false), deployment, "POST", $"/release/publish/{operation.PendingOperationId:D}").ConfigureAwait(false);
            var published = response.Deserialize<WorldReleaseWorkerStatus>(Json);
            return published is { AdmissionOpen: true } && published.Group == scaleSet && published.Release == deployment.Manifest.Identity && published.OperationId == operation.PendingOperationId
                ? WorldReleaseRuntimePublication.Opened : WorldReleaseRuntimePublication.Refused;
        }

        private async Task EnsureWorkerAsync(AzureWorldReleaseDeployment deployment, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            var workers = await StableWorkersAsync(resourceGroup, scaleSet).ConfigureAwait(false);
            if (workers.Length > 1) { throw new InvalidOperationException("release requires one authoritative worker"); }
            if (workers.Length == 1) {
                var worker = Text(workers[0]!["name"]);
                var process = await InspectAsync(worker).ConfigureAwait(false);
                if (process["running"]?.GetValue<bool>() == true && process["image"]?.GetValue<string>() == deployment.Image) {
                    await CheckWorkerAsync(worker, deployment).ConfigureAwait(false);
                    return;
                }
                if (process["running"]?.GetValue<bool>() == true) { throw new InvalidOperationException("another image is still running after the drain boundary"); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(temporary, "release-compute.json");
            WriteParameters(path, deployment.Parameters);
            var template = Path.Combine(temporary, "release-compute-template.json");
            CliFiles.WriteJson(template, deployment.Configuration.ComputeTemplate);
            await ApplyWorldComputeAsync(resourceGroup, path, scaleSet, template).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await CheckWorkerAsync(await WorkerAsync().ConfigureAwait(false), deployment).ConfigureAwait(false);
        }

        private async Task CheckWorkerAsync(string worker, AzureWorldReleaseDeployment deployment) {
            var process = await InspectAsync(worker).ConfigureAwait(false);
            if (process["running"]?.GetValue<bool>() != true || process["image"]?.GetValue<string>() != deployment.Image) {
                throw new InvalidDataException("the running container does not match the retained exact image");
            }
            var status = (await RequestAsync(worker, deployment, "GET", "/release/status").ConfigureAwait(false)).Deserialize<WorldReleaseWorkerStatus>(Json);
            if (status?.Release != deployment.Manifest.Identity || status.Group != scaleSet) { throw new InvalidDataException("the running worker does not match the retained release identity"); }
            await WorldGuestAsync(resourceGroup, $"curl --fail --silent --show-error --max-time 30 http://127.0.0.1:{deployment.HealthPort}/private-healthz", worker).ConfigureAwait(false);
        }

        private Task<JsonNode> RequestAsync(string worker, AzureWorldReleaseDeployment deployment, string method, string path) =>
            WorldGuestJsonAsync(resourceGroup, $"curl --fail --silent --show-error --max-time {deployment.ShutdownSeconds} -X {method} http://127.0.0.1:{deployment.HealthPort}{path}", worker);

        private Task<JsonNode> InspectAsync(string worker) => WorldGuestJsonAsync(resourceGroup,
            "if docker inspect puck-world >/dev/null 2>&1; then docker inspect --format '{\"image\":{{json .Config.Image}},\"running\":{{.State.Running}}}' puck-world; else printf '%s' '{\"image\":null,\"running\":false}'; fi", worker);

        private async Task<string> WorkerAsync() {
            var workers = await WorkersAsync(resourceGroup, scaleSet).ConfigureAwait(false);
            if (workers.Length != 1) { throw new InvalidOperationException("release requires exactly one worker"); }
            return Text(workers[0]!["name"]);
        }

        private async Task RequireAsync(WorldReleaseGroupRecord expected, WorldReleaseOperationPhase[] phases, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            var current = (await groups.LoadAsync(scaleSet, cancellationToken).ConfigureAwait(false))?.Record;
            if (current is null || current.PendingOperationId != expected.PendingOperationId || current.PendingSourceRelease != source?.Manifest.Identity ||
                current.PendingTargetRelease != target.Manifest.Identity || current.PendingPhase is not { } phase || !phases.Contains(phase) ||
                current.RecoveryRoots.Count != expected.RecoveryRoots.Count || expected.RecoveryRoots.Any(root => !current.RecoveryRoots.TryGetValue(root.Key, out var pin) || pin != root.Value)) {
                throw new InvalidOperationException("the durable release operation changed before the Azure worker effect");
            }
        }

        private void ValidateRoots(IReadOnlyDictionary<string, string> roots) {
            if (roots.Count != m_identities.Length || !roots.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(m_identities.Select(identity => $"{identity.Owner:D}/{identity.World}"))) {
                throw new InvalidDataException("protected source roots do not match the release inventory");
            }
        }
        private static WorldAuthorityIdentity ParseIdentity(string value) {
            var pieces = value.Split('/');
            if (pieces.Length != 2 || !Guid.TryParseExact(pieces[0], "D", out var owner) || owner == Guid.Empty) { throw new InvalidDataException("release has an invalid stable world identity"); }
            return new(owner, SafeName.Parse(pieces[1]));
        }
    }

    private static async Task<JsonNode> WorldGuestJsonAsync(string group, string script, string worker) {
        var marker = "puck-json-" + Guid.NewGuid().ToString("N") + ":";
        var path = Path.Combine(Path.GetTempPath(), "puck-release-request-" + Guid.NewGuid().ToString("N") + ".sh");
        try {
            File.WriteAllText(path, $"set -eu\npayload=$(\n{script}\n)\nprintf '%s' '{marker}'\nprintf '%s' \"$payload\" | base64 -w0\nprintf '\\n'\n");
            var response = await AzJsonAsync("vm", "run-command", "invoke", "-g", group, "-n", worker, "--command-id", "RunShellScript", "--scripts", "@" + path, "-o", "json").ConfigureAwait(false);
            var message = string.Join("\n", response["value"]!.AsArray().Select(row => Text(row!["message"])));
            var start = message.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) { throw new IOException("world worker did not return a complete release response"); }
            var encoded = message[(start + marker.Length)..].Split('\n', 2)[0].Trim();
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded))) ?? throw new InvalidDataException("world worker returned an empty release response");
        } finally { File.Delete(path); }
    }
}
