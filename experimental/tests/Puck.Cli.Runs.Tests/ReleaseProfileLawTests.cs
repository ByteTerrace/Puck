using System.Text;
using System.Text.Json.Nodes;
using Puck.Cli.Canary;
using Puck.Cli.Qualification;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>puck qualify</c> reads a <c>puck.release.profile.v1</c> document strictly and refuses one
/// qualification could not honor, naming the member: an unknown or missing member, a foreign schema tag, a backend no
/// leg boots, a validation layer on a backend the profile does not run, an odd resolution axis, a workload named twice
/// or booting anything but a package-relative <c>.world.json</c>, and a matrix cell with no threshold row, two, or a
/// pipeline threshold that disagrees with its workload. The checked-in profile loads, and its functional list names
/// only offscreen canaries and every <c>pipeline-*</c> canary. A valid profile expands into
/// backend-major cells, and each cell's script arms exactly the waits, readings, inspections and releases its
/// expectation counts, with a resize naming the exact extents its layout slot halves to.
/// </summary>
public sealed class ReleaseProfileLawTests {
    [Fact]
    public void TheCheckedInProfileLoads() {
        Assert.True(condition: ReleaseProfileLoader.TryLoad(
            path: RepositoryPaths.Resolve(relativePath: ReleaseProfileLoader.DefaultPath),
            profile: out var profile,
            reason: out var reason
        ), userMessage: reason);
        Assert.Equal(actual: profile.Publish.Mode, expected: ReleasePublishMode.ReadyToRun);
        Assert.Equal(actual: profile.Publish.Compiler, expected: ReleaseCompilerDiscovery.None);
        Assert.Equal(
            actual: QualificationPlan.Cells(profile: profile).Count,
            expected: ((profile.Backends.Count * profile.Workloads.Count) * profile.Resolutions.Count)
        );
        Assert.Contains(collection: profile.Deferred, filter: static deferral => (deferral.Check == QualificationDeferredCheck.DeviceLocalPeak));
    }
    [Fact]
    public void TheCheckedInProfileRunsEveryPipelineCanaryAndOnlyOffscreenProofs() {
        Assert.True(condition: ReleaseProfileLoader.TryLoad(
            path: RepositoryPaths.Resolve(relativePath: ReleaseProfileLoader.DefaultPath),
            profile: out var profile,
            reason: out var reason
        ), userMessage: reason);
        Assert.True(condition: CanaryManifestLoader.TryLoadAll(
            error: out var error,
            manifests: out var manifests,
            refused: out _,
            repositoryRoot: RepositoryPaths.RequireRoot(),
            strict: false
        ), userMessage: error);

        var byId = manifests.ToDictionary(keySelector: static manifest => manifest.Id);

        foreach (var id in profile.Functional) {
            Assert.True(condition: byId.TryGetValue(key: id, value: out var manifest), userMessage: $"functional: '{id}' names no canary");
            Assert.Equal(actual: manifest.BootShape, expected: CanaryBootShape.Offscreen);
        }

        Assert.Empty(collection: byId.Keys.Where(predicate: static id => id.StartsWith(comparisonType: StringComparison.Ordinal, value: "pipeline-")).Except(second: profile.Functional));
    }
}
