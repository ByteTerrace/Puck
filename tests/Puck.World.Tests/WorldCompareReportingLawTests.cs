using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Late comparison verdicts reach the script streams, administrative tape, and refusal count exactly once.</summary>
public sealed class WorldCompareReportingLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void InstalledComparisonReportPreservesStreamsTapeAndRefusalCount(bool refused) {
        using var files = new TemporaryDirectory();
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        using var output = new BufferedConsoleOutput(error: stderr, output: stdout);
        var builder = WorldBootHarness.Compose(files, WorldHostPresentation.None,
            "tests/Puck.World.Canaries/editor-grid/fixture.puck");

        builder.Services.AddSingleton(implementationInstance: output);
        var host = files.Own(owner: builder.Build());

        Assert.True(condition: WorldPostBuildWiring.Install(services: host.Services));
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var sessions = host.Services.GetRequiredService<TerminalConsoleSessions>();
        var capture = host.Services.GetRequiredService<WorldCompareCapture>();
        const string Message = "[world.compare: late capture verdict — β]";

        Assert.Equal(expected: "[wire.errors: 0 rejected]", actual: registry.Submit(line: "wire.errors").Output);
        Assert.NotNull(@object: capture.Report);
        capture.Report(obj: new CommandResult(Output: Message) { IsError = refused });
        output.Flush();
        Assert.True(condition: sessions.OperatorStore.TrySnapshot(frame: out var tape));
        var line = Assert.Single(collection: tape.Lines);

        Assert.Multiple(
            () => Assert.Equal(expected: (refused ? "" : (Message + Environment.NewLine)), actual: Encoding.UTF8.GetString(bytes: stdout.ToArray())),
            () => Assert.Equal(expected: (refused ? (Message + Environment.NewLine) : ""), actual: Encoding.UTF8.GetString(bytes: stderr.ToArray())),
            () => Assert.Equal(expected: new ConsoleTapeLine(Refused: refused, Text: Message), actual: line),
            () => Assert.Equal(expected: (refused ? "[wire.errors: 1 rejected]" : "[wire.errors: 0 rejected]"),
                actual: registry.Submit(line: "wire.errors").Output));
    }

}
