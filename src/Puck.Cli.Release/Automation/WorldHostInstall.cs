using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>A hosting machine's bootstrap, <c>puck world host install</c>: it runs as root from the host release the
/// machine's installer just installed, reads its retained inputs by their pin from the owner's store, re-checks the boot
/// guard under the machine's release lock, writes the configuration and keys as root-only files the services load as
/// systemd credentials, writes and starts the sandboxed units that run the installed CLI directly, and waits for what a
/// candidate reaches before its release commits: private health and, with MCP, the TLS front. A wait that fails reports
/// its cause last, since only the tail of the extension's output reaches the deploying controller.</summary>
[System.Runtime.Versioning.SupportedOSPlatform(platformName: "linux")]
public static class WorldHostInstall {
    private static readonly TimeSpan InputAttemptDelay = TimeSpan.FromSeconds(seconds: 10);
    private const int InputAttempts = 30;
    private static readonly TimeSpan LockWait = TimeSpan.FromMinutes(minutes: 10);
    private static readonly TimeSpan ReadyPoll = TimeSpan.FromSeconds(seconds: 2);
    // The Azure platform address the load balancer's health probes come from; only it reaches the health port.
    private const string HealthProbeSource = "168.63.129.16/32";
    // The port the TLS front listens on; the load balancer forwards 443 to it.
    private const int TlsFrontPort = 8443;

    /// <summary>Installs and starts the release the bootstrap names.</summary>
    /// <param name="bootstrap">The protected bootstrap.</param>
    /// <param name="cancellationToken">Cancels the install.</param>
    /// <returns>The verb's exit code: 0 when the candidate is ready, 1 when it is refused or does not become ready.</returns>
    public static async Task<int> RunAsync(WorldHostBootstrap bootstrap, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: bootstrap);
        var started = DateTimeOffset.UtcNow;
        var version = Path.TrimEndingDirectorySeparator(path: AppContext.BaseDirectory);

