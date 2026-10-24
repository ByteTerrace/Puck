using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Hosting;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>
/// The <see cref="HostLog"/> ring and its <c>host.log</c> verb: every line written through a teed writer still reaches
/// the writer it wraps and is held with its stream, its leading tag (inherited by an indented continuation) and the
/// tick anchored when it was written; the ring evicts the oldest past its line capacity or character budget, and the
/// verb selects by sequence, tick range, category, stream and text.
/// </summary>
public sealed class HostLogLawTests {
    [Fact]
    public void TeedLinesAreHeldWithTheirTagTickAndStreamAndTheVerbSelectsThem() {
        var log = new HostLog(capacity: 8, characterBudget: 4096);
        var services = new ServiceCollection();

        services.AddSingleton(implementationInstance: log);
        services.AddLauncherHeadlessTerminal(readStandardInput: false);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<CommandRegistry>();
        using var inner = new StringWriter();
        using var error = log.Tee(inner: inner, stream: HostLogStream.Error);
        using var output = log.Tee(inner: new StringWriter(), stream: HostLogStream.Output);

        error.Write(value: "[sdf.mesh-bake] start");
        error.Write(value: " residency=2\r\n  group one\n");
        var tick = 0UL;

        log.Anchor(completedTick: () => tick);
        tick = 7UL;
        error.WriteLine(value: "[world.wait: released at tick 7]");
        output.Write(value: 'p');
        output.Write(buffer: "lain answer\n".ToCharArray(), index: 0, count: 12);
        tick = 9UL;
        error.Write(buffer: "[sdf.mesh-gi] two words\n".AsSpan());
        error.WriteLine();
        log.Append(stream: HostLogStream.Error, text: "[host.refused] by the buffered output\n");

        Assert.Equal(expected: $"[sdf.mesh-bake] start residency=2\r\n  group one\n[world.wait: released at tick 7]{Environment.NewLine}[sdf.mesh-gi] two words\n{Environment.NewLine}", actual: inner.ToString());

        var all = registry.Submit(line: "host.log");

        Assert.False(condition: all.IsError);
        Assert.Equal(
            expected: string.Join(separator: '\n', values: [
                "[host.log: 7 of 7 matching line(s); holds 7 of the newest 8 lines, 0 evicted; next since 7]",
                "#1 t- err [sdf.mesh-bake] start residency=2",
                "#2 t- err   group one",
                "#3 t7 err [world.wait: released at tick 7]",
                "#4 t7 out plain answer",
                "#5 t9 err [sdf.mesh-gi] two words",
                "#6 t9 err ",
                "#7 t9 err [host.refused] by the buffered output",
            ]),
            actual: all.Output
        );
        Assert.Contains(expectedSubstring: "#2 t- err   group one", actualString: registry.Submit(line: "host.log category sdf.mesh-bake").Output);
        Assert.Contains(expectedSubstring: "2 of 3 matching", actualString: registry.Submit(line: "host.log category [sdf last 2").Output);
        Assert.StartsWith(expectedStartString: "[host.log: 1 of 1 matching", actualString: registry.Submit(line: "host.log stream out").Output);
        Assert.Contains(expectedSubstring: "#5 t9 err [sdf.mesh-gi] two words", actualString: registry.Submit(line: "host.log text \"TWO words\" ticks 8..").Output);
        Assert.StartsWith(expectedStartString: "[host.log: 2 of 2 matching", actualString: registry.Submit(line: "host.log ticks ..7").Output);
        Assert.StartsWith(expectedStartString: "[host.log: 1 of 1 matching", actualString: registry.Submit(line: "host.log since 6 stream err ticks 9..9").Output);

        foreach (var refused in new[] { "host.log last 0", "host.log last", "host.log bogus 1", "host.log ticks 9..2", "host.log ticks ..", "host.log ticks 5", "host.log ticks x..", "host.log ticks ..x", "host.log stream both", "host.log since -1" }) {
            Assert.True(condition: registry.Submit(line: refused).IsError, userMessage: refused);
        }
    }
    [Fact]
    public void TheRingEvictsTheOldestPastItsLinesOrCharactersAndCutsALongLine() {
        var log = new HostLog(capacity: 3, characterBudget: 10);

        foreach (var text in new[] { "aaaa", "bbbb", "cc", "dddddd" }) {
            log.Append(stream: HostLogStream.Output, text: text);
        }

        var read = log.Read(query: new HostLogQuery());

        Assert.Equal(expected: ["cc", "dddddd"], actual: read.Lines.Select(selector: static line => line.Text));
        Assert.Equal(expected: (4L, 2L, 2), actual: (read.Newest, read.Dropped, read.Held));

        foreach (var text in new[] { "e", "f", "g" }) {
            log.Append(stream: HostLogStream.Output, text: text);
        }

        Assert.Equal(expected: ["e", "f", "g"], actual: log.Read(query: new HostLogQuery()).Lines.Select(selector: static line => line.Text));

        var wide = new HostLog();

        wide.Append(stream: HostLogStream.Error, text: new string(c: 'x', count: (HostLog.MaxLineCharacters + 5)));

        Assert.EndsWith(expectedEndString: $"… [{HostLog.MaxLineCharacters + 5} characters]", actualString: wide.Read(query: new HostLogQuery()).Lines.Single().Text);
        Assert.Equal(expected: (HostLog.Capacity, HostLog.CharacterBudget), actual: (wide.LineCapacity, wide.CharacterLimit));
    }
    [Fact]
    public void BufferedOutputAppendsTheRefusalsItWrites() {
        var log = new HostLog();
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();

        using (var output = new BufferedConsoleOutput(error: stderr, log: log, output: stdout)) {
            output.WriteLine(value: "[accepted]");
            output.WriteErrorLine(value: "[refused: one]\nsecond line");
        }

        Assert.Equal(expected: ["[refused: one]", "second line"], actual: log.Read(query: new HostLogQuery()).Lines.Select(selector: static line => line.Text));
        Assert.All(collection: log.Read(query: new HostLogQuery()).Lines, action: static line => Assert.Equal(expected: HostLogStream.Error, actual: line.Stream));
    }
}
