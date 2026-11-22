using Puck.Testing;
using Xunit;

namespace Puck.Cli.Laws.Tests;

/// <summary>Every MSBuild run the CLI starts in one checkout waits for the one before it to finish, so a World store
/// build never writes a checkout's outputs while another build of that checkout does.</summary>
public sealed class CheckoutBuildLockLawTests {
    [Fact]
    public async Task TwoBuildsOfOneCheckoutSerialize() {
        using var directory = new TemporaryDirectory(prefix: "puck-checkout-lock-law-");
        _ = directory.WriteText(name: "Puck.slnx", text: "<Solution />");
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await CliCheckoutBuildLock.AcquireAsync(arguments: ["build", "src/Puck.World/Puck.World.csproj"], cancellationToken: cancellationToken, fileName: "dotnet", workingDirectory: directory.PathOf(name: "src"));

        Assert.NotNull(@object: first);
        Assert.True(condition: File.Exists(path: directory.PathOf(name: CliCheckoutBuildLock.RelativePath)));

        var queued = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var second = CliCheckoutBuildLock.AcquireAsync(cancellationToken: cancellationToken, checkout: directory.RootPath, waiting: queued.SetResult);

        await queued.Task.WaitAsync(cancellationToken: cancellationToken, timeout: TestLiveness.Bound);
        Assert.False(condition: second.IsCompleted);
        first.Dispose();
        using var held = await second.WaitAsync(cancellationToken: cancellationToken, timeout: TestLiveness.Bound);
    }
    [InlineData("dotnet", "build", true)]
    [InlineData("dotnet", "publish", true)]
    [InlineData("C:/Program Files/dotnet/dotnet.exe", "restore", true)]
    [InlineData("dotnet", "msbuild", true)]
    [InlineData("dotnet", "Puck.World.dll", false)]
    [InlineData("dotnet", "test", false)]
    [InlineData("cargo", "build", false)]
    [Theory]
    public void OnlyMSBuildRunsTakeTheLock(string fileName, string subcommand, bool builds) =>
        Assert.Equal(expected: builds, actual: CliCheckoutBuildLock.Builds(arguments: [subcommand, "x"], fileName: fileName));
}
