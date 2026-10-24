using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using ModelContextProtocol.Protocol;

using Puck.Hosting;

namespace Puck.Mcp;

/// <summary>What the Operator adapter needs to start Worlds: the checkout it serves and the machine's GPU lease.</summary>
/// <param name="Checkout">The adapter's own Puck checkout, the default checkout a start boots from; its repository's
/// worktrees are the only other checkouts a start accepts.</param>
/// <param name="AcquireGpuLease">Takes the machine's exclusive GPU lease for a holder, waiting at most the given time,
/// and returns the held lease, which releases on disposal; throws <see cref="TimeoutException"/> when it stays held.</param>
public sealed record OperatorWorldLaunch(string Checkout, Func<string, TimeSpan, CancellationToken, Task<IDisposable>> AcquireGpuLease);

/// <summary>
/// The Worlds the Operator adapter started: <c>puck_world_start</c> boots one from a checkout's Release build, and
/// <c>puck_world_stop</c> ends one of these by its recorded process and nothing else. A started World passes
/// <c>--devices none</c> unless the owner-play option names otherwise, gets its own state directory and
/// <c>--control</c>, and a windowed one holds the GPU lease until it exits. Disposal stops every World still running,
/// so none outlives the adapter that started it.
/// </summary>
internal sealed class OperatorWorlds(OperatorWorldLaunch? launch, TimeProvider clock) : IDisposable {
    internal const int MaxStartSeconds = 540;

    private static readonly TimeSpan AttachPoll = TimeSpan.FromMilliseconds(milliseconds: 250);
    private static readonly TimeSpan DisposeGrace = TimeSpan.FromSeconds(seconds: 10);

    private readonly ConcurrentDictionary<int, Started> m_started = new();

    private int m_runs;

    private sealed class Started(Process process, string document, string checkout, string build, string runDirectory, IDisposable? lease) {
        public string? AttachmentId;

        public readonly Process Process = process;
        public readonly string Build = build;
        public readonly string Checkout = checkout;
        public readonly string Document = document;
        public readonly string RunDirectory = runDirectory;
        public readonly string StderrLog = Path.Combine(path1: runDirectory, path2: "stderr.log");
        public readonly string StdoutLog = Path.Combine(path1: runDirectory, path2: "stdout.log");
        public readonly string StateDirectory = Path.Combine(path1: runDirectory, path2: "state");
        public IDisposable? Lease = lease;
        public readonly bool Leased = (lease is not null);
        public Task Exited = Task.CompletedTask;
    }

