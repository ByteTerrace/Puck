using Microsoft.Extensions.Hosting;
using Puck.Commands;

namespace Puck.World.Silo;

/// <summary>
/// The silo's own stdin reader, replacing <c>Puck.Launcher</c>'s single-session
/// <c>StandardInputReaderService</c> (<c>AddLauncherHeadlessTerminal(readStandardInput: false)</c>). A tagged line
/// <c>@&lt;key&gt; line</c> (<c>&lt;key&gt;</c> = <c>owner/{oid}/{world}</c> or the bare world id) enqueues
/// <c>line</c> on that row's own session; an untagged <c>silo.*</c> line always reaches the administrative session;
/// any other untagged line reaches the session <c>silo.use &lt;key&gt;</c> last selected, and refuses by name
/// (writing directly, since no session is bound yet to tag it) when none is selected.
/// </summary>
public sealed class SiloStdinRouter(WorldSiloHost host, SiloConsoleRouting routing, TextCommandSource administrative) : BackgroundService {
    private const string SiloVerbPrefix = "silo.";

    private void ReadLoop(CancellationToken stoppingToken) {
        try {
            var input = Console.In;

            while (!stoppingToken.IsCancellationRequested) {
                var line = input.ReadLine();

                if (line is null) {
                    break;
                }

                Route(line: line);
            }
        } catch (IOException) {
            // No readable console — nothing to drive from, so the reader stops (StandardInputReaderService's own
            // rule).
        }
    }
    private void Route(string line) {
        var trimmed = line.AsSpan().TrimStart();

        if (
            trimmed.IsEmpty ||
            (trimmed[0] == '#')
        ) {
            administrative.Enqueue(line: line);

            return;
        }

        if (trimmed[0] == '@') {
            RouteTagged(rest: trimmed[1..]);

            return;
        }

        if (
            trimmed.StartsWith(comparisonType: StringComparison.Ordinal, value: SiloVerbPrefix) ||
            IsProcessVerb(trimmed: trimmed)
        ) {
            administrative.Enqueue(line: line);

            return;
        }

        if (
            (routing.DefaultWorldId is { } defaultWorldId) &&
            routing.TryGetSession(
            session: out var session,
            worldId: defaultWorldId
        )
        ) {
            session.Enqueue(line: line.ToString());

            return;
        }

        Console.Error.WriteLine(value: ((routing.DefaultWorldId is null)
            ? "refused: no row selected — tag the line '@<key> ...' or run 'silo.use <key>' first"
            : $"refused: '{routing.DefaultWorldId}' is no longer admitted — select another row with 'silo.use <key>'"
        ));
    }
    // The verbs that address the process rather than a row: the terminal's own quit and the registry-wide
    // rejection count. Everything else untagged is a row verb and needs a selected row.
    private static bool IsProcessVerb(ReadOnlySpan<char> trimmed) {
        var end = trimmed.IndexOfAny(value0: ' ', value1: '	');
        var verb = ((end < 0) ? trimmed : trimmed[..end]);

        return (verb.SequenceEqual(other: "quit") || verb.SequenceEqual(other: "wire.errors"));
    }
    private void RouteTagged(ReadOnlySpan<char> rest) {
        var separator = rest.IndexOf(value: ' ');
        var key = ((separator < 0) ? rest : rest[..separator]).ToString();
        var content = ((separator < 0) ? string.Empty : rest[(separator + 1)..].ToString());

        if (!host.TryResolveKey(
            identity: out var identity,
            key: key,
            reason: out var keyReason
        )) {
            Console.Error.WriteLine(value: $"refused: {keyReason}");

            return;
        }

        if (!routing.TryGetSession(
            session: out var session,
            worldId: identity.World.Value
        )) {
            Console.Error.WriteLine(value: $"refused: '{key}' is declared but not currently admitted");

            return;
        }

        session.Enqueue(line: content);
    }

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) {
        var readerThread = new Thread(start: () => ReadLoop(stoppingToken: stoppingToken)) {
            IsBackground = true,
            Name = "Puck.World.Silo Stdin Router",
        };

        readerThread.Start();

        return Task.CompletedTask;
    }
}