        if (!PuckPaths.IsWithin(path: version, root: Path.Combine(path1: WorldHostLayout.InstallRoot, path2: Puck.Confinement.InstallLayout.VersionsDirectoryName))) {
            Console.Error.WriteLine(value: $"world host install: runs only from a version under {WorldHostLayout.InstallRoot}, not {version}.");
            return 1;
        }
        foreach (var (name, value) in bootstrap.Environment) { Environment.SetEnvironmentVariable(variable: name, value: value); }
        await EnsureQuicAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        using var held = await AcquireLockAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var store = WorldHostStore.Open(owner: bootstrap.Owner, store: bootstrap.Store);
        var inputs = await ReadInputsAsync(content: store.Content, pin: bootstrap.Inputs, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (await WorldReleaseGuestGuard.CheckAsync(cancellationToken: cancellationToken, groups: store.Groups, request: inputs.Guard).ConfigureAwait(continueOnCapturedContext: false) is { } refusal) {
            Console.Error.WriteLine(value: $"world release guest guard refused: {refusal}");
            return 1;
        }
        if (await RefuseRunningAsync(inputs: inputs, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false) is { } running) {
            Console.Error.WriteLine(value: running);
            return 1;
        }
        await EnsureAccountAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        WriteConfiguration(bootstrap: bootstrap, inputs: inputs);
        WriteUnits(environment: bootstrap.Environment, inputs: inputs, keys: [.. bootstrap.Keys.Keys.Order(comparer: StringComparer.Ordinal)], version: version);
        await StartAsync(mcp: (inputs.Mcp is not null), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        return await WaitReadyAsync(inputs: inputs, started: started, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    // .NET's QUIC needs the host's msquic. Azure Linux carries it in its Microsoft open-source repository, whose
    // definition is itself a package of the base repository.
    private static async Task EnsureQuicAsync(CancellationToken cancellationToken) {
        if (System.Net.Quic.QuicListener.IsSupported) { return; }
        var release = (File.Exists(path: "/etc/os-release") ? await File.ReadAllTextAsync(path: "/etc/os-release", cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false) : string.Empty);

        if (!release.Contains(value: "ID=azurelinux", comparisonType: StringComparison.Ordinal)) {
            throw new PlatformNotSupportedException(message: "QUIC needs libmsquic, which this host installs only on Azure Linux; install libmsquic with the host's package manager.");
        }
        await HostAsync(cancellationToken: cancellationToken, arguments: ["install", "-y", "azurelinux-repos-ms-oss"], fileName: "tdnf").ConfigureAwait(continueOnCapturedContext: false);
        await HostAsync(cancellationToken: cancellationToken, arguments: ["install", "-y", "libmsquic"], fileName: "tdnf").ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Takes the machine's release lock (<see cref="WorldHostLayout.ReleaseLock"/>), which every delayed effect
    /// holds across its guard and the effect: a controller's guest command takes it with <c>flock</c>, and .NET's exclusive
    /// open takes the same advisory lock on Linux.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The held lock.</returns>
    /// <exception cref="TimeoutException">The lock stayed held for the whole wait.</exception>
    public static async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken) {
        var deadline = (DateTimeOffset.UtcNow + LockWait);

        while (true) {
            try {
                return new FileStream(access: FileAccess.ReadWrite, mode: FileMode.OpenOrCreate, path: WorldHostLayout.ReleaseLock, share: FileShare.None);
            } catch (IOException) when (DateTimeOffset.UtcNow < deadline) {
                await Task.Delay(cancellationToken: cancellationToken, delay: TimeSpan.FromSeconds(seconds: 1)).ConfigureAwait(continueOnCapturedContext: false);
            } catch (IOException exception) {
                throw new TimeoutException(message: $"{WorldHostLayout.ReleaseLock} stayed held for {LockWait.TotalMinutes} minutes.", innerException: exception);
            }
        }
    }
    // A new machine's identity and network can lag its first boot; the pin check inside the read still fails closed.
    private static Task<WorldHostInputs> ReadInputsAsync(WorldReleaseArchive content, string pin, CancellationToken cancellationToken) => Retry.RetryAsync(
        action: async () => {
            var bytes = (await content.ReadContentAsync(cancellationToken: cancellationToken, pin: pin).ConfigureAwait(continueOnCapturedContext: false)
                ?? throw new InvalidDataException(message: $"the release content store holds no inputs under {pin}"));
            // The pin names the deployment's whole retained input document; the machine's inputs are its host member.
            var inputs = (JsonNode.Parse(utf8Json: bytes.Span)?["host"]?.Deserialize<WorldHostInputs>() ?? throw new InvalidDataException(message: "the retained deployment inputs carry no host inputs"));

            return ((inputs.Schema == WorldHostInputs.CurrentSchema) ? inputs : throw new InvalidDataException(message: $"the retained host inputs name schema {inputs.Schema}"));
        },
        attempts: InputAttempts,
        delay: InputAttemptDelay,
        report: static (exception, attempt) => $"world host install: the retained inputs are not readable yet ({exception.Message}); retrying.",
        // The provider's own failures (an identity endpoint not answering yet) carry its exception types, which this host
        // does not name.
        retryable: exception => !cancellationToken.IsCancellationRequested && (exception is not OperationCanceledException)
    );
    // Only the coordinator drains and stops an authoritative silo. A retried bootstrap may find its own healthy candidate
    // running, but never restarts a running writer of another release.
    private static async Task<string?> RefuseRunningAsync(WorldHostInputs inputs, CancellationToken cancellationToken) {
        if (!await IsActiveAsync(unit: WorldHostLayout.SiloUnit, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false)) { return null; }
        if (await WorldHostGuest.RunningEngineAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false) != inputs.Engine) { return "Another engine is still running; the coordinator must drain it first."; }
        var status = await WorldHostGuest.GetAsync(cancellationToken: cancellationToken, path: "/release/status", port: inputs.HealthPort).ConfigureAwait(continueOnCapturedContext: false);

        if ((status.Code != 200) || ((string?)JsonNode.Parse(json: status.Body)?["release"] != inputs.Release)) { return $"Another release is running: {status.Body}"; }
        var health = await WorldHostGuest.GetAsync(cancellationToken: cancellationToken, path: "/private-healthz", port: inputs.HealthPort).ConfigureAwait(continueOnCapturedContext: false);

        return ((health.Code == 200) ? null : $"The running candidate is not privately healthy: {health.Body}");
    }
    private static async Task EnsureAccountAsync(CancellationToken cancellationToken) {
        if ((await RunAsync(arguments: ["passwd", WorldHostLayout.Account], cancellationToken: cancellationToken, fileName: "getent").ConfigureAwait(continueOnCapturedContext: false)).ExitCode != 0) {
            await HostAsync(cancellationToken: cancellationToken, fileName: "useradd",
                arguments: ["--system", "--user-group", "--no-create-home", "--home-dir", "/nonexistent", "--shell", "/usr/sbin/nologin", WorldHostLayout.Account]).ConfigureAwait(continueOnCapturedContext: false);
        }
        // The account owns the install root: its processes register as holders of the version they run from
        // (InstallRegistration), which takes the root's update lease. The binaries stay readable by nothing else's writes.
        await HostAsync(cancellationToken: cancellationToken, arguments: ["-R", $"{WorldHostLayout.Account}:{WorldHostLayout.Account}", WorldHostLayout.InstallRoot], fileName: "chown").ConfigureAwait(continueOnCapturedContext: false);
    }
    // Every file under the configuration directory is root's alone; a service reads only the ones its unit loads as
    // credentials, which systemd copies into a directory only that unit's processes see.
    private static void WriteConfiguration(WorldHostBootstrap bootstrap, WorldHostInputs inputs) {
        var directory = WorldHostLayout.ConfigurationDirectory;

        _ = Directory.CreateDirectory(path: directory, unixCreateMode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(path: directory, mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var stale in Directory.EnumerateFiles(path: directory)) { File.Delete(path: stale); }

        var credentials = WorldHostLayout.CredentialsDirectory(unit: WorldHostLayout.SiloUnit);
        var silo = inputs.Silo.DeepClone().AsObject();

        foreach (var row in silo["worlds"]!.AsArray()) { row!["federation"]!["keyFile"] = Credential(name: (string)row["federation"]!["keyFile"]!); }
        if (silo["issuer"] is JsonObject issuer) { issuer["keyFile"] = Credential(name: (string)issuer["keyFile"]!); }
        silo["documentDirectory"] = WorldHostLayout.WorldsDirectory;
        silo["stateDir"] = WorldHostLayout.SiloStateDirectory;

        foreach (var (name, encoded) in bootstrap.Keys) {
            if (!Puck.Confinement.InstallLayout.IsPlainName(name: name) || !name.EndsWith(value: ".pk8", comparisonType: StringComparison.Ordinal)) {
                throw new InvalidDataException(message: $"the bootstrap names the key file '{name}', which is not a plain .pk8 name");
            }
            Write(name: name, bytes: Convert.FromBase64String(s: encoded));
        }
        Write(name: "silo.json", bytes: Encoding.UTF8.GetBytes(s: silo.ToJsonString()));
        Write(name: "host.json", bytes: JsonSerializer.SerializeToUtf8Bytes(value: new WorldHostMachine(Environment: bootstrap.Environment, Inputs: inputs, Owner: bootstrap.Owner, Store: bootstrap.Store)));
        if (inputs.Mcp is { } mcp) {
            Write(name: "mcp.json", bytes: Encoding.UTF8.GetBytes(s: mcp.Options.ToJsonString()));
            Write(name: "tls.json", bytes: Encoding.UTF8.GetBytes(s: mcp.Tls.ToJsonString()));
        }

        string Credential(string name) => (Puck.Confinement.InstallLayout.IsPlainName(name: name)
            ? $"{credentials}/{name}"
            : throw new InvalidDataException(message: $"the silo document names the key file '{name}', which is not a credential name"));
        void Write(string name, byte[] bytes) {
            var path = Path.Combine(path1: directory, path2: name);

            File.WriteAllBytes(path: path, bytes: bytes);
            File.SetUnixFileMode(path: path, mode: UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
    private static void WriteUnits(WorldHostInputs inputs, IReadOnlyDictionary<string, string> environment, IReadOnlyList<string> keys, string version) {
        var executable = Path.Combine(path1: version, path2: WorldHostLayout.Executable);
        var configuration = WorldHostLayout.ConfigurationDirectory;
        var mcp = (inputs.Mcp is not null);
        var silo = new StringBuilder();

        silo.Append(value: $"""
            [Unit]
            Description=Puck world host
            Wants=network-online.target
            After=network-online.target
            [Service]
            Type=exec
            WorkingDirectory={version}
            ExecStartPre=+-/usr/sbin/iptables -N PUCK-WORLD
            ExecStartPre=+/usr/sbin/iptables -F PUCK-WORLD
            ExecStartPre=+/usr/sbin/iptables -A PUCK-WORLD -p udp --dport {inputs.QuicPort} -j ACCEPT
            ExecStartPre=+/usr/sbin/iptables -A PUCK-WORLD -p tcp -s {HealthProbeSource} --dport {inputs.HealthPort} -j ACCEPT

            """);
        if (mcp) { silo.Append(value: $"ExecStartPre=+/usr/sbin/iptables -A PUCK-WORLD -p tcp --dport {TlsFrontPort} -j ACCEPT\n"); }
        silo.Append(value: $"""
            ExecStartPre=+-/usr/sbin/iptables -D INPUT -j PUCK-WORLD
            ExecStartPre=+/usr/sbin/iptables -I INPUT 1 -j PUCK-WORLD
            ExecStart={executable} world host serve --silo %d/silo.json{(mcp ? " --mcp %d/mcp.json" : "")}
            LoadCredential=silo.json:{configuration}/silo.json

            """);
        foreach (var key in keys) { silo.Append(value: $"LoadCredential={key}:{configuration}/{key}\n"); }
        if (mcp) { silo.Append(value: $"LoadCredential=mcp.json:{configuration}/mcp.json\n"); }
        silo.Append(value: $"""
            StateDirectory={Path.GetFileName(path: WorldHostLayout.SiloStateDirectory)}
            Environment=HOME={WorldHostLayout.SiloStateDirectory}
            # Each restart takes a fresh authority fence, so a silo that keeps failing backs off from 5 s to 5 min.
            Restart=on-failure
            RestartSec=5
            RestartSteps=6
            RestartMaxDelaySec=300
            TimeoutStopSec={inputs.ShutdownSeconds + 10}
            CPUQuota={inputs.Cpu * 100}%
            MemoryMax={inputs.MemoryInGB}G
            TasksMax=512
            InaccessiblePaths=-{WorldHostLayout.TlsStateDirectory}

            """);
        silo.Append(value: Sandbox(environment: environment)).Append(value: "[Install]\nWantedBy=multi-user.target\n");
        WriteUnit(name: WorldHostLayout.SiloUnit, text: silo.ToString());
        if (mcp) {
            // The keeper's unit is the one door to the TLS front: the front is Requisite on it (its start fails unless the
            // keeper is already active, which a Type=notify unit is only after READY) and is never enabled itself, and
            // the keeper Upholds the front, so systemd starts the front whenever the keeper is ready, a keeper start that
            // failed and was retried included. A Requires= edge would fail the front's start with the keeper's and never
            // queue it again.
            WriteUnit(name: WorldHostLayout.TlsKeeperUnit, text: $"""
                [Unit]
                Description=Puck TLS state keeper
                Wants=network-online.target
                After=network-online.target
                Upholds={WorldHostLayout.TlsUnit}
                [Service]
                Type=notify
                WorkingDirectory={version}
                ExecStart={executable} world host tls --machine %d/host.json
                LoadCredential=host.json:{configuration}/host.json
                StateDirectory={Path.GetFileName(path: WorldHostLayout.TlsStateDirectory)}
                Environment=HOME={WorldHostLayout.TlsStateDirectory}
                Restart=always
                RestartSec=10
                TimeoutStartSec=infinity
                InaccessiblePaths=-{WorldHostLayout.SiloStateDirectory}
                {Sandbox(environment: environment)}[Install]
                WantedBy=multi-user.target

                """);
            WriteUnit(name: WorldHostLayout.TlsUnit, text: $"""
                [Unit]
                Description=Puck automatic HTTPS
                Requisite={WorldHostLayout.TlsKeeperUnit}
                After=network-online.target {WorldHostLayout.SiloUnit} {WorldHostLayout.TlsKeeperUnit}
                [Service]
                Type=exec
                ExecStart={Path.Combine(path1: version, path2: WorldHostLayout.TlsFrontExecutable)} run --config %d/tls.json
                LoadCredential=tls.json:{configuration}/tls.json
                StateDirectory={Path.GetFileName(path: WorldHostLayout.TlsStateDirectory)}
                Environment=XDG_DATA_HOME={WorldHostLayout.TlsStateDirectory}
                Environment=XDG_CONFIG_HOME={WorldHostLayout.TlsStateDirectory}
                Restart=on-failure
                RestartSec=5
                TimeoutStopSec=30
                MemoryMax=128M
                TasksMax=128
                InaccessiblePaths=-{WorldHostLayout.SiloStateDirectory}
                {Sandbox(environment: new Dictionary<string, string>())}
                """);
        }
        // The stable path a controller's guest commands and an operator run the installed CLI by.
        File.Delete(path: WorldHostLayout.HostExecutable);
        File.CreateSymbolicLink(path: WorldHostLayout.HostExecutable, pathToTarget: executable);
    }
    // The sandbox every Puck unit runs in: the install's account, no capabilities, the system read-only except the install
    // root (whose update lease registration takes) and the unit's own state directory, and system calls limited to a
    // service's set. Write-execute memory stays allowed: the runtime's JIT needs it. A filtered call fails with EPERM
    // rather than killing the process.
    private static string Sandbox(IReadOnlyDictionary<string, string> environment) {
        var text = new StringBuilder();

        foreach (var (name, value) in environment.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
            text.Append(value: $"Environment={name}={value}\n");
        }
        return text.Append(value: $"""
            User={WorldHostLayout.Account}
            Group={WorldHostLayout.Account}
            UMask=0077
            ReadWritePaths={WorldHostLayout.InstallRoot}
            NoNewPrivileges=yes
            ProtectSystem=strict
            ProtectHome=yes
            PrivateTmp=yes
            PrivateDevices=yes
            ProtectKernelTunables=yes
            ProtectKernelModules=yes
            ProtectKernelLogs=yes
            ProtectControlGroups=yes
            ProtectClock=yes
            ProtectHostname=yes
            ProtectProc=invisible
            RestrictNamespaces=yes
            RestrictRealtime=yes
            RestrictSUIDSGID=yes
            LockPersonality=yes
            RemoveIPC=yes
            CapabilityBoundingSet=
            AmbientCapabilities=
            RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK
            SystemCallArchitectures=native
            SystemCallFilter=@system-service
            SystemCallErrorNumber=EPERM

            """).ToString();
    }
    private static void WriteUnit(string name, string text) => File.WriteAllText(contents: text, path: $"/etc/systemd/system/{name}");
    private static async Task StartAsync(bool mcp, CancellationToken cancellationToken) {
        await HostAsync(arguments: ["daemon-reload"], cancellationToken: cancellationToken, fileName: "systemctl").ConfigureAwait(continueOnCapturedContext: false);
        await HostAsync(arguments: ["enable", "--now", WorldHostLayout.SiloUnit], cancellationToken: cancellationToken, fileName: "systemctl").ConfigureAwait(continueOnCapturedContext: false);
        if (mcp) {
            await HostAsync(arguments: ["enable", WorldHostLayout.TlsKeeperUnit], cancellationToken: cancellationToken, fileName: "systemctl").ConfigureAwait(continueOnCapturedContext: false);
            // Both stop in one transaction (the keeper would uphold a front stopped alone), then the keeper starts: the
            // front follows with this release's configuration once the keeper is ready.
            await HostAsync(arguments: ["stop", WorldHostLayout.TlsKeeperUnit, WorldHostLayout.TlsUnit], cancellationToken: cancellationToken, fileName: "systemctl").ConfigureAwait(continueOnCapturedContext: false);
            await HostAsync(arguments: ["start", "--no-block", WorldHostLayout.TlsKeeperUnit], cancellationToken: cancellationToken, fileName: "systemctl").ConfigureAwait(continueOnCapturedContext: false);
        } else {
            _ = await RunAsync(arguments: ["disable", "--now", WorldHostLayout.TlsUnit, WorldHostLayout.TlsKeeperUnit], cancellationToken: cancellationToken, fileName: "systemctl").ConfigureAwait(continueOnCapturedContext: false);
        }
    }
    // Waits only for what a candidate reaches before its release commits: private health (the pinned rows activated and
    // checkpointed) and, with MCP, the TLS front. Public /healthz answers 503 until the coordinator commits and opens
    // admission, which happens only after this bootstrap succeeds, so waiting on it would deadlock. A silo that exits
    // during the wait has failed: its start is deterministic over the stored roots and the release, so a restart would
    // only repeat the refusal under a fresh fence; the wait ends at the first exit and stops the unit.
    private static async Task<int> WaitReadyAsync(WorldHostInputs inputs, DateTimeOffset started, CancellationToken cancellationToken) {
        var deadline = (DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds: inputs.ReadySeconds));
        var privateState = "private health has not answered";
        var mcpState = "not reached: private health is not ready";
        var verdict = $"World worker readiness timed out after {inputs.ReadySeconds} s.";

        while (DateTimeOffset.UtcNow < deadline) {
            var health = await WorldHostGuest.GetAsync(cancellationToken: cancellationToken, path: "/private-healthz", port: inputs.HealthPort).ConfigureAwait(continueOnCapturedContext: false);

            privateState = $"{health.Code} {health.Body}";
            if (health.Code == 200) {
                if ((inputs.Mcp is { } mcp) && (await TlsFrontAsync(host: mcp.Host, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false) is { } tls)) {
                    mcpState = tls;
                } else {
                    return 0;
                }
            } else {
                var restarts = await ShowAsync(cancellationToken: cancellationToken, property: "NRestarts").ConfigureAwait(continueOnCapturedContext: false);
                var active = await ShowAsync(cancellationToken: cancellationToken, property: "ActiveState").ConfigureAwait(continueOnCapturedContext: false);
                var sub = await ShowAsync(cancellationToken: cancellationToken, property: "SubState").ConfigureAwait(continueOnCapturedContext: false);

                if ((int.TryParse(s: restarts, provider: CultureInfo.InvariantCulture, result: out var count) && (count > 0)) || (active == "failed") || (sub == "auto-restart")) {
                    verdict = $"World worker silo exited (status {await ShowAsync(cancellationToken: cancellationToken, property: "ExecMainStatus").ConfigureAwait(continueOnCapturedContext: false)}) after {(int)(DateTimeOffset.UtcNow - started).TotalSeconds} s, before private health answered; its restart was stopped.";
                    _ = await RunAsync(arguments: ["stop", WorldHostLayout.SiloUnit], cancellationToken: cancellationToken, fileName: "systemctl").ConfigureAwait(continueOnCapturedContext: false);
                    break;
                }
            }
            await Task.Delay(cancellationToken: cancellationToken, delay: ReadyPoll).ConfigureAwait(continueOnCapturedContext: false);
        }
        // Only the tail of the output survives in the extension's error, so the silo's journal goes to standard output and
        // the verdict of each wait goes last, to standard error.
        var since = $"@{started.ToUnixTimeSeconds()}";
        var journal = await RunAsync(arguments: ["-u", WorldHostLayout.SiloUnit, "--no-pager", "-o", "cat", "--since", since], cancellationToken: cancellationToken, fileName: "journalctl").ConfigureAwait(continueOnCapturedContext: false);
        var lines = journal.Stdout.Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines.TakeLast(count: 40)) { Console.Out.WriteLine(value: line); }
        if (inputs.Mcp is not null) {
            var front = await RunAsync(arguments: ["-u", WorldHostLayout.TlsUnit, "--no-pager", "-o", "cat", "--since", since], cancellationToken: cancellationToken, fileName: "journalctl").ConfigureAwait(continueOnCapturedContext: false);

            foreach (var line in front.Stdout.Split(separator: '\n').Where(predicate: static line => line.Contains(value: "error", comparisonType: StringComparison.Ordinal) || line.Contains(value: "fail", comparisonType: StringComparison.Ordinal)).TakeLast(count: 6)) {
                Console.Out.WriteLine(value: Clip(text: line, length: 200));
            }
        }
        var report = new StringBuilder()
            .AppendLine(value: verdict)
            .AppendLine(handler: $"private health (what this wait needs): {privateState}");

        if (inputs.Mcp is not null) {
            var keeper = await RunAsync(arguments: ["-u", WorldHostLayout.TlsKeeperUnit, "--no-pager", "-o", "cat", "-n", "3", "--since", since], cancellationToken: cancellationToken, fileName: "journalctl").ConfigureAwait(continueOnCapturedContext: false);

            report.AppendLine(handler: $"MCP TLS front (what this wait needs): {mcpState}")
                .AppendLine(handler: $"TLS state keeper (starts the front): {await ShowAsync(cancellationToken: cancellationToken, property: "ActiveState", unit: WorldHostLayout.TlsKeeperUnit).ConfigureAwait(continueOnCapturedContext: false)}; last lines: {string.Join(separator: '|', values: keeper.Stdout.Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries))}");
        }
        var open = await WorldHostGuest.GetAsync(cancellationToken: cancellationToken, path: "/healthz", port: inputs.HealthPort).ConfigureAwait(continueOnCapturedContext: false);

        report.AppendLine(handler: $"public health (opens only after the release commits): {open.Body} (HTTP {open.Code})")
            .AppendLine(handler: $"service: {await ShowAsync(cancellationToken: cancellationToken, property: "ActiveState").ConfigureAwait(continueOnCapturedContext: false)} restarts={await ShowAsync(cancellationToken: cancellationToken, property: "NRestarts").ConfigureAwait(continueOnCapturedContext: false)}")
            // The silo's tagged narration ([silo.activate: ... refused], [silo.host: stopped by ...]) first, since a stack
            // trace can be longer than the tail.
            .AppendLine(value: "silo (its tagged lines):");
        foreach (var line in lines.Where(predicate: static line => line.Contains(value: "[silo", comparisonType: StringComparison.Ordinal) || line.Contains(value: "[world", comparisonType: StringComparison.Ordinal)).TakeLast(count: 15)) {
            report.AppendLine(value: Clip(text: line, length: 240));
        }
        report.AppendLine(value: "silo (last lines):");
        foreach (var line in lines.TakeLast(count: 12)) { report.AppendLine(value: Clip(text: line, length: 240)); }
        Console.Error.Write(value: report.ToString());
        return 1;
    }
    // Asks the TLS front for the protected-resource document, connecting to it on loopback under the public name, so the
    // certificate is checked as a client checks it. Returns null when it answers, otherwise why not.
    private static async Task<string?> TlsFrontAsync(string host, CancellationToken cancellationToken) {
        using var handler = new SocketsHttpHandler {
            ConnectCallback = static async (context, token) => {
                var socket = new Socket(socketType: SocketType.Stream, protocolType: ProtocolType.Tcp);

                try {
                    await socket.ConnectAsync(host: "127.0.0.1", port: TlsFrontPort, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
                    return new NetworkStream(ownsSocket: true, socket: socket);
                } catch {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler: handler) { Timeout = TimeSpan.FromSeconds(seconds: 10) };

        try {
            using var response = await client.GetAsync(cancellationToken: cancellationToken, requestUri: $"https://{host}/.well-known/oauth-protected-resource/mcp").ConfigureAwait(continueOnCapturedContext: false);

            return (response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
        } catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException) {
            return exception.Message;
        }
    }
    private static async Task<bool> IsActiveAsync(string unit, CancellationToken cancellationToken) =>
        ((await RunAsync(arguments: ["is-active", "--quiet", unit], cancellationToken: cancellationToken, fileName: "systemctl").ConfigureAwait(continueOnCapturedContext: false)).ExitCode == 0);
    // One property per call: systemctl show prints several in its own order, not the order asked.
    private static async Task<string> ShowAsync(string property, CancellationToken cancellationToken, string unit = WorldHostLayout.SiloUnit) =>
        (await RunAsync(arguments: ["show", "-p", property, "--value", unit], cancellationToken: cancellationToken, fileName: "systemctl").ConfigureAwait(continueOnCapturedContext: false)).Stdout.Trim();
    private static string Clip(string text, int length) => ((text.Length > length) ? text[..length] : text);
    private static Task<ChildProcessResult> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken) =>
        CliProcess.RunAsync(arguments: arguments, cancellationToken: cancellationToken, fileName: fileName);
    private static Task<string> HostAsync(string fileName, string[] arguments, CancellationToken cancellationToken) =>
        CliProcess.RunCheckedAsync(arguments: arguments, cancellationToken: cancellationToken, fileName: fileName);
}
