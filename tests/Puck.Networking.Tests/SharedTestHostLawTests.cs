using Puck.Testing;
using Xunit;

namespace Puck.Networking.Tests;

/// <summary>Laws for <see cref="SharedTestHost"/>: a suite runs under the shared <c>dotnet</c> host and refuses its
/// per-checkout apphost, whose new image path Windows Firewall asks about once a QUIC law listens.</summary>
public sealed class SharedTestHostLawTests {
    [Theory]
    [InlineData("C:/Program Files/dotnet/dotnet.exe")]
    [InlineData("C:/Program Files/dotnet/DOTNET.EXE")]
    [InlineData("/usr/share/dotnet/dotnet")]
    public void TheSharedHostRuns(string processPath) => Assert.True(condition: SharedTestHost.IsSharedHost(processPath: processPath));
    [Theory]
    [InlineData("C:/Source/Puck/.claude/worktrees/lane/tests/Puck.Networking.Tests/bin/Release/net10.0/Puck.Networking.Tests.exe")]
    [InlineData("/home/runner/work/Puck/tests/Puck.Networking.Tests/bin/Release/net10.0/Puck.Networking.Tests")]
    public void ASuitesApphostIsRefused(string processPath) => Assert.False(condition: SharedTestHost.IsSharedHost(processPath: processPath));
    [Fact]
    public void ThisRunIsUnderTheSharedHost() => Assert.True(
        condition: SharedTestHost.IsSharedHost(processPath: Environment.ProcessPath),
        userMessage: $"this suite runs under {Environment.ProcessPath}"
    );
}
