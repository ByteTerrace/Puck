namespace Puck.Cli.Motion;

/// <summary>The ordered script output through the shared world.at read-back barrier.</summary>
public sealed record MotionScriptResult(ulong Tick, IReadOnlyList<WorldOutputLine> Lines);

/// <summary>Runs console lines on a driven World and reads back the tick they completed on.</summary>
public static class MotionWorldScript {
    /// <summary>Runs console lines in order, then reads the completed tick through the same session. No sleep or
    /// wall-clock injection chooses a script tick; world.at owns every pinned operation.</summary>
    /// <param name="process">The driven World.</param>
    /// <param name="lines">The console lines.</param>
    /// <param name="timeout">How long the read-back may take.</param>
    /// <returns>The tick the lines completed on and every line the World wrote up to its answer.</returns>
    /// <exception cref="IOException">The World closed its console input.</exception>
    /// <exception cref="TimeoutException">The World did not answer the read-back in time.</exception>
    public static MotionScriptResult RunScript(this WorldProcess process, IReadOnlyList<string> lines, TimeSpan timeout) {
        var mark = process.LineCount;
        foreach (var line in lines) {
            if (!process.Send(line)) { throw new IOException("The World closed its console input."); }
        }
        if (!process.Send("world.at")) { throw new IOException("The World closed its console input before its read-back."); }
        var answer = process.WaitFor(mark, static row => row.Stream == CliProcessOutputStream.Stdout &&
            MotionTranscript.TryParseReadBack(row.Text, out _), timeout);
        if (answer is null) {
            throw new TimeoutException($"The World did not complete its script/read-back; inspect its stdout.log and stderr.log (pid {process.ProcessId}).");
        }
        _ = MotionTranscript.TryParseReadBack(answer.Text, out var tick);
        return new(tick, process.LinesFrom(mark).Where(row => row.Index <= answer.Index).ToArray());
    }
}
