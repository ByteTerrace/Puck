using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.World.Server;
using Puck.World.Silo;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private sealed record AzureWorldReleaseDeployment(WorldReleaseManifest Manifest, WorldReleaseDeploymentConfiguration Configuration) {
        public int HealthPort => Parameters["configuration"]!["lifecycle"]!["healthPort"]!.GetValue<int>();
        public string Image => Text(value: Parameters["release"]);
        public JsonObject Parameters => Configuration.Parameters;
        public string Priority => WorldPriority(compute: Parameters["configuration"]!["compute"]!);
        public int ShutdownSeconds => Parameters["configuration"]!["lifecycle"]!["shutdownSeconds"]!.GetValue<int>();
    }
    /// <summary>Runs the shared release transaction against the existing single-worker VMSS and private store.</summary>
    private sealed class AzureWorldReleaseRuntime(
        string resourceGroup, string scaleSet, string temporary, string endpoint, string clientId,
        WorldReleaseGroupStore groups, WorldAuthorityBlobStore authority, WorldReleaseArchive archive,
        WorldReleaseRestore restores,
        AzureWorldReleaseDeployment? source, AzureWorldReleaseDeployment target,
        Func<CancellationToken, Task> initializeBootstrap, TimeProvider clock) : IWorldReleaseRuntime {
        private static readonly JsonSerializerOptions Json = new(defaults: JsonSerializerDefaults.Web);

        private readonly WorldAuthorityIdentity[] m_identities = target.Manifest.Definitions.Keys.Select(selector: ParseIdentity).ToArray();

        private async Task CheckWorkerAsync(string worker, AzureWorldReleaseDeployment deployment) {
            var process = await InspectAsync(worker: worker).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (process["running"]?.GetValue<bool>() != true) ||
                (process["image"]?.GetValue<string>() != deployment.Image)
            ) {
                throw new InvalidDataException(message: "the running container does not match the retained exact image");
            }
            var status = (await RequestAsync(
                worker,
                deployment,
                "GET",
                "/release/status"
            ).ConfigureAwait(continueOnCapturedContext: false)).Deserialize<WorldReleaseWorkerStatus>(options: Json);

            if (
                (status?.Release != deployment.Manifest.Identity) ||
                (status.Group != scaleSet)
            ) { throw new InvalidDataException(message: "the running worker does not match the retained release identity"); }
            await WorldGuestAsync(
                group: resourceGroup,
                script: $"curl --fail --silent --show-error --max-time 30 http://127.0.0.1:{deployment.HealthPort}/private-healthz",
                worker: worker
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        private async Task EnsureWorkerAsync(AzureWorldReleaseDeployment deployment, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            var workers = await StableWorkersAsync(
                clock: clock,
                group: resourceGroup,
                scaleSet: scaleSet
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (workers.Length > 1) { throw new InvalidOperationException(message: "release requires one authoritative worker"); }
            if (workers.Length == 1) {
                var worker = Text(value: workers[0]!["name"]);
                var process = await InspectAsync(worker: worker).ConfigureAwait(continueOnCapturedContext: false);

                if (
                    (process["running"]?.GetValue<bool>() == true) &&
                    (process["image"]?.GetValue<string>() == deployment.Image)
                ) {
                    await CheckWorkerAsync(
                        deployment: deployment,
                        worker: worker
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return;
                }
                if (process["running"]?.GetValue<bool>() == true) { throw new InvalidOperationException(message: "another image is still running after the drain boundary"); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(
                path1: temporary,
                path2: "release-compute.json"
            );

            WriteParameters(
                path: path,
                values: deployment.Parameters
            );
            var template = Path.Combine(
                path1: temporary,
                path2: "release-compute-template.json"
            );

            CliFiles.WriteJson(
                path: template,
                value: deployment.Configuration.ComputeTemplate
            );
            await ApplyWorldComputeAsync(
                clock: clock,
                group: resourceGroup,
                parameters: path,
                priority: deployment.Priority,
                retainedTemplate: template,
                scaleSet: scaleSet
            ).ConfigureAwait(continueOnCapturedContext: false);
            cancellationToken.ThrowIfCancellationRequested();
            await CheckWorkerAsync(
                await WorkerAsync().ConfigureAwait(continueOnCapturedContext: false),
                deployment
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        private async Task<string> GuestGuardAsync(WorldReleaseGroupRecord operation, WorldReleaseOperationPhase[] phases, CancellationToken token) {
            await RequireAsync(
                cancellationToken: token,
                expected: operation,
                phases: phases
            ).ConfigureAwait(continueOnCapturedContext: false);
            var request = new JsonObject {
                ["endpoint"] = endpoint,
                ["owner"] = operation.Owner.ToString(format: "D"),
                ["group"] = scaleSet,
                ["clientId"] = clientId,
                ["operation"] = operation.PendingOperationId!.Value.ToString(format: "D"),
                ["source"] = operation.PendingSourceRelease,
                ["target"] = operation.PendingTargetRelease,
                ["phases"] = new JsonArray(phases.Select(selector: phase => JsonValue.Create(((int)phase))).ToArray()),
            };

            return ((WorldReleaseGuestLock + WorldReleaseGuestGuard(request: request)) + "\n");
        }
        private Task<JsonNode> InspectAsync(string worker) => WorldGuestJsonAsync(
            group: resourceGroup,
            script: "if docker inspect puck-world >/dev/null 2>&1; then docker inspect --format '{\"image\":{{json .Config.Image}},\"running\":{{.State.Running}}}' puck-world; else printf '%s' '{\"image\":null,\"running\":false}'; fi",
            worker: worker
        );
        private static WorldAuthorityIdentity ParseIdentity(string value) {
            var pieces = value.Split('/');

            if (
                (pieces.Length != 2) ||
                !Guid.TryParseExact(
                pieces[0],
                "D",
                out var owner
            ) ||
                (owner == Guid.Empty)
            ) { throw new InvalidDataException(message: "release has an invalid stable world identity"); }
            return new(
                Owner: owner,
                World: SafeName.Parse(candidate: pieces[1])
            );
        }
        private async Task<WorldReleaseRuntimePublication> PublishAsync(WorldReleaseGroupRecord operation, AzureWorldReleaseDeployment deployment, WorldReleaseOperationPhase phase, CancellationToken cancellationToken) {
            await RequireAsync(
                cancellationToken: cancellationToken,
                expected: operation,
                phases: [phase]
            ).ConfigureAwait(continueOnCapturedContext: false);
            var response = await RequestAsync(
                await WorkerAsync().ConfigureAwait(continueOnCapturedContext: false),
                deployment,
                "POST",
                $"/release/publish/{operation.PendingOperationId:D}",
                await GuestGuardAsync(
                    operation: operation,
                    phases: [phase],
                    token: cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false)
            ).ConfigureAwait(continueOnCapturedContext: false);
            var published = response.Deserialize<WorldReleaseWorkerStatus>(options: Json);

            return (((published is { AdmissionOpen: true }) && (published.Group == scaleSet) && (published.Release == deployment.Manifest.Identity) && (published.OperationId == operation.PendingOperationId))
                ? WorldReleaseRuntimePublication.Opened
                : WorldReleaseRuntimePublication.Refused
            );
        }
        private Task<JsonNode> RequestAsync(string worker, AzureWorldReleaseDeployment deployment, string method, string path, string guard = "") =>
            WorldGuestJsonAsync(
                group: resourceGroup,
                script: (guard + $"curl --fail --silent --show-error --max-time {deployment.ShutdownSeconds} -X {method} http://127.0.0.1:{deployment.HealthPort}{path}"),
                worker: worker
            );
        private async Task RequireAsync(WorldReleaseGroupRecord expected, WorldReleaseOperationPhase[] phases, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            var current = (await groups.LoadAsync(
                cancellationToken: cancellationToken,
                deploymentGroup: scaleSet
            ).ConfigureAwait(continueOnCapturedContext: false))?.Record;

            if (
                (current is null) ||
                (current.PendingOperationId != expected.PendingOperationId) ||
                (current.PendingSourceRelease != source?.Manifest.Identity) ||
                (current.PendingTargetRelease != target.Manifest.Identity) ||
                (current.PendingPhase is not { } phase) ||
                !phases.Contains(phase) ||
                (current.RecoveryRoots.Count != expected.RecoveryRoots.Count) ||
                expected.RecoveryRoots.Any(predicate: root => (!current.RecoveryRoots.TryGetValue(
                key: root.Key,
                value: out var pin
            ) || (pin != root.Value)))
            ) {
                throw new InvalidOperationException(message: "the durable release operation changed before the Azure worker effect");
            }
        }
        private void ValidateRoots(IReadOnlyDictionary<string, string> roots) {
            if (
                (roots.Count != m_identities.Length) ||
                !roots.Keys.ToHashSet(comparer: StringComparer.Ordinal).SetEquals(other: m_identities.Select(selector: identity => $"{identity.Owner:D}/{identity.World}"))
            ) {
                throw new InvalidDataException(message: "protected source roots do not match the release inventory");
            }
        }
        private async Task<string> WorkerAsync() {
            var workers = await WorkersAsync(
                group: resourceGroup,
                scaleSet: scaleSet
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (workers.Length != 1) { throw new InvalidOperationException(message: "release requires exactly one worker"); }
            return Text(value: workers[0]!["name"]);
        }

        public async Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(
                cancellationToken: cancellationToken,
                expected: operation,
                phases: [WorldReleaseOperationPhase.Drain]
            ).ConfigureAwait(continueOnCapturedContext: false);
            IReadOnlyDictionary<string, string> roots;

            if (source is null) {
                if ((await WorkersAsync(
                    group: resourceGroup,
                    scaleSet: scaleSet
                ).ConfigureAwait(continueOnCapturedContext: false)).Length != 0) {
                    return new(
                        false,
                        "bootstrap refuses an existing worker without an adopted managed release"
                    );
                }
                await initializeBootstrap(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                var captured = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

                foreach (var identity in m_identities) {
                    var saved = (await authority.CaptureRecoveryRootAsync(
                        identity,
                        operation.PendingOperationId!.Value,
                        cancellationToken
                    ).ConfigureAwait(continueOnCapturedContext: false)
                        ?? throw new InvalidDataException(message: "bootstrap recovery root is missing"));

                    if (saved.Root.FenceToken != Guid.Empty) { throw new InvalidDataException(message: "bootstrap cannot capture an owned world"); }
                    captured[$"{identity.Owner:D}/{identity.World}"] = saved.Pin;
                }
                roots = captured;
            } else {
                var retained = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

                foreach (var identity in m_identities) {
                    var saved = await authority.FindRecoveryRootAsync(
                        identity,
                        operation.PendingOperationId!.Value,
                        cancellationToken
                    ).ConfigureAwait(continueOnCapturedContext: false);

                    if (saved is { } root) { retained[$"{identity.Owner:D}/{identity.World}"] = root.Pin; }
                }
                var workers = await WorkersAsync(
                    group: resourceGroup,
                    scaleSet: scaleSet
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (workers.Length > 1) { throw new InvalidOperationException(message: "release drain requires one authoritative worker"); }
                var worker = ((workers.Length == 1)
                    ? Text(value: workers[0]!["name"])
                    : null
                );

                if (retained.Count == m_identities.Length) {
                    // The drain endpoint writes these only after the whole host is frozen. A retry after source
                    // stop must reuse them; it must neither contact a stopped endpoint nor recapture newer state.
                    roots = retained;
                } else {
                    if (worker is null) { throw new InvalidOperationException(message: "source disappeared before its complete drain roots were retained"); }
                    var response = await RequestAsync(
                        worker,
                        source,
                        "POST",
                        $"/release/drain/{operation.PendingOperationId:D}",
                        await GuestGuardAsync(
                            operation: operation,
                            phases: [WorldReleaseOperationPhase.Drain],
                            token: cancellationToken
                        ).ConfigureAwait(continueOnCapturedContext: false)
                    ).ConfigureAwait(continueOnCapturedContext: false);

                    roots = (response.Deserialize<Dictionary<string, string>>(options: Json) ?? throw new InvalidDataException(message: "source drain returned no protected roots"));
                }
                ValidateRoots(roots: roots);
                // The drain endpoint retains the frozen host for retries. Stop only after its immutable roots exist.
                await RequireAsync(
                    cancellationToken: cancellationToken,
                    expected: operation,
                    phases: [WorldReleaseOperationPhase.Drain]
                ).ConfigureAwait(continueOnCapturedContext: false);
                if (worker is not null) {
                    var process = await InspectAsync(worker: worker).ConfigureAwait(continueOnCapturedContext: false);

                    if (
                        (process["image"]?.GetValue<string>() is { } image) &&
                        (image != source.Image)
                    ) {
                        throw new InvalidOperationException(message: "source drain found an unknown image; refusing to stop another deployment");
                    }
                    await WorldGuestAsync(
                        group: resourceGroup,
                        script: (await GuestGuardAsync(
                            operation: operation,
                            phases: [WorldReleaseOperationPhase.Drain],
                            token: cancellationToken
                        ).ConfigureAwait(continueOnCapturedContext: false) +
                        "systemctl stop puck-world.service"),
                        worker: worker
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }
            }
            return new(
                Detail: "source drained and protected roots retained",
                RecoveryRoots: roots,
                Succeeded: true
            );
        }
        public Task<WorldReleaseRuntimePublication> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            PublishAsync(
                cancellationToken: cancellationToken,
                deployment: target,
                operation: operation,
                phase: WorldReleaseOperationPhase.Commit
            );
        public Task<WorldReleaseRuntimePublication> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            PublishAsync(
                operation,
                (source ?? throw new InvalidOperationException(message: "bootstrap has no source worker")),
                WorldReleaseOperationPhase.RecoverActivate,
                cancellationToken
            );
        public async Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(
                cancellationToken: cancellationToken,
                expected: operation,
                phases: [WorldReleaseOperationPhase.Verify]
            ).ConfigureAwait(continueOnCapturedContext: false);
            var response = await RequestAsync(
                await WorkerAsync().ConfigureAwait(continueOnCapturedContext: false),
                target,
                "GET",
                $"/release/fences/{operation.PendingOperationId:D}"
            ).ConfigureAwait(continueOnCapturedContext: false);
            var census = (response.Deserialize<WorldReleaseWorkerFence[]>(options: Json) ?? throw new InvalidDataException(message: "candidate returned no fence census"));
            var result = census.Select(selector: row => (Identity: new WorldAuthorityIdentity(
                Owner: row.Owner,
                World: SafeName.Parse(candidate: row.World)
            ), Fence: new WorldAuthorityFence(
                row.Epoch,
                row.Token,
                row.RootVersion
            ))).ToArray();

            if (
                (result.Length != m_identities.Length) ||
                !result.Select(selector: row => row.Identity).ToHashSet().SetEquals(other: m_identities)
            ) {
                throw new InvalidDataException(message: "candidate fence census does not match the release inventory");
            }
            _ = WorldReleaseFenceClaim.Compute(fences: result);
            return result;
        }
        public async Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(
                cancellationToken: cancellationToken,
                expected: operation,
                phases: [WorldReleaseOperationPhase.Recover]
            ).ConfigureAwait(continueOnCapturedContext: false);
            ValidateRoots(roots: operation.RecoveryRoots);
            // Validate the entire protected inventory before the first maintenance takeover.
            foreach (var identity in m_identities) {
                _ = (await authority.LoadRecoveryRootAsync(
                    identity,
                    operation.RecoveryRoots[$"{identity.Owner:D}/{identity.World}"],
                    operation.PendingOperationId!.Value,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false)
                    ?? throw new InvalidDataException(message: "a protected source root is missing"));
            }
            foreach (var identity in m_identities) {
                await RequireAsync(
                    cancellationToken: cancellationToken,
                    expected: operation,
                    phases: [WorldReleaseOperationPhase.Recover]
                ).ConfigureAwait(continueOnCapturedContext: false);
                var fence = (await authority.AcquireActivationAsync(
                    cancellationToken: cancellationToken,
                    identity: identity
                ).ConfigureAwait(continueOnCapturedContext: false)
                    ?? throw new IOException(message: "source recovery could not acquire maintenance ownership"));
                var restored = await authority.RestoreRecoveryRootAsync(
                    identity,
                    operation.RecoveryRoots[$"{identity.Owner:D}/{identity.World}"],
                    operation.PendingOperationId!.Value,
                    fence,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!restored.Ok) { throw new IOException(message: $"source recovery failed for '{identity.World}': {restored.Detail}"); }
            }
            return new(
                true,
                "protected source roots restored under fresh fences"
            );
        }
        public async Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(
                cancellationToken: cancellationToken,
                expected: operation,
                phases: [WorldReleaseOperationPhase.Activate, WorldReleaseOperationPhase.Verify, WorldReleaseOperationPhase.Commit]
            ).ConfigureAwait(continueOnCapturedContext: false);
            if (
                (operation.PendingPhase == WorldReleaseOperationPhase.Activate) &&
                (source is not null)
            ) {
                if (operation.RestorePoint is not null) {
                    await restores.ApplyAsync(
                        operation: operation,
                        token: cancellationToken
                    ).ConfigureAwait(continueOnCapturedContext: false);
                } else {
                    foreach (var identity in m_identities) {
                        var key = $"{identity.Owner:D}/{identity.World}";

                        if (source.Manifest.Definitions[key] == target.Manifest.Definitions[key]) { continue; }
                        var publication = await authority.PrepareReleaseMetadataAsync(
                            identity,
                            operation,
                            source.Manifest,
                            target.Manifest,
                            archive,
                            cancellationToken,
                            CliWorldVocabulary.EnsureInstalled()
                        ).ConfigureAwait(continueOnCapturedContext: false);

                        if (!publication.Ok) {
                            return new(
                                false,
                                $"'{identity.World}' release definition publication refused: {publication.Detail}"
                            );
                        }
                    }
                }
            }
            await EnsureWorkerAsync(
                cancellationToken: cancellationToken,
                deployment: target
            ).ConfigureAwait(continueOnCapturedContext: false);
            return new(
                true,
                "exact target is running and privately healthy"
            );
        }
        public async Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(
                cancellationToken: cancellationToken,
                expected: operation,
                phases: [WorldReleaseOperationPhase.RecoverActivate]
            ).ConfigureAwait(continueOnCapturedContext: false);
            await EnsureWorkerAsync(
                (source ?? throw new InvalidOperationException(message: "bootstrap has no source worker")),
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
            return new(
                true,
                "restored source is privately healthy"
            );
        }
        public async Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(
                cancellationToken: cancellationToken,
                expected: operation,
                phases: [WorldReleaseOperationPhase.Recover]
            ).ConfigureAwait(continueOnCapturedContext: false);
            var workers = await WorkersAsync(
                group: resourceGroup,
                scaleSet: scaleSet
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (workers.Length > 1) { throw new InvalidOperationException(message: "release recovery requires one worker"); }
            if (workers.Length == 1) {
                var worker = Text(value: workers[0]!["name"]);
                var process = await InspectAsync(worker: worker).ConfigureAwait(continueOnCapturedContext: false);
                var image = process["image"]?.GetValue<string>();

                if (
                    (image is not null) &&
                    (image != target.Image) &&
                    (image != source?.Image)
                ) {
                    throw new InvalidOperationException(message: "the worker contains an unknown image; refusing to stop another deployment");
                }
                // A failed private candidate may be unable to checkpoint. Its protected source roots are already durable.
                await WorldGuestAsync(
                    group: resourceGroup,
                    script: (await GuestGuardAsync(
                        operation: operation,
                        phases: [WorldReleaseOperationPhase.Recover],
                        token: cancellationToken
                    ).ConfigureAwait(continueOnCapturedContext: false) +
                    "systemctl stop puck-world.service || true\nif docker inspect puck-world >/dev/null 2>&1; then docker rm -f puck-world; fi"),
                    worker: worker
                ).ConfigureAwait(continueOnCapturedContext: false);
            }
            return new(
                true,
                "private worker stopped"
            );
        }
        public async Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await RequireAsync(
                cancellationToken: cancellationToken,
                expected: operation,
                phases: [WorldReleaseOperationPhase.Verify, WorldReleaseOperationPhase.Commit]
            ).ConfigureAwait(continueOnCapturedContext: false);
            await CheckWorkerAsync(
                await WorkerAsync().ConfigureAwait(continueOnCapturedContext: false),
                target
            ).ConfigureAwait(continueOnCapturedContext: false);
            return new(
                true,
                "exact candidate identity and private persistence health verified"
            );
        }
    }

    private static async Task<JsonNode> WorldGuestJsonAsync(string group, string script, string worker) {
        var marker = (("puck-json-" + Guid.NewGuid().ToString(format: "N")) + ":");
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: (("puck-release-request-" + Guid.NewGuid().ToString(format: "N")) + ".sh")
        );

        try {
            File.WriteAllText(
                contents: $"set -eu\npayload=$(\n{script}\n)\nprintf '%s' '{marker}'\nprintf '%s' \"$payload\" | base64 -w0\nprintf '\\n'\n",
                path: path
            );
            var response = await AzJsonAsync(
                "vm",
                "run-command",
                "invoke",
                "-g",
                group,
                "-n",
                worker,
                "--command-id",
                "RunShellScript",
                "--scripts",
                ("@" + path),
                "-o",
                "json"
            ).ConfigureAwait(continueOnCapturedContext: false);
            var message = string.Join(
                separator: "\n",
                values: response["value"]!.AsArray().Select(selector: row => Text(value: row!["message"]))
            );
            var start = message.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: marker
            );

            if (start < 0) { throw new IOException(message: "world worker did not return a complete release response"); }
            var encoded = message[(start + marker.Length)..].Split(
                '\n',
                2
            )[0].Trim();

            return (JsonNode.Parse(Encoding.UTF8.GetString(bytes: Convert.FromBase64String(s: encoded))) ?? throw new InvalidDataException(message: "world worker returned an empty release response"));
        } finally { File.Delete(path: path); }
    }
}
