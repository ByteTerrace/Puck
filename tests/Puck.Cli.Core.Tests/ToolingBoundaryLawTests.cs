using Puck.Cli.Architecture;
using Xunit;

namespace Puck.Cli.Core.Tests;

public sealed class ToolingBoundaryLawTests {
    [Fact]
    public void ProviderProjectsAndSdkReferencesAreRejectedByTheSharedPolicyExceptInTheirProviderOrTests() {
        var model = new ArchitectureModel();
        model.Kinds["Tool"] = false;
        model.Kinds["Test"] = false;
        model.Kinds["Production"] = true;
        model.Ranks["Optional extensions"] = -1;
        model.Providers["Provider"] = ["Provider.Sdk"];
        var provider = new ArchitectureProject { Name = "Puck.Provider", Kind = "Production", Layer = "Optional extensions", Provider = "Provider" };
        var tool = new ArchitectureProject { Name = "Puck.Tool", Kind = "Tool" };
        var failures = model.Check(tool, [provider], [], ["Provider.Sdk.Storage"]);
        Assert.Equal(2, failures.Count);
        Assert.All(failures, failure => Assert.Equal("PUCKARCH009", failure.Code));
        tool.Provider = "Provider";
        Assert.Empty(model.Check(tool, [provider], [], ["Provider.Sdk.Storage"]));
        tool.Provider = "";
        tool.Kind = "Test";
        Assert.Empty(model.Check(tool, [provider], [], ["Provider.Sdk.Storage"]));
    }

    [Fact]
    public async Task LostHeadroomReleasesTheGpuBeforeWaitingAgainAndCancellationReleasesAnAcquiredHold() {
        var held = false;
        var acquisitions = 0;
        var judgements = new List<(bool Held, bool Reserve)>();
        Task<IDisposable> Acquire(TimeSpan? _, CancellationToken token) {
            Assert.False(held);
            held = true;
            acquisitions++;
            return Task.FromResult<IDisposable>(new Hold(() => held = false));
        }
        HostAdmit admit = (_, _, _, _, _, _, reserve, _, _, _) => {
            judgements.Add((held, reserve));
            return Task.FromResult(judgements.Count != 2);
        };
        using var progress = new StringWriter();
        var lease = await LeasedAdmission.AcquireAsync(Acquire, admit, ".", "law", true, false, HostStartKind.Step, TimeProvider.System, TimeSpan.FromMinutes(1), progress, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, acquisitions);
        Assert.Equal([(false, false), (true, true), (false, false), (true, true)], judgements);
        Assert.True(held);
        lease.Dispose();
        Assert.False(held);

        admit = (_, _, _, _, _, _, reserve, _, _, _) => reserve ? throw new OperationCanceledException() : Task.FromResult(true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => LeasedAdmission.AcquireAsync(Acquire, admit, ".", "law", true, false, HostStartKind.Step, TimeProvider.System, TimeSpan.Zero, progress, null, TestContext.Current.CancellationToken));
        Assert.False(held);
    }

    private sealed class Hold(Action release) : IDisposable {
        public void Dispose() => release();
    }
}
