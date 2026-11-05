using Puck.Commands;

namespace Puck.World.Client;

/// <summary>
/// One line the presentation client reports about what it refused, dropped or could not show: the channel that names
/// the subsystem (<c>player.join</c>, <c>player.bindings</c>, <c>world.presentation-field</c>) and the text.
/// </summary>
/// <param name="Channel">The subsystem's tag, which opens the line and is the category <c>host.log</c> filters on.</param>
/// <param name="Text">The line's text, without the channel.</param>
public readonly record struct WorldClientDiagnostic(string Channel, string Text) {
    /// <summary>Gets the line as the console and <c>host.log</c> read it: <c>[channel: text]</c>.</summary>
    public string Line => $"[{Channel}: {Text}]";

    /// <summary>Reads a diagnostic back from a line another project framed itself, as <see cref="Line"/> spells it.</summary>
    /// <param name="line">The framed line, <c>[channel: text]</c>.</param>
    /// <returns>The diagnostic; a line that is not framed that way has the channel <c>client</c> and the whole line as its
    /// text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    public static WorldClientDiagnostic FromLine(string line) {
        ArgumentNullException.ThrowIfNull(argument: line);

        var separator = line.IndexOf(value: ": ", comparisonType: StringComparison.Ordinal);

        return ((line.StartsWith(value: '[') && line.EndsWith(value: ']') && (separator > 1))
            ? new WorldClientDiagnostic(Channel: line[1..separator], Text: line[(separator + 2)..^1])
            : new WorldClientDiagnostic(Channel: "client", Text: line));
    }
}

/// <summary>Receives the presentation client's diagnostics.</summary>
public interface IWorldClientDiagnosticSink {
    /// <summary>Receives one diagnostic. Called on whichever thread reports it, so an implementation is safe to call
    /// from any thread and does not throw.</summary>
    /// <param name="diagnostic">The diagnostic.</param>
    void Report(in WorldClientDiagnostic diagnostic);
}

/// <summary>
/// The one channel the presentation client reports through. No client code writes to <see cref="Console"/> itself: a
/// diagnostic reaches the sink in effect, which by default frames it as one record on standard error
/// (<see cref="ConsoleRecord"/>), the stream the on-screen console mirrors and <c>host.log</c> holds, so a headless
/// script, a canary and an Operator attachment all read the same line.
/// <para>
/// A caller replaces the sink for the logical flow that is running (<see cref="Capture"/>), so a law reads what a
/// client reported without redirecting the process's standard error, and parallel laws never read each other's lines.
/// </para>
/// </summary>
public static class WorldClientDiagnostics {
    private sealed class ConsoleSink : IWorldClientDiagnosticSink {
        public void Report(in WorldClientDiagnostic diagnostic) =>
            ConsoleRecord.Write(
                record: diagnostic.Line,
                writer: Console.Error
            );
    }
    private sealed class Scope(IWorldClientDiagnosticSink? previous) : IDisposable {
        public void Dispose() => Captured.Value = previous;
    }

    private static readonly IWorldClientDiagnosticSink DefaultSink = new ConsoleSink();
    private static readonly AsyncLocal<IWorldClientDiagnosticSink?> Captured = new();

    /// <summary>Replaces the sink for the current logical flow of execution, including the tasks and threads it starts,
    /// until the returned scope is disposed.</summary>
    /// <param name="sink">The sink that receives every diagnostic reported while the scope is open.</param>
    /// <returns>The scope, which restores the sink in effect before this call when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public static IDisposable Capture(IWorldClientDiagnosticSink sink) {
        ArgumentNullException.ThrowIfNull(argument: sink);

        var scope = new Scope(previous: Captured.Value);

        Captured.Value = sink;

        return scope;
    }
    /// <summary>Reports one diagnostic to the sink in effect.</summary>
    /// <param name="channel">The subsystem's tag.</param>
    /// <param name="text">The line's text, without the channel.</param>
    public static void Report(string channel, string text) =>
        Report(diagnostic: new WorldClientDiagnostic(Channel: channel, Text: text));
    /// <summary>Reports one diagnostic to the sink in effect.</summary>
    /// <param name="diagnostic">The diagnostic.</param>
    public static void Report(WorldClientDiagnostic diagnostic) =>
        (Captured.Value ?? DefaultSink).Report(diagnostic: in diagnostic);
}
