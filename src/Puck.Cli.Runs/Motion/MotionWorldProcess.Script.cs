namespace Puck.Cli.Motion;

/// <summary>The ordered script output through the shared world.at read-back barrier.</summary>
public sealed record MotionScriptResult(ulong Tick, IReadOnlyList<MotionOutputLine> Lines);

public sealed partial class MotionWorldProcess {
    /// <summary>Runs console lines in order, then reads the completed tick through the same session. No sleep or
    /// wall-clock injection chooses a script tick; world.at owns every pinned operation.</summary>
    public MotionScriptResult RunScript(IReadOnlyList<string> lines, TimeSpan timeout) {
        var mark = LineCount;
        foreach (var line in lines) {
            if (!Send(line)) { throw new IOException("The World closed its console input."); }
        }
        if (!Send("world.at")) { throw new IOException("The World closed its console input before its read-back."); }
        var answer = WaitFor(mark, static row => row.Stream == CliProcessOutputStream.Stdout &&
            MotionTranscript.TryParseReadBack(row.Text, out _), timeout);
        if (answer is null) {
            throw new TimeoutException($"The World did not complete its script/read-back; inspect its stdout.log and stderr.log (pid {ProcessId}).");
        }
        _ = MotionTranscript.TryParseReadBack(answer.Text, out var tick);
        return new(tick, LinesFrom(mark).Where(row => row.Index <= answer.Index).ToArray());
    }
}
