using System.Globalization;

using Puck.Cli.Motion;
using Puck.Hosting;
using Puck.Networking;

namespace Puck.Cli.Sweep;

/// <summary>One answer a World gave a sweep line or script.</summary>
/// <param name="Ok">Whether every line completed.</param>
/// <param name="Output">The answer of the script's last line, or the refusal.</param>
/// <param name="Refusals">Each refused line with its refusal.</param>
public sealed record SweepAnswer(bool Ok, string Output, IReadOnlyList<string> Refusals);
/// <summary>A World a sweep drives through its Operator attachment: one the sweep booted (headless, or windowed under
/// the GPU lease), or a running World it attached to. Lines run one call at a time on the attachment's ordered
/// session, so a write is settled before the next line reads.</summary>
public sealed class SweepWorld : IDisposable {
    private const string AttachmentLine = "[world.control: operator attachment ";
    private const string CapabilityPattern = "puck-control-*.json";

    private readonly LocalControlClient m_client;
    private readonly IDisposable? m_lease;
    private readonly MotionWorldProcess? m_process;

    private SweepWorld(LocalControlClient client, MotionWorldProcess? process, IDisposable? lease, string leg) {
        m_client = client;
        m_lease = lease;
        m_process = process;
        Leg = leg;
    }

    /// <summary>Gets the leg the World serves: <see cref="SweepLegs.Headless"/> when it composes no renderer, else
    /// <see cref="SweepLegs.Windowed"/>.</summary>
    public string Leg { get; }
    /// <summary>Gets the World's process id.</summary>
    public int ProcessId => m_client.Endpoint.ProcessId;
    /// <summary>Gets whether the sweep booted this World, and so stops it.</summary>
    public bool Owned => (m_process is not null);
    /// <summary>Gets whether the attachment has closed: the World exited, or a call failed or timed out.</summary>
    public bool Gone => m_client.IsClosed;

    /// <summary>Attaches to a running World by its process id, attachment id or attachment file.</summary>
    /// <param name="world">The process id, attachment id or attachment file path.</param>
    /// <param name="session">The attached World.</param>
    /// <param name="error">Why no World was attached.</param>
    /// <returns><see langword="true"/> when a World was attached.</returns>
    public static bool TryAttach(string world, out SweepWorld? session, out string error) {
        session = null;

        var path = (File.Exists(path: world) ? Path.GetFullPath(path: world) : Find(world: world));

        if (path is null) {
            error = $"no running World with process or attachment id {world} publishes an Operator attachment in {Path.GetTempPath()}";

            return false;
        }

        return TryConnect(error: out error, lease: null, path: path, process: null, session: out session);
    }
    /// <summary>Boots a World and attaches to it.</summary>
    /// <param name="artifact">The World build's entry assembly.</param>
    /// <param name="arguments">The World's arguments, <c>--control</c> excluded.</param>
    /// <param name="workingDirectory">The directory the World runs in.</param>
    /// <param name="logDirectory">The directory that receives the World's <c>stdout.log</c> and <c>stderr.log</c>.</param>
    /// <param name="lease">The GPU lease a windowed World holds until it stops, or <see langword="null"/>; owned by
    /// the returned World, or released on failure.</param>
    /// <param name="bootTimeout">How long the World may take to publish its attachment.</param>
    /// <param name="session">The booted World.</param>
    /// <param name="error">Why no World was booted.</param>
    /// <returns><see langword="true"/> when the World booted and attached.</returns>
    public static bool TryBoot(string artifact, IReadOnlyList<string> arguments, string workingDirectory, string logDirectory, IDisposable? lease, TimeSpan bootTimeout, out SweepWorld? session, out string error) {
        var process = MotionWorldProcess.Start(
            arguments: [.. arguments, "--control"],
            artifact: artifact,
            logDirectory: logDirectory,
            workingDirectory: workingDirectory
        );
        var line = process.WaitFor(
            accept: static line => line.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: AttachmentLine),
            from: 0,
            timeout: bootTimeout
        );

        if (line is null) {
            process.Dispose();
            lease?.Dispose();
            session = null;
            error = $"the World never published its Operator attachment; its output is in {logDirectory}";

            return false;
        }

        var path = line.Text[AttachmentLine.Length..].TrimEnd(trimChar: ']');

        if (!TryConnect(error: out error, lease: lease, path: path, process: process, session: out session)) {
            process.Dispose();
            lease?.Dispose();

            return false;
        }

