using System.Text;

namespace Puck.Cli.Tests;

/// <summary>
/// Captures what a verb writes to <see cref="Console.Out"/> and <see cref="Console.Error"/> without racing any
/// other test. The process-global writers are replaced once by routers that forward to the writers bound on the
/// current async flow, or to the original writers when none is bound, so concurrent captures never see each
/// other's output and never restore a writer another capture installed.
/// </summary>
internal static class ConsoleCapture {
    private static readonly AsyncLocal<TextWriter?> CurrentError = new();
    private static readonly AsyncLocal<TextWriter?> CurrentOut = new();
    private static readonly Lock InstallLock = new();

    private static bool Installed;

    /// <summary>Runs <paramref name="run"/> with standard output and standard error captured together.</summary>
    public static (int ExitCode, string Output) Run(Func<int> run) {
        using var writer = new StringWriter();
        var exitCode = Run(
            error: writer,
            output: writer,
            run: run
        );

        return (exitCode, writer.ToString());
    }
    /// <summary>Runs <paramref name="run"/> with standard output and standard error captured separately.</summary>
    public static (int ExitCode, string Output, string Error) RunSplit(Func<int> run) {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = Run(
            error: error,
            output: output,
            run: run
        );

        return (exitCode, output.ToString(), error.ToString());
    }
    /// <summary>Awaits <paramref name="run"/> with standard output and standard error captured separately.</summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunSplitAsync(Func<Task<int>> run) {
        using var output = new StringWriter();
        using var error = new StringWriter();

        using (Bind(
            error: error,
            output: output
        )) {
            var exitCode = await run();

            return (exitCode, output.ToString(), error.ToString());
        }
    }

    private static int Run(TextWriter output, TextWriter error, Func<int> run) {
        using (Bind(
            error: error,
            output: output
        )) {
            return run();
        }
    }
    // Binds the writers on the current async flow until the scope is disposed. A value set here flows into
    // everything the caller starts afterwards, including work it awaits.
    private static Scope Bind(TextWriter output, TextWriter error) {
        Install();

        var synchronizedOutput = TextWriter.Synchronized(writer: output);
        var synchronizedError = (ReferenceEquals(
            objA: output,
            objB: error
        )
            ? synchronizedOutput
            : TextWriter.Synchronized(writer: error)
        );
        var previousOut = CurrentOut.Value;
        var previousError = CurrentError.Value;

        CurrentOut.Value = synchronizedOutput;
        CurrentError.Value = synchronizedError;

        return new Scope(
            PreviousError: previousError,
            PreviousOut: previousOut
        );
    }
    private static void Install() {
        lock (InstallLock) {
            if (Installed) {
                return;
            }

            Console.SetOut(newOut: new Router(
                current: CurrentOut,
                fallback: Console.Out
            ));
            Console.SetError(newError: new Router(
                current: CurrentError,
                fallback: Console.Error
            ));
            Installed = true;
        }
    }

    private readonly record struct Scope(TextWriter? PreviousOut, TextWriter? PreviousError) : IDisposable {
        public void Dispose() {
            CurrentOut.Value = PreviousOut;
            CurrentError.Value = PreviousError;
        }
    }
    private sealed class Router(AsyncLocal<TextWriter?> current, TextWriter fallback) : TextWriter {
        public override Encoding Encoding => Target.Encoding;

        private TextWriter Target => (current.Value ?? fallback);

        public override void Flush() => Target.Flush();
        public override void Write(char value) => Target.Write(value: value);
        public override void Write(char[] buffer, int index, int count) => Target.Write(
            buffer: buffer,
            count: count,
            index: index
        );
        public override void Write(ReadOnlySpan<char> buffer) => Target.Write(buffer: buffer);
        public override void Write(string? value) => Target.Write(value: value);
        public override void WriteLine() => Target.WriteLine();
        public override void WriteLine(ReadOnlySpan<char> buffer) => Target.WriteLine(buffer: buffer);
        public override void WriteLine(string? value) => Target.WriteLine(value: value);
        public override Task FlushAsync() => Target.FlushAsync();
        public override Task WriteAsync(char value) => Target.WriteAsync(value: value);
        public override Task WriteAsync(string? value) => Target.WriteAsync(value: value);
        public override Task WriteLineAsync(string? value) => Target.WriteLineAsync(value: value);
    }
}
