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
        var python = (OperatingSystem.IsWindows()
            ? "python"
            : "python3");

        try {
            await CliProcess.RunCheckedAsync(
            workingDirectory: Environment.CurrentDirectory,
            fileName: python,
            arguments: ["--version"],
            capture: true,
            cancellationToken: token
        );
        } catch (Exception exception) when ((exception is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)) {
            // Absent from PATH, or present only as a launcher stub that exits nonzero (Windows' Store alias exits 9009).
            Assert.Skip(reason: "Install Python 3 on PATH to run the retained guest guard contract law.");
            return;
        }
        Assert.True(
            condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root),
            userMessage: "repository root is required"
        );
        var operation = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var record = new WorldReleaseGroupRecord {
            ActiveRelease = "source",
            DeploymentGroup = "official",
            Owner = owner,
            PendingOperationId = operation,
            PendingPhase = WorldReleaseOperationPhase.Activate,
            PendingSourceRelease = "source",
            PendingTargetRelease = "target",
            Schema = WorldReleaseGroupStore.Schema,
        };
        var request = new JsonObject {
            ["owner"] = owner.ToString(format: "D"),
            ["group"] = "official",
            ["operation"] = operation.ToString(format: "D"),
            ["source"] = "source",
            ["target"] = "target",
            ["phases"] = new JsonArray(((int)WorldReleaseOperationPhase.Activate)),
        };
        var cases = new JsonArray();

        void Add(JsonObject wanted, WorldReleaseGroupRecord actual, bool accepted) => cases.Add(value: new JsonObject {
            ["request"] = wanted.DeepClone(),
            ["record"] = JsonSerializer.SerializeToNode(actual),
            ["accepted"] = accepted,
        });
        Add(
            accepted: true,
            actual: record,
            wanted: request
        );
        Add(
            request,
            record with { PendingOperationId = Guid.NewGuid() },
            false
        );
        Add(
            request,
            record with { PendingOperationId = null },
            false
        );
        Add(
            request,
            record with { PendingPhase = WorldReleaseOperationPhase.Recover },
            false
        );
        Add(
            request,
            record with { Owner = Guid.NewGuid() },
            false
        );
        Add(
            request,
            record with { DeploymentGroup = "another" },
            false
        );
        Add(
            request,
            record with { PendingTargetRelease = "another" },
            false
        );
        Add(
            request,
            record with { PendingSourceRelease = "another" },
            false
        );
        var bootstrap = new JsonObject { ["owner"] = owner.ToString(format: "D"), ["group"] = "official", ["bootRelease"] = "target" };

        Add(
            accepted: true,
            actual: record,
            wanted: bootstrap
        );
        Add(
            bootstrap,
            record with { PendingPhase = WorldReleaseOperationPhase.Drain },
            false
        );
        Add(
            bootstrap,
            record with { PendingPhase = WorldReleaseOperationPhase.Commit },
            true
        );
        Add(
            bootstrap,
            record with { PendingOperationId = null, ActiveRelease = "target", Admission = WorldReleaseAdmissionState.Open },
            true
        );
        Add(
            bootstrap,
            record with { PendingOperationId = null, ActiveRelease = "source", Admission = WorldReleaseAdmissionState.Open },
            false
        );
        Add(
            bootstrap,
            record with { PendingOperationId = null, ActiveRelease = "target", Admission = WorldReleaseAdmissionState.Closed },
            false
        );
        Add(
            bootstrap,
            record with { PendingPhase = WorldReleaseOperationPhase.RecoverActivate },
            false
        );
        bootstrap["bootRelease"] = "source";
        Add(
            accepted: false,
            actual: record,
            wanted: bootstrap
        );
        Add(
            bootstrap,
            record with { PendingPhase = WorldReleaseOperationPhase.RecoverActivate },
            true
        );
        Add(
            bootstrap,
            record with { PendingPhase = WorldReleaseOperationPhase.Finalized, PendingOperationId = null },
            false
        );
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-guest-guard-");

        try {
            var path = Path.Combine(
                path1: directory.FullName,
                path2: "cases.json"
            );

            File.WriteAllText(
                path,
                cases.ToJsonString()
            );
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
            var result = await CliProcess.RunCheckedAsync(
                workingDirectory: directory.FullName,
                fileName: python,
                arguments: ["-c", Program, Path.Combine(
                        path1: root,
                        path2: "build/Guard-WorldRelease.py"
                    ), path],
                capture: true,
                cancellationToken: token
            );

            Assert.Contains(
                actualString: result,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "guest guard wire contract passed"
            );
        } finally { directory.Delete(recursive: true); }
    }
}
