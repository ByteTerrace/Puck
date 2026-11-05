using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>CONTRACT UNDER TEST: the client reports through <see cref="WorldClientDiagnostics"/> only. A captured scope
/// receives every diagnostic reported inside it, on any thread the scope's flow starts, and only those; the line a
/// diagnostic spells is the one <c>host.log</c> filters by its leading tag.</summary>
public sealed class WorldClientDiagnosticsLawTests {
    private sealed class Recording : IWorldClientDiagnosticSink {
        public List<string> Lines { get; } = [];

        public void Report(in WorldClientDiagnostic diagnostic) {
            lock (Lines) {
                Lines.Add(item: diagnostic.Line);
            }
        }
    }

    [Fact]
    public async Task ACapturedScopeReadsWhatTheFlowReportsAndNothingAfterIt() {
        var recording = new Recording();

        using (WorldClientDiagnostics.Capture(sink: recording)) {
            WorldClientDiagnostics.Report(channel: "player.join", text: "denied: no seat");
            await Task.Run(
                action: static () => WorldClientDiagnostics.Report(channel: "world.lever", text: "dropped"),
                cancellationToken: TestContext.Current.CancellationToken
            );
        }

        var outer = new Recording();

        using (WorldClientDiagnostics.Capture(sink: outer)) {
            using (WorldClientDiagnostics.Capture(sink: recording)) {
                WorldClientDiagnostics.Report(channel: "frame", text: "inner");
            }

            WorldClientDiagnostics.Report(channel: "frame", text: "outer");
        }

        Assert.Equal(
            actual: recording.Lines,
            expected: ["[player.join: denied: no seat]", "[world.lever: dropped]", "[frame: inner]"]
        );
        Assert.Equal(
            actual: outer.Lines,
            expected: ["[frame: outer]"]
        );
    }
    [Fact]
    public void AFramedLineReadsBackAsTheSameDiagnostic() {
        var framed = new WorldClientDiagnostic(Channel: "world.faces", Text: "'a':'b' shows unbound glass");

        Assert.Equal(
            actual: WorldClientDiagnostic.FromLine(line: framed.Line),
            expected: framed
        );
        Assert.Equal(
            actual: WorldClientDiagnostic.FromLine(line: "not framed"),
            expected: new WorldClientDiagnostic(Channel: "client", Text: "not framed")
        );
    }
}
