using Puck.Commands;
using Puck.Testing;

namespace Puck.Hosting.Tests;

// Laws for the control transport's output bound: a long console answer is a completed read, delivered whole up to
// ControlLimits.OutputCharacters and cut with a marker past it, never an unknown outcome.
public sealed class ControlOutputLawTests {
    private const string Marker = "\n[control: output truncated;";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // A help listing runs to well over 64 KiB; the transport carries it whole.
    [Fact]
    public async Task AnAnswerLongerThanSixtyFourKibCrossesTheWireWhole() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var directory = new TemporaryDirectory();
        await using var host = new LocalControlServer(
            createSession: () => new LongSession(length: 200_000),
            directory: directory.RootPath
        );
        using var client = await LocalControlClient.ConnectAsync(
            attachmentPath: host.AttachmentPath,
            cancellationToken: Token
        );
        var response = await client.ExecuteAsync(
            "exec",
            "anything",
            cancellationToken: Token
        );

        Assert.Equal(
            "completed",
            response.Status
        );
        Assert.False(condition: response.Truncated);
        Assert.Equal(
            200_000,
            response.Output.Length
        );
    }
    // The server cuts any session's long answer before it validates one, so an injected session's long read crosses
    // the wire completed rather than as an invalid result.
    [Fact]
    public async Task TheServerDeliversAnySessionsLongAnswerCompleted() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var directory = new TemporaryDirectory();
        await using var host = new LocalControlServer(
            createSession: () => new LongSession(length: (2 * ControlLimits.OutputCharacters)),
            directory: directory.RootPath
        );
        using var client = await LocalControlClient.ConnectAsync(
            attachmentPath: host.AttachmentPath,
            cancellationToken: Token
        );
        var response = await client.ExecuteAsync(
            "exec",
            "anything",
            cancellationToken: Token
        );

        Assert.Equal(
            "completed",
            response.Status
        );
        Assert.True(condition: response.Truncated);
        Assert.Equal(
            ControlLimits.OutputCharacters,
            response.Output.Length
        );
        Assert.False(condition: client.IsClosed);
    }
    [Fact]
    public async Task AConsoleAnswerPastTheBoundIsCutWithAMarkerUnderItsOwnStatus() {
        var source = new TextCommandSource(new CommandRegistry([new LongModule()]));
        using var session = new ConsoleControlSession(
            source,
            _ => throw new InvalidOperationException(message: "No renderer.")
        );
        var reply = session.ExecuteAsync(
            new(
                Command: "long 1500000",
                Id: 1,
                Operation: "exec",
                TimeoutMilliseconds: 1000
            ),
            Token
        );

        source.Collect();

        var response = await reply;

        Assert.Equal(
            "completed",
            response.Status
        );
        Assert.False(condition: response.IsError);
        Assert.True(condition: response.Truncated);
        Assert.Equal(
            ControlLimits.OutputCharacters,
            response.Output.Length
        );
        Assert.Contains(
            "its 1500000 characters exceed the 1048576-character limit",
            response.Output
        );
        Assert.StartsWith(
            new string(c: 'x', count: 1000),
            response.Output
        );
    }
    [Fact]
    public void ACutNeverSplitsASurrogatePairAndAnAnswerWithinTheBoundIsKept() {
        foreach (var length in new[] { (ControlLimits.OutputCharacters + 10), (ControlLimits.OutputCharacters + 11) }) {
            var text = string.Concat(values: Enumerable.Repeat(
                count: ((length / 2) + 1),
                element: "\U0001F600"
            ))[..length];
            var bounded = new ControlResponse(
                1,
                "completed",
                text
            ).Bounded();

            Assert.True(condition: bounded.Truncated);
            Assert.True(condition: (bounded.Output.Length <= ControlLimits.OutputCharacters));

            var head = bounded.Output[..bounded.Output.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: Marker
            )];

            Assert.False(condition: char.IsHighSurrogate(c: head[^1]));
        }

        var within = new ControlResponse(
            1,
            "refused",
            new string(c: 'x', count: ControlLimits.OutputCharacters),
            true
        );

        Assert.Same(
            within,
            within.Bounded()
        );
    }

    private sealed class LongModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                "long",
                "Answers with the given number of characters.",
                (_, args) => new(new string(c: 'x', count: int.Parse(s: args[0].ToString(), provider: System.Globalization.CultureInfo.InvariantCulture))),
                bindability: CommandBindability.Unbindable
            );
        }
    }
    private sealed class LongSession(int length) : IControlSession {
        public void Dispose() { }
        public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) => Task.FromResult(result: new ControlResponse(
            request.Id,
            "completed",
            new string(c: 'y', count: length)
        ));
    }
}