        return true;
    }
    /// <summary>Runs a console line, or several separated by newlines.</summary>
    /// <param name="script">The line or lines.</param>
    /// <param name="timeoutSeconds">How long the whole call may take, at most 120 seconds.</param>
    /// <returns>The answer.</returns>
    public SweepAnswer Run(string script, int timeoutSeconds = 60) {
        ControlResponse response;

        try {
            response = m_client.ExecuteAsync(
                command: script,
                operation: "exec",
                timeoutMilliseconds: (Math.Clamp(max: 120, min: 1, value: timeoutSeconds) * 1000)
            ).GetAwaiter().GetResult();
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)) {
            return new SweepAnswer(Ok: false, Output: $"the attachment failed: {exception.Message}", Refusals: [$"{script}: the attachment failed"]);
        }

        // A truncated answer is the head of a longer one: a parser reading it would report the cut as the World's.
        if (response.Commands is { Length: > 0 } commands) {
            var refusals = commands.Where(predicate: static command => (command.IsError || command.Truncated)).Select(selector: static command => $"{command.Command}: {(command.Truncated ? Truncation(output: command.Output) : command.Output)}").ToArray();

            return new SweepAnswer(Ok: (refusals.Length == 0), Output: commands[^1].Output, Refusals: refusals);
        }

        return ((response.IsError || response.Truncated)
            ? new SweepAnswer(Ok: false, Output: response.Output, Refusals: [$"{script}: {(response.Truncated ? Truncation(output: response.Output) : response.Output)}"])
            : new SweepAnswer(Ok: true, Output: response.Output, Refusals: []));
    }
    /// <summary>Takes a settled capture of the composed frame.</summary>
    /// <param name="output">The capture's answer: its tick, state hash and pose, or the refusal.</param>
    /// <returns>The PNG's bytes, or <see langword="null"/> when the capture was refused.</returns>
    public byte[]? Capture(out string output) {
        try {
            var response = m_client.ExecuteAsync(
                capture: new ControlCapture(Mode: ControlCapture.Settled),
                command: null,
                operation: "capture",
                timeoutMilliseconds: 120_000
            ).GetAwaiter().GetResult();

            output = response.Output;

            return (response.IsError ? null : response.Png);
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)) {
            output = $"the attachment failed: {exception.Message}";

            return null;
        }
    }
    /// <summary>Stops a World the sweep booted, sending <c>quit</c> and killing it when it does not exit in time, and
    /// releases its GPU lease; closes only the attachment of a World it attached to.</summary>
    public void Dispose() {
        if (m_process is { } process) {
            try {
                _ = m_client.ExecuteAsync(command: "quit", operation: "exec", timeoutMilliseconds: 10_000).GetAwaiter().GetResult();
            } catch (Exception exception) when ((exception is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)) {
                // The World closed the attachment as it quit.
            }

            _ = process.WaitForExit(timeout: TimeSpan.FromSeconds(value: 60));
            process.Dispose();
        }

        m_client.Dispose();
        m_lease?.Dispose();
    }

    private static bool TryConnect(string path, MotionWorldProcess? process, IDisposable? lease, out SweepWorld? session, out string error) {
        LocalControlClient client;

        try {
            client = LocalControlClient.ConnectAsync(attachmentPath: path).GetAwaiter().GetResult();
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)) {
            session = null;
            error = $"the World's Operator attachment {path} did not accept a session: {exception.Message}";

            return false;
        }

        // A World that composes no renderer refuses the readiness wait by saying so.
        var probe = client.ExecuteAsync(command: "world.wait ready 1", operation: "exec", timeoutMilliseconds: 10_000).GetAwaiter().GetResult();
        var leg = ((probe.IsError && probe.Output.Contains(comparisonType: StringComparison.Ordinal, value: "composes no renderer")) ? SweepLegs.Headless : SweepLegs.Windowed);

        session = new SweepWorld(client: client, lease: lease, leg: leg, process: process);
        error = string.Empty;

        return true;
    }
    private static string Truncation(string output) {
        var marker = output.LastIndexOf(comparisonType: StringComparison.Ordinal, value: "[control: output truncated");

        return ((marker < 0) ? "the answer was truncated" : output[marker..].Trim());
    }
    private static string? Find(string world) {
        foreach (var file in Directory.EnumerateFiles(path: Path.GetTempPath(), searchPattern: CapabilityPattern)) {
            LocalEndpointInfo info;

            try {
                info = LocalEndpointCapability.ReadDescriptor(path: file).Info;
            } catch (Exception exception) when ((exception is UnauthorizedAccessException or IOException or InvalidDataException or System.Text.Json.JsonException)) {
                continue;
            }

            if (info.IsHostRunning() && (string.Equals(a: info.Id, b: world, comparisonType: StringComparison.OrdinalIgnoreCase) || (int.TryParse(provider: CultureInfo.InvariantCulture, result: out var processId, s: world, style: NumberStyles.None) && (processId == info.ProcessId)))) {
                return file;
            }
        }

        return null;
    }
}
