using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Late presentation verdicts reach the script streams, administrative tape, and refusal count exactly once.</summary>
[Collection(AllocationCollection.Name)]
public sealed class WorldCompareReportingLawTests {
    [InlineData(false, "world.compare")]
    [InlineData(true, "world.compare")]
    [InlineData(false, "world.explain")]
    [InlineData(true, "world.explain")]
    [Theory]
    public void InstalledComparisonReportPreservesStreamsTapeAndRefusalCount(bool refused, string verb) {
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
        var inspection = Assert.Single(collection: host.Services.GetServices<ICommandModule>(),
            predicate: module => (module.GetType().Name == "WorldInspectionCommandModule"));
        var report = ((verb == "world.compare")
            ? host.Services.GetRequiredService<WorldCompareCapture>().Report
            : (Action<CommandResult>?)inspection.GetType().GetProperty(name: "Report")!.GetValue(obj: inspection));
        var message = $"[{verb}: late presentation verdict — β]";

        Assert.Equal(expected: "[wire.errors: 0 rejected]", actual: registry.Submit(line: "wire.errors").Output);
        Assert.NotNull(@object: report);
        report(obj: new CommandResult(Output: message) { IsError = refused });
        output.Flush();
        Assert.True(condition: sessions.OperatorStore.TrySnapshot(frame: out var tape));
        var line = Assert.Single(collection: tape.Lines);

        Assert.Multiple(
            () => Assert.Equal(expected: (refused ? "" : (message + Environment.NewLine)), actual: Encoding.UTF8.GetString(bytes: stdout.ToArray())),
            () => Assert.Equal(expected: (refused ? (message + Environment.NewLine) : ""), actual: Encoding.UTF8.GetString(bytes: stderr.ToArray())),
            () => Assert.Equal(expected: new ConsoleTapeLine(Refused: refused, Text: message), actual: line),
            () => Assert.Equal(expected: (refused ? "[wire.errors: 1 rejected]" : "[wire.errors: 0 rejected]"),
                actual: registry.Submit(line: "wire.errors").Output));
    }

}