    /// <summary>Gets whether this adapter started the World a process id names.</summary>
    /// <param name="processId">The World's process id.</param>
    /// <returns><see langword="true"/> for a World this adapter started that has not been stopped.</returns>
    public bool Owns(int processId) => m_started.ContainsKey(key: processId);
    /// <summary>Stops every World this adapter started: each is asked to quit, then its process tree is killed.</summary>
    public void Dispose() {
        foreach (var started in m_started.Values) {
            try {
                _ = Stop(grace: DisposeGrace, started: started);
            } catch (Exception error) when ((error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)) { }
        }
    }
    /// <summary>Starts a World and returns once it accepts an Operator attachment.</summary>
    /// <param name="arguments">The tool's arguments.</param>
    /// <param name="attachment">The adapter's attachments, which the World's must join.</param>
    /// <param name="cancellationToken">Cancels the start; a World already launched is stopped.</param>
    /// <returns>The started World, or a refusal.</returns>
    public async Task<CallToolResult> StartAsync(IDictionary<string, JsonElement>? arguments, OperatorAttachment attachment, CancellationToken cancellationToken) {
        string? checkoutArgument = null;
        var document = "worlds/debug";
        var holder = "puck-mcp";
        var ownerPlay = false;
        var seconds = 300;
        var windowed = false;

        foreach (var (key, value) in (arguments ?? new Dictionary<string, JsonElement>())) {
            if ((key == "document") && (value.ValueKind == JsonValueKind.String) && !string.IsNullOrWhiteSpace(value: value.GetString())) {
                document = value.GetString()!;
            } else if ((key == "checkout") && (value.ValueKind == JsonValueKind.String) && !string.IsNullOrWhiteSpace(value: value.GetString())) {
                checkoutArgument = value.GetString();
            } else if ((key == "windowed") && (value.ValueKind is JsonValueKind.True or JsonValueKind.False)) {
                windowed = value.GetBoolean();
            } else if ((key == "ownerPlay") && (value.ValueKind is JsonValueKind.True or JsonValueKind.False)) {
                ownerPlay = value.GetBoolean();
            } else if ((key == "holder") && (value.ValueKind == JsonValueKind.String) && !string.IsNullOrWhiteSpace(value: value.GetString()) && (value.GetString()!.IndexOfAny(anyOf: ['\r', '\n']) < 0)) {
                holder = value.GetString()!;
            } else if ((key == "timeoutSeconds") && (value.ValueKind == JsonValueKind.Number) && value.TryGetInt32(value: out var parsed) && (parsed is >= 1 and <= MaxStartSeconds)) {
                seconds = parsed;
            } else {
                return Refuse(output: $"Invalid argument: {key}.");
            }
        }

        if (launch is null) {
            return Refuse(output: "This adapter serves no Puck checkout, so it starts no World; run it from a checkout's build (.claude/hooks/puck-mcp.ps1).");
        }
        if (!attachment.Follows) {
            return Refuse(output: "This adapter is pinned to one World by --attach, so a World it started could not be reached; run it without --attach to start Worlds.");
        }

        var (checkout, refusal) = await CheckoutAsync(cancellationToken: cancellationToken, requested: checkoutArgument).ConfigureAwait(continueOnCapturedContext: false);

        if (checkout is null) {
            return Refuse(output: refusal!);
        }

        var path = Path.GetFullPath(path: Path.Combine(path1: checkout, path2: document));
        var worlds = (Path.Combine(path1: checkout, path2: "worlds") + Path.DirectorySeparatorChar);

        if (!path.StartsWith(comparisonType: PathComparison, value: worlds) || !(File.Exists(path: path) || Directory.Exists(path: path))) {
            return Refuse(output: $"Refused: {path} is not a world document or package under {worlds}; a start boots only the checkout's own worlds.");
        }

        var build = NewestBuild(checkout: checkout);

        if (build is null) {
            return Refuse(output: $"Refused: {checkout} has no Release build of src/Puck.World; build it first (dotnet build src/Puck.World -c Release -nodeReuse:false).");
        }

        var begun = clock.GetTimestamp();
        var budget = TimeSpan.FromSeconds(seconds: seconds);
        IDisposable? lease = null;

        if (windowed) {
            try {
                lease = await launch.AcquireGpuLease(holder, budget, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            } catch (TimeoutException error) {
                return Refuse(output: $"Refused: the GPU lease stayed held for {seconds} s, so no windowed World was started ({error.Message}). Retry the start, or start headless.");
            }
        }

        Started started;

        try {
            started = Launch(build: build, checkout: checkout, document: path, lease: lease, ownerPlay: ownerPlay, target: attachment.Target, windowed: windowed);
        } catch (Exception error) when ((error is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)) {
            lease?.Dispose();

            return Refuse(output: $"Refused: the World could not be launched ({error.Message}).");
        }

        try {
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();

                if (started.Process.HasExited) {
                    _ = Stop(grace: TimeSpan.Zero, started: started);

                    return Refuse(output: $"Refused: World process {started.Process.Id} exited with code {started.Process.ExitCode} before it accepted an attachment.{Tail(path: started.StderrLog)}", started: started);
                }

                var world = await attachment.RunAsync(
                    call: static (client, _) => Task.FromResult<OperatorMcpWorld?>(result: OperatorMcpServer.Describe(info: client.Endpoint)),
                    cancellationToken: cancellationToken,
                    refuse: static _ => null,
                    world: started.Process.Id.ToString(provider: CultureInfo.InvariantCulture)
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (world is { } reached) {
                    started.AttachmentId = reached.AttachmentId;

                    return Result(metadata: Metadata(output: $"Started World process {started.Process.Id} ({(windowed ? "windowed, holding the GPU lease" : "headless")}, --devices {(ownerPlay ? "all" : "none")}) from {build}; name it by processId or attachmentId in every call, read its output with host.log, and end it with puck_world_stop.", started: started, status: "started", world: reached));
                }
                if (clock.GetElapsedTime(startingTimestamp: begun) >= budget) {
                    _ = Stop(grace: TimeSpan.Zero, started: started);

                    return Refuse(output: $"Refused: World process {started.Process.Id} did not accept an attachment within {seconds} s and was stopped.{Tail(path: started.StderrLog)}", started: started);
                }

                await Task.Delay(cancellationToken: cancellationToken, delay: AttachPoll, timeProvider: clock).ConfigureAwait(continueOnCapturedContext: false);
            }
        } catch (OperationCanceledException) {
            _ = Stop(grace: TimeSpan.Zero, started: started);

            throw;
        }
    }
    /// <summary>Stops a World this adapter started.</summary>
    /// <param name="arguments">The tool's arguments.</param>
    /// <param name="cancellationToken">Cancels waiting for the World to quit; its process tree is then killed.</param>
    /// <returns>The World's exit, or a refusal that touched nothing.</returns>
    public async Task<CallToolResult> StopAsync(IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken) {
        string? selector = null;
        var timeout = 30_000;

        foreach (var (key, value) in (arguments ?? new Dictionary<string, JsonElement>())) {
            if ((key == "world") && (value.ValueKind == JsonValueKind.String) && !string.IsNullOrWhiteSpace(value: value.GetString())) {
                selector = value.GetString()!.Trim();
            } else if ((key == "timeoutMs") && (value.ValueKind == JsonValueKind.Number) && value.TryGetInt32(value: out var parsed) && (parsed is >= 1 and <= ControlLimits.TimeoutMilliseconds)) {
                timeout = parsed;
            } else {
                return Refuse(output: $"Invalid argument: {key}.");
            }
        }

        if (selector is null) {
            return Refuse(output: "Invalid argument: world. Give the processId or attachmentId puck_world_start returned.");
        }

        var started = m_started.Values.FirstOrDefault(predicate: entry =>
            (string.Equals(a: entry.AttachmentId, b: selector, comparisonType: StringComparison.OrdinalIgnoreCase) ||
            (int.TryParse(s: selector, style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out var processId) && (processId == entry.Process.Id))));

        if (started is null) {
            return Refuse(output: $"Refused: '{selector}' names no World this adapter started with puck_world_start, so nothing was stopped; a World it did not start is never touched.");
        }

        var stopped = await Task.Run(function: () => Stop(started: started, grace: TimeSpan.FromMilliseconds(milliseconds: timeout)), cancellationToken: CancellationToken.None).WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return Result(metadata: Metadata(
            output: $"World process {started.Process.Id} {(stopped.Killed ? "did not quit in time and its process tree was killed" : "quit")}; exit code {stopped.ExitCode}.{(stopped.LeaseReleased ? " Its GPU lease is released." : string.Empty)}",
            started: started,
            status: "stopped",
            stop: stopped
        ));
    }

    private static StringComparison PathComparison => (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: path));
    // The newest Release build of the World in a checkout, whatever its target framework.
    private static string? NewestBuild(string checkout) {
        var release = Path.Combine(path1: checkout, path2: "src", path3: "Puck.World", path4: "bin/Release");

        return (Directory.Exists(path: release)
            ? Directory.GetDirectories(path: release, searchPattern: "net*")
                .Select(selector: directory => new FileInfo(fileName: Path.Combine(path1: directory, path2: "Puck.World.dll")))
                .Where(predicate: static file => file.Exists)
                .OrderByDescending(keySelector: static file => file.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName
            : null);
    }
    private static CallToolResult Refuse(string output, Started? started = null) => Result(metadata: Metadata(output: output, started: started, status: "refused"));
    private static OperatorMcpWorldRun Metadata(string output, Started? started, string status, OperatorMcpWorld? world = null, (int? ExitCode, bool Killed, bool LeaseReleased)? stop = null) => new(
        Build: started?.Build,
        Checkout: started?.Checkout,
        ExitCode: stop?.ExitCode,
        IsError: (status == "refused"),
        Killed: (stop?.Killed ?? false),
        LeaseReleased: (stop?.LeaseReleased ?? false),
        Output: output,
        ProcessId: started?.Process.Id,
        StateDirectory: started?.StateDirectory,
        Status: status,
        StderrLog: started?.StderrLog,
        StdoutLog: started?.StdoutLog,
        World: world
    );
    private static CallToolResult Result(OperatorMcpWorldRun metadata) {
        var element = JsonSerializer.SerializeToElement(value: metadata, jsonTypeInfo: OperatorMcpJson.Default.OperatorMcpWorldRun);

        return new() {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            IsError = metadata.IsError,
            StructuredContent = element,
        };
    }
    // The last lines a World wrote to standard error, for a refusal that says why it never attached.
    private static string Tail(string path) {
        try {
            using var stream = new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: path, share: FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream: stream, encoding: Encoding.UTF8);
            var lines = reader.ReadToEnd().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n');

            return ((lines.Length == 0) ? string.Empty : $"{Environment.NewLine}Last stderr lines:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: lines.TakeLast(count: 8).Select(selector: static line => line.TrimEnd(trimChar: '\r')))}");
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
            return string.Empty;
        }
    }
    // The checkout a start boots from: the adapter's own, or another worktree of its repository by absolute path.
    private async Task<(string? Checkout, string? Refusal)> CheckoutAsync(string? requested, CancellationToken cancellationToken) {
        var own = Normalize(path: launch!.Checkout);
        var wanted = ((requested is null) ? own : Normalize(path: requested));

        if (string.Equals(a: wanted, b: own, comparisonType: PathComparison)) {
            return (own, null);
        }

        ChildProcessResult listing;

        try {
            listing = await ChildProcess.RunAsync(
                arguments: ["-C", own, "worktree", "list", "--porcelain"],
                cancellationToken: cancellationToken,
                clock: clock,
                fileName: "git",
                // The adapter is already reading its MCP pipe. Git for Windows probes inherited stdin during
                // startup; that probe waits behind the adapter's pending read. Give this noninteractive query EOF.
                input: string.Empty,
                timeout: TimeSpan.FromSeconds(seconds: 30)
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception error) when ((error is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            return (null, $"Refused: git failed to start while listing the worktrees of {own}: {error.Message}");
        }

        if (listing.TimedOut) {
            return (null, $"Refused: git timed out after 30 s while listing the worktrees of {own}; its process tree was stopped. {listing.Stderr.Trim()}");
        }
        if (listing.ExitCode != 0) {
            return (null, $"Refused: git could not list the worktrees of {own} (exit {listing.ExitCode}): {listing.Stderr.Trim()}");
        }

        var worktrees = listing.Stdout.Split(separator: '\n')
            .Where(predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "worktree "))
            .Select(selector: static line => Normalize(path: line["worktree ".Length..].Trim()));

        return (worktrees.Any(predicate: worktree => string.Equals(a: worktree, b: wanted, comparisonType: PathComparison))
            ? (wanted, null)
            : (null, $"Refused: {wanted} is not a checkout of this repository ({own} or one of its worktrees); a start boots only those."));
    }
    private Started Launch(string build, string checkout, string document, IDisposable? lease, bool ownerPlay, string target, bool windowed) {
        var runDirectory = Path.Combine(
            path1: target,
            path2: "puck-mcp-worlds",
            path3: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Environment.ProcessId}-{Interlocked.Increment(location: ref m_runs)}-{Guid.NewGuid():N}")
        );

        _ = Directory.CreateDirectory(path: Path.Combine(path1: runDirectory, path2: "state"));

        var info = new ProcessStartInfo(fileName: "dotnet") {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = checkout,
        };

        foreach (var argument in new[] {
            build,
            "--world", document,
            "--headless", (windowed ? "false" : "true"),
            "--devices", (ownerPlay ? "all" : "none"),
            "--state-dir", Path.Combine(path1: runDirectory, path2: "state"),
            "--control",
        }) {
            info.ArgumentList.Add(item: argument);
        }

        // The World publishes its attachment file in its temporary directory, which must be the one this adapter follows.
        info.Environment["TEMP"] = target;
        info.Environment["TMP"] = target;
        info.Environment["TMPDIR"] = target;

        var process = (Process.Start(startInfo: info) ?? throw new InvalidOperationException(message: "dotnet did not start."));
        var started = new Started(build: build, checkout: checkout, document: document, lease: lease, process: process, runDirectory: runDirectory);

        started.Exited = WatchAsync(started: started);
        m_started[process.Id] = started;

        return started;
    }
    // Copies both streams to the run's log files, and releases the lease once the World has exited.
    private static async Task WatchAsync(Started started) {
        async Task CopyAsync(Stream source, string path) {
            await using var file = new FileStream(access: FileAccess.Write, bufferSize: 0, mode: FileMode.Create, path: path, share: FileShare.ReadWrite | FileShare.Delete);

            try {
                await source.CopyToAsync(destination: file).ConfigureAwait(continueOnCapturedContext: false);
            } catch (IOException) { }
        }

        var pumps = Task.WhenAll(
            CopyAsync(path: started.StdoutLog, source: started.Process.StandardOutput.BaseStream),
            CopyAsync(path: started.StderrLog, source: started.Process.StandardError.BaseStream)
        );

        try {
            await started.Process.WaitForExitAsync().ConfigureAwait(continueOnCapturedContext: false);
            _ = await Task.WhenAny(task1: pumps, task2: Task.Delay(delay: ChildProcess.ExitDrainGrace)).ConfigureAwait(continueOnCapturedContext: false);
        } finally {
            Interlocked.Exchange(location1: ref started.Lease, value: null)?.Dispose();
        }
    }
    // Asks the World to quit, kills its tree once the grace passes, and waits for its lease to release.
    private (int? ExitCode, bool Killed, bool LeaseReleased) Stop(Started started, TimeSpan grace) {
        var killed = false;

        if (!started.Process.HasExited) {
            try {
                started.Process.StandardInput.BaseStream.Write(buffer: "quit\n"u8);
                started.Process.StandardInput.BaseStream.Flush();
            } catch (Exception error) when ((error is IOException or ObjectDisposedException)) { }

            if (!started.Process.WaitForExit(timeout: grace)) {
                killed = true;

                try {
                    started.Process.Kill(entireProcessTree: true);
                } catch (InvalidOperationException) { }

                started.Process.WaitForExit();
            }
        }

        started.Exited.GetAwaiter().GetResult();
        _ = m_started.TryRemove(key: started.Process.Id, value: out _);

        try {
            Directory.Delete(path: started.StateDirectory, recursive: true);
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { }

        return (started.Process.ExitCode, killed, started.Leased);
    }
}
