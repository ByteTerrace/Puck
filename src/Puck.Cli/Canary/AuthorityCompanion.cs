using System.Diagnostics;
using System.Globalization;
using System.Text;
using Puck.Hosting;

namespace Puck.Cli.Canary;

/// <summary>Owns a federation authority's process and live transcript. Disposal asks it to quit, kills it after
/// its quit grace, and drains its output through <see cref="ChildProcess.ExitDrainGrace"/>.</summary>
public sealed class AuthorityCompanion : IDisposable {
    // Its own exit backstop outlasts listening, quitting and the client's whole timeout.
    internal const int ListenSeconds = 5;
    internal const int QuitGraceSeconds = 10;

    private readonly Process m_process;
    private readonly string m_quitInput;
    private readonly TimeProvider m_clock;

    private readonly CancellationTokenSource m_release = new();

    private readonly StreamReader m_output;
    private readonly StreamReader m_errors;
    private readonly Task<bool>[] m_pumps;

    private readonly StringBuilder m_stdout = new();
    private readonly StringBuilder m_stderr = new();
    private readonly Lock m_gate = new();

    private CancellationTokenRegistration m_cancellation;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="AuthorityCompanion"/> class, taking ownership of a started
    /// process whose input, output and error streams are redirected and not yet read.</summary>
    /// <param name="process">The process; no other reader may consume its output streams.</param>
    /// <param name="quitInput">The complete input that asks the authority to quit.</param>
    /// <param name="clock">The clock for the post-exit drain grace; system time when absent.</param>
    public AuthorityCompanion(Process process, string quitInput, TimeProvider? clock = null) {
        m_process = process;
        m_quitInput = quitInput;
        m_clock = (clock ?? TimeProvider.System);
        m_output = ChildProcess.OpenOutputReader(reader: process.StandardOutput, release: m_release.Token);
        m_errors = ChildProcess.OpenOutputReader(reader: process.StandardError, release: m_release.Token);
        m_pumps = [PumpAsync(reader: m_output, text: m_stdout), PumpAsync(reader: m_errors, text: m_stderr)];
    }

    /// <summary>Gets the error transcript observed so far, complete after disposal.</summary>
    public string Stderr { get { lock (m_gate) { return m_stderr.ToString(); } } }
    /// <summary>Gets the output transcript observed so far, complete after disposal.</summary>
    public string Stdout { get { lock (m_gate) { return m_stdout.ToString(); } } }

    private async Task<bool> PumpAsync(StreamReader reader, StringBuilder text) {
        while (await reader.ReadLineAsync().ConfigureAwait(continueOnCapturedContext: false) is { } line) {
            lock (m_gate) {
                text.AppendLine(value: line);
            }
        }
        return true;
    }

    /// <summary>Stops the authority, settles both readers within the shared post-exit drain, and closes its streams.</summary>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        try {
            if (!m_process.HasExited) {
                try {
                    m_process.StandardInput.Write(value: m_quitInput);
                } catch (IOException) {
                    // The companion already closed its end; the wait below observes its exit.
                }
                ChildProcess.CloseInput(input: m_process.StandardInput);
                if (!m_process.WaitForExit(timeout: TimeSpan.FromSeconds(value: QuitGraceSeconds))) {
                    Kill();
                }
            }
            // There are no Process event readers: this wait observes only process exit, not inherited pipe handles.
            m_process.WaitForExitAsync().GetAwaiter().GetResult();
            ChildProcess.DrainAfterExitAsync(
                clock: m_clock,
                pumps: m_pumps,
                release: m_release
            ).GetAwaiter().GetResult();
        } finally {
            m_cancellation.Dispose();
            m_output.Dispose();
            m_errors.Dispose();
            m_process.StandardInput.BaseStream.Dispose();
            m_process.Dispose();
            m_release.Dispose();
        }
    }

    internal static AuthorityCompanion Start(string artifact, string world, string stateDirectory, string federationKeyPath, int exitAfterSeconds, string quitInput, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        var process = ChildProcess.StartRedirected(
            arguments: [artifact, "--world", world, "--state-dir", stateDirectory, "--exit-after-seconds", exitAfterSeconds.ToString(provider: CultureInfo.InvariantCulture), "--headless", "true", "--federation-key-file", federationKeyPath],
            fileName: "dotnet"
        );
        var companion = new AuthorityCompanion(process: process, quitInput: quitInput);

        companion.m_cancellation = cancellationToken.Register(callback: companion.Kill);
        return companion;
    }

    private void Kill() {
        try {
            m_process.Kill(entireProcessTree: true);
        } catch (InvalidOperationException) {
            // It had already exited.
        }
    }

    /// <summary>Waits for the authority to report a bound listener, or for it to exit.</summary>
    /// <param name="timeout">The maximum time to wait for the listener.</param>
    /// <returns>Whether the listener was observed before exit or timeout.</returns>
    public bool WaitUntilListening(TimeSpan timeout) {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < timeout) {
            lock (m_gate) {
                if (m_stderr.ToString().Contains(comparisonType: StringComparison.Ordinal, value: "[world.listen: bound ")) {
                    return true;
                }
            }
            if (m_process.HasExited) { return false; }
            _ = m_process.WaitForExit(milliseconds: 25);
        }
        return false;
    }
}
