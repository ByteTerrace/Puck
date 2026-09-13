using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.World.Server;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Checks the Python guest guard against the actual C# durable wire format.</summary>
public sealed class WorldReleaseGuestGuardTests {
    [Fact]
    public async Task DelayedGuestEffectsRefuseChangedOperationsAndBootstrapRoles() {
        var token = TestContext.Current.CancellationToken;
        var python = Environment.GetEnvironmentVariable("PUCK_TEST_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3");
        try { await CliProcess.RunCheckedAsync(Environment.CurrentDirectory, python, ["--version"], capture: true, cancellationToken: token); }
        catch (System.ComponentModel.Win32Exception) { Assert.Skip("Install Python 3 or set PUCK_TEST_PYTHON to run the retained guest guard contract law."); return; }
        Assert.True(CliPaths.TryGetRepositoryRoot(out var root), "repository root is required");
        var operation = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var record = new WorldReleaseGroupRecord {
            Schema = WorldReleaseGroupStore.Schema, DeploymentGroup = "official", Owner = owner, ActiveRelease = "source",
            PendingOperationId = operation, PendingSourceRelease = "source", PendingTargetRelease = "target", PendingPhase = WorldReleaseOperationPhase.Activate,
        };
        var request = new JsonObject {
            ["owner"] = owner.ToString("D"), ["group"] = "official", ["operation"] = operation.ToString("D"),
            ["source"] = "source", ["target"] = "target", ["phases"] = new JsonArray((int)WorldReleaseOperationPhase.Activate),
        };
        var cases = new JsonArray();
        void Add(JsonObject wanted, WorldReleaseGroupRecord actual, bool accepted) => cases.Add(new JsonObject {
            ["request"] = wanted.DeepClone(), ["record"] = JsonSerializer.SerializeToNode(actual), ["accepted"] = accepted,
        });
        Add(request, record, true);
        Add(request, record with { PendingOperationId = Guid.NewGuid() }, false);
        Add(request, record with { PendingOperationId = null }, false);
        Add(request, record with { PendingPhase = WorldReleaseOperationPhase.Recover }, false);
        Add(request, record with { Owner = Guid.NewGuid() }, false);
        Add(request, record with { DeploymentGroup = "another" }, false);
        Add(request, record with { PendingTargetRelease = "another" }, false);
        Add(request, record with { PendingSourceRelease = "another" }, false);
        var bootstrap = new JsonObject { ["owner"] = owner.ToString("D"), ["group"] = "official", ["bootRelease"] = "target" };
        Add(bootstrap, record, true);
        Add(bootstrap, record with { PendingPhase = WorldReleaseOperationPhase.Drain }, false);
        Add(bootstrap, record with { PendingPhase = WorldReleaseOperationPhase.Commit }, true);
        Add(bootstrap, record with { PendingOperationId = null, ActiveRelease = "target", Admission = WorldReleaseAdmissionState.Open }, true);
        Add(bootstrap, record with { PendingOperationId = null, ActiveRelease = "source", Admission = WorldReleaseAdmissionState.Open }, false);
        Add(bootstrap, record with { PendingOperationId = null, ActiveRelease = "target", Admission = WorldReleaseAdmissionState.Closed }, false);
        Add(bootstrap, record with { PendingPhase = WorldReleaseOperationPhase.RecoverActivate }, false);
        bootstrap["bootRelease"] = "source";
        Add(bootstrap, record, false);
        Add(bootstrap, record with { PendingPhase = WorldReleaseOperationPhase.RecoverActivate }, true);
        Add(bootstrap, record with { PendingPhase = WorldReleaseOperationPhase.Finalized, PendingOperationId = null }, false);
        var directory = Directory.CreateTempSubdirectory("puck-guest-guard-");
        try {
            var path = Path.Combine(directory.FullName, "cases.json");
            File.WriteAllText(path, cases.ToJsonString());
            const string Program = """
                import json, runpy, sys
                validate = runpy.run_path(sys.argv[1])['validate']
                for index, case in enumerate(json.load(open(sys.argv[2], encoding='utf-8'))):
                    try:
                        validate(case['request'], case['record'])
                        accepted = True
                    except ValueError:
                        accepted = False
                    assert accepted == case['accepted'], f'guest guard case {index} returned {accepted}'
                print('guest guard wire contract passed')
                """;
            var result = await CliProcess.RunCheckedAsync(directory.FullName, python, ["-c", Program, Path.Combine(root, "build/Guard-WorldRelease.py"), path], capture: true, cancellationToken: token);
            Assert.Contains("guest guard wire contract passed", result, StringComparison.Ordinal);
        } finally { directory.Delete(recursive: true); }
    }
}
