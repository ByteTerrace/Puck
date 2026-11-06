using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Puck.World;

namespace Puck.Cli;

/// <summary>How a World a verb starts presents, which decides whether it opens a GPU device.</summary>
public enum WorldShape {
    /// <summary>Boots with <c>--headless true</c>: full authority, no window and no device.</summary>
    Headless,
    /// <summary>Boots with <c>--headless false</c>: a window and a device.</summary>
    Windowed,
    /// <summary>Passes no <c>--headless</c>, so the world document's <c>host.presentation</c> decides; an offscreen
    /// document opens a device, and the launch treats every such World as one that does.</summary>
    Document,
    /// <summary>Runs a test batch (<c>--test-batch</c>), which boots every scenario headless.</summary>
    TestBatch,
}
/// <summary>One World a verb starts: what it boots, how it presents and the verb's own further arguments. The launch
/// (<see cref="WorldLaunch"/>) builds the command line from it, so the arguments every World shares are spelled in one
/// place.</summary>
/// <param name="Step">The verb or step starting the World, which names its admission wait and, unless
/// <see cref="Holder"/> names another, its GPU lease holder.</param>
/// <param name="Artifact">The World build's entry assembly (<c>Puck.World.dll</c>).</param>
public sealed record WorldLaunchRequest(string Step, string Artifact) {
    /// <summary>Gets the executable that starts the World in place of <c>dotnet</c> running <see cref="Artifact"/> (the
    /// update stub), or <see langword="null"/>.</summary>
    public string? Executable { get; init; }
    /// <summary>Gets the world the World boots (<c>--world</c>), or <see langword="null"/> when the verb's own arguments
    /// name what it runs.</summary>
    public string? World { get; init; }
    /// <summary>Gets how the World presents.</summary>
    public WorldShape Shape { get; init; } = WorldShape.Headless;
    /// <summary>Gets the backend the World boots on (<c>--backend</c>), or <see langword="null"/> to let the document
    /// decide.</summary>
    public WorldBackendPreference? Backend { get; init; }
    /// <summary>Gets the World's state directory (<c>--state-dir</c>), or <see langword="null"/>.</summary>
    public string? StateDirectory { get; init; }
    /// <summary>Gets the World's own exit backstop in seconds (<c>--exit-after-seconds</c>; zero disables it), or
    /// <see langword="null"/> to let the document decide. <see cref="WorldLaunch.Backstop"/> derives it from a run's
    /// deadline.</summary>
    public int? ExitAfterSeconds { get; init; }
    /// <summary>Gets whether the World opens the machine's physical input devices (<c>--devices all</c>), which only a
    /// World the owner asked to play in does; every other World passes <c>--devices none</c>.</summary>
    public bool OwnerDevices { get; init; }
    /// <summary>Gets the verb's own further arguments, after the shared ones.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
    /// <summary>Gets the environment entries the World gets over this process's: a value sets a variable,
    /// <see langword="null"/> removes it.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    /// <summary>Gets the directory the World runs in, or <see langword="null"/> for this process's own.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>Gets whether the start is part of a run that holds the heavy lease, so its admission also waits while
    /// another process runs a heavy test suite.</summary>
    public bool HeavySuite { get; init; }
    /// <summary>Gets the GPU lease holder's name, or <see langword="null"/> for <see cref="Step"/>.</summary>
    public string? Holder { get; init; }
    /// <summary>Gets the lane the GPU lease is held for, or <see langword="null"/> for the checkout's lane
    /// (<see cref="CliLease.LaneOf"/>).</summary>
    public string? Lane { get; init; }
    /// <summary>Gets the checkout whose shared lease directory holds the GPU lease, or <see langword="null"/> for the
    /// repository this process runs in.</summary>
    public string? Checkout { get; init; }

    /// <summary>Gets whether the World opens a GPU device, and so starts only under the GPU lease.</summary>
    public bool OpensDevice => (Shape is WorldShape.Windowed or WorldShape.Document);
    /// <summary>Gets what the World is to the host admission, which sets the commit it is expected to peak at.</summary>
    public HostStartKind Kind => Shape switch {
        WorldShape.TestBatch => HostStartKind.ReproduceReplica,
        WorldShape.Windowed or WorldShape.Document => HostStartKind.WindowedWorld,
        _ => HostStartKind.HeadlessWorld,
    };
    /// <summary>Gets the program the launch starts: <see cref="Executable"/>, or <c>dotnet</c>.</summary>
    public string FileName => (Executable ?? "dotnet");

    /// <summary>Builds the World's command line after <see cref="FileName"/>: the entry assembly (unless an executable
    /// starts it), the shared arguments every World a verb starts carries, then <see cref="Arguments"/>.</summary>
    /// <returns>The arguments.</returns>
    public IReadOnlyList<string> CommandLine() {
        var arguments = new List<string>();

        if (Executable is null) { arguments.Add(item: Artifact); }
        if (World is { } world) { arguments.AddRange(collection: ["--world", world]); }
        arguments.AddRange(collection: ["--devices", (OwnerDevices ? "all" : "none")]);
        switch (Shape) {
            case WorldShape.Headless:
                arguments.AddRange(collection: ["--headless", "true"]);
                break;
            case WorldShape.Windowed:
                arguments.AddRange(collection: ["--headless", "false"]);
                break;
        }
        if (Backend is { } backend) { arguments.AddRange(collection: ["--backend", WorldBackendTokens.Token(backend: backend)]); }
        if (StateDirectory is { } state) { arguments.AddRange(collection: ["--state-dir", state]); }
        if (ExitAfterSeconds is { } seconds) { arguments.AddRange(collection: ["--exit-after-seconds", seconds.ToString(provider: CultureInfo.InvariantCulture)]); }
        arguments.AddRange(collection: Arguments);

        return arguments;
    }
}
/// <summary>
/// The one door every <c>Puck.World</c> process this CLI starts passes through. <see cref="Admit"/> takes the machine's
/// <c>gpu</c> lease for a World that opens a device (shared by this process's other device Worlds of the same holder and
/// lane, and nested under an ancestor that already holds it, so a verb under <c>puck lease gpu</c> or a sweep never
/// waits on itself), then the host admission; the admitted launch then starts the World once, as a captured run
/// (<see cref="Run"/>), a driven process (<see cref="Start"/>) or a raw redirected process
/// (<see cref="StartRedirected"/>). Every process it starts joins a job that the operating system ends with this
/// process, so a killed CLI leaves no World running. A headless World takes no GPU lease.
/// </summary>
public sealed class WorldLaunch : IDisposable {
    /// <summary>The seconds a World's own exit backstop outlasts the deadline of the run that started it, so the run's
    /// own watchdog always acts first.</summary>
    public const int BackstopGraceSeconds = 120;

    private readonly WorldLaunchRequest m_request;

    private IDisposable? m_gpu;
    private bool m_used;

    private WorldLaunch(WorldLaunchRequest request, IDisposable? gpu) {
        m_request = request;
        m_gpu = gpu;
    }

    /// <summary>Gets the request this launch starts.</summary>
    public WorldLaunchRequest Request => m_request;

    /// <summary>Returns a World's own exit backstop for a run bounded by a deadline: the deadline, rounded up to whole
    /// seconds, plus <see cref="BackstopGraceSeconds"/>.</summary>
    /// <param name="deadline">The longest the run lets the World live.</param>
    /// <returns>The backstop in seconds.</returns>
    public static int Backstop(TimeSpan deadline) => (((int)Math.Ceiling(a: deadline.TotalSeconds)) + BackstopGraceSeconds);
    /// <summary>Returns whether a World of this shape on this backend needs a build carrying Vulkan SPIR-V: on Windows a
    /// one-project build ships Direct3D 12 bytecode alone, so any World that opens a device on a backend other than
    /// Direct3D 12 (Vulkan, or one the document chooses) needs it. Elsewhere every build carries it.</summary>
    /// <param name="shape">How the World presents.</param>
    /// <param name="backend">The backend it boots on, or <see langword="null"/> when the document decides.</param>
    /// <returns><see langword="true"/> when the build must carry SPIR-V.</returns>
    public static bool NeedsSpirv(WorldShape shape, WorldBackendPreference? backend) =>
        (OperatingSystem.IsWindows() && (shape is WorldShape.Windowed or WorldShape.Document) && (backend != WorldBackendPreference.DirectX));
    /// <summary>Resolves the World a verb boots: the build a caller names with <c>--world-artifact</c>, used as given, or
    /// the build of the checkout's current sources, built once when no run has built that source state, with SPIR-V
    /// when any World the verb starts needs it (<see cref="NeedsSpirv"/>).</summary>
    /// <param name="verb">The calling verb, which prefixes progress and refusal lines.</param>
    /// <param name="repositoryRoot">The checkout whose sources are built.</param>
    /// <param name="logDirectory">The run's directory, which keeps the build's log when this run builds.</param>
    /// <param name="named">The entry assembly the caller named, or <see langword="null"/> for the checkout's build.</param>
    /// <param name="spirv">Whether the checkout's build must carry Vulkan SPIR-V.</param>
    /// <param name="build">The resolved build, which the caller disposes after its last World has exited.</param>
    /// <param name="error">Why no build is available, or empty.</param>
    /// <returns><see langword="true"/> when a build is available.</returns>
    public static bool TryResolve(string verb, string repositoryRoot, string logDirectory, string? named, bool spirv, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldBuild? build, out string error) {
        if (!WorldArtifactBuild.TryResolveNamed(error: out error, lease: out var lease, logDirectory: logDirectory, named: named, path: out var path, repositoryRoot: repositoryRoot, spirv: spirv, timeout: WorldArtifactBuild.BuildTimeout, verb: verb)) {
            build = null;

            return false;
        }

        build = new WorldBuild(lease: lease, path: path);

        return true;
    }
    /// <summary>Parses a backend token through the World's own spelling (<see cref="WorldBackendTokens"/>,
    /// case-insensitive).</summary>
    /// <param name="token">The token, or <see langword="null"/>.</param>
    /// <returns>The backend, or <see langword="null"/> for a <see langword="null"/> token.</returns>
    /// <exception cref="ArgumentException"><paramref name="token"/> names no backend.</exception>
    public static WorldBackendPreference? ParseBackend(string? token) => ((token is null)
        ? null
        : (WorldBackendTokens.Parse(token: token) ?? throw new ArgumentException(message: WorldBackendTokens.Refusal(token: token), paramName: nameof(token))));
    /// <summary>Creates a verb's <c>--backend</c> option, parsed at parse time through the World's own spelling and
    /// refused the way the World refuses it.</summary>
    /// <param name="description">The verb's statement of what the backend selects.</param>
    /// <param name="accepted">The backends the verb runs on, or <see langword="null"/> for every one.</param>
    /// <returns>The option; its value is <see langword="null"/> when omitted.</returns>
    public static Option<WorldBackendPreference?> BackendOption(string description, IReadOnlyList<WorldBackendPreference>? accepted = null) => new(name: "--backend") {
        CustomParser = result => {
            var token = result.Tokens[0].Value;

            if ((WorldBackendTokens.Parse(token: token) is { } backend) && ((accepted is null) || accepted.Contains(value: backend))) {
                return backend;
            }

            result.AddError(errorMessage: $"--backend {((accepted is null) ? WorldBackendTokens.Refusal(token: token) : $"'{token}' is not a backend this verb runs on; expected {string.Join(separator: ", ", values: accepted.Select(selector: WorldBackendTokens.Token))}")}.");

            return null;
        },
        Description = description,
    };
    /// <summary>Admits one World start: takes the GPU lease when the World opens a device, then waits for the host
    /// admission. The lease and admission waits are other runs' time; <paramref name="queue"/> lets a caller stop its own
    /// budget's clock for them.</summary>
    /// <param name="request">The World to start.</param>
    /// <param name="cancellationToken">Stops the waits.</param>
    /// <param name="queue">Opens a scope around the lease and admission waits, or <see langword="null"/>.</param>
    /// <param name="stage">Receives what the start is waiting on (<c>gpu-lease</c>, <c>host-admission</c>, then the
    /// admission's own wait text), or <see langword="null"/>.</param>
    /// <returns>The admitted launch, which holds the GPU lease until it is disposed or the World it starts has exited.</returns>
    /// <exception cref="TimeoutException">The GPU lease stayed held past the lease wait.</exception>
    /// <exception cref="HostAdmissionRefusedException">The host had no headroom within the admission's bound.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">The checkout names no shared lease directory.</exception>
    public static WorldLaunch Admit(WorldLaunchRequest request, CancellationToken cancellationToken = default, Func<IDisposable>? queue = null, Action<string>? stage = null) =>
        AdmitAsync(cancellationToken: cancellationToken, queue: queue, request: request, stage: stage).GetAwaiter().GetResult();
    /// <summary>Admits one World start as <see cref="Admit"/> does, without blocking a thread on the waits.</summary>
    /// <param name="request">The World to start.</param>
    /// <param name="cancellationToken">Stops the waits.</param>
    /// <param name="queue">Opens a scope around the lease and admission waits, or <see langword="null"/>.</param>
    /// <param name="stage">Receives what the start is waiting on, or <see langword="null"/>.</param>
    /// <returns>The admitted launch.</returns>
    /// <exception cref="TimeoutException">The GPU lease stayed held past the lease wait.</exception>
    /// <exception cref="HostAdmissionRefusedException">The host had no headroom within the admission's bound.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">The checkout names no shared lease directory.</exception>
    public static async Task<WorldLaunch> AdmitAsync(WorldLaunchRequest request, CancellationToken cancellationToken = default, Func<IDisposable>? queue = null, Action<string>? stage = null) {
        using var waiting = queue?.Invoke();

        if (!request.OpensDevice) {
            stage?.Invoke(obj: "host-admission");
            await AdmitHostAsync(bound: null, cancellationToken: cancellationToken, request: request, reserve: true, stage: stage, refuse: true).ConfigureAwait(continueOnCapturedContext: false);

            return new WorldLaunch(gpu: null, request: request);
        }

        var checkout = (request.Checkout ?? (CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root) ? root : Environment.CurrentDirectory));

        // A device World waits for headroom before it asks for the GPU lease, reserving nothing, so the GPU never idles
        // under a lease while memory is short. Headroom is then judged again, and reserved, with the lease held: the lease
        // is what serializes device starts across processes (a reservation lives only in the process that made it), so of
        // two device starts only the holder reads the headroom it is about to take. A leased judgement that does not pass
        // within LeasedAdmissionBound gives the lease back and waits for headroom again rather than idling the GPU.
        for (var attempt = 1; ; attempt++) {
            stage?.Invoke(obj: "host-admission");
            await AdmitHostAsync(bound: null, cancellationToken: cancellationToken, refuse: true, request: request, reserve: false, stage: stage).ConfigureAwait(continueOnCapturedContext: false);
            stage?.Invoke(obj: "gpu-lease");

            var gpu = await WorldGpuHold.AcquireAsync(cancellationToken: cancellationToken, checkout: checkout, holder: (request.Holder ?? request.Step), lane: (request.Lane ?? CliLease.LaneOf(checkout: checkout))).ConfigureAwait(continueOnCapturedContext: false);

            try {
                stage?.Invoke(obj: "host-admission");

                if (await AdmitHostAsync(bound: LeasedAdmissionBound, cancellationToken: cancellationToken, refuse: false, request: request, reserve: true, stage: stage).ConfigureAwait(continueOnCapturedContext: false)) {
                    return new WorldLaunch(gpu: gpu, request: request);
                }
            } catch {
                gpu.Dispose();

                throw;
            }

            gpu.Dispose();

            if (attempt == MaximumRequeues) { throw new HostAdmissionRefusedException(step: request.Step); }
            Console.Error.WriteLine(value: $"{request.Step}: the host's headroom went to another start while the GPU lease was held; released it and waiting for headroom again.");
        }
    }

    /// <summary>The longest a device start judges headroom while it holds the GPU lease before it gives the lease back:
    /// three readings at the host admission's ten-second interval, so the GPU idles under a lease for no longer.</summary>
    public static readonly TimeSpan LeasedAdmissionBound = TimeSpan.FromSeconds(seconds: 30);
    /// <summary>How many times a device start gives the GPU lease back for headroom before it is refused.</summary>
    public const int MaximumRequeues = 8;

    private static async Task<bool> AdmitHostAsync(WorldLaunchRequest request, TimeSpan? bound, bool reserve, bool refuse, Action<string>? stage, CancellationToken cancellationToken) {
        var admitted = await WorldAdmission.TryAdmitAsync(bound: bound, cancellationToken: cancellationToken, device: request.OpensDevice, heavySuite: request.HeavySuite, kind: request.Kind, reserve: reserve, stage: stage, step: request.Step).ConfigureAwait(continueOnCapturedContext: false);

        if (!admitted && refuse) { throw new HostAdmissionRefusedException(step: request.Step); }

        return admitted;
    }
    // The readiness of a World this launch started, which marks it ready on its own report of steady state.
    private WorldReadiness Readiness(Process process) => WorldReadiness.For(
        checkout: (m_request.Checkout ?? (CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root) ? root : Environment.CurrentDirectory)),
        opensDevice: m_request.OpensDevice,
        process: process
    );

    /// <summary>Runs the admitted World to exit with its streams captured, as <see cref="CliProcess.RunCaptured"/> does,
    /// and releases the GPU lease once it has exited.</summary>
    /// <param name="input">The World's standard input.</param>
    /// <param name="timeout">The run's own kill ceiling; <see cref="Timeout.InfiniteTimeSpan"/> sets none.</param>
    /// <param name="cancellationToken">Kills the World's tree and throws <see cref="OperationCanceledException"/>.</param>
    /// <param name="continueWhen">Releases <paramref name="continuationInput"/> once it accepts an output line, or
    /// <see langword="null"/>.</param>
    /// <param name="continuationInput">The input held back until <paramref name="continueWhen"/> accepts a line.</param>
    /// <param name="continuationGate">Also holds <paramref name="continuationInput"/> until it completes, or
    /// <see langword="null"/>.</param>
    /// <param name="onOutput">Receives each output line as it arrives, or <see langword="null"/>.</param>
    /// <param name="retainOutput">Whether the result keeps the transcript.</param>
    /// <returns>The finished World.</returns>
    /// <exception cref="InvalidOperationException">This launch already started its World, or the World could not start
    /// or join the job that ends it with this process.</exception>
    public CliProcessResult Run(string input, TimeSpan timeout, CancellationToken cancellationToken = default, Func<CliProcessOutputLine, bool>? continueWhen = null, string continuationInput = "", Task? continuationGate = null, Action<CliProcessOutputLine>? onOutput = null, bool retainOutput = true) {
        Use();

        WorldReadiness? readiness = null;

        try {
            return CliProcess.RunCaptured(
                arguments: m_request.CommandLine(),
                cancellationToken: cancellationToken,
                continuationGate: continuationGate,
                continuationInput: continuationInput,
                continueWhen: continueWhen,
                environment: m_request.Environment,
                fileName: m_request.FileName,
                input: input,
                onOutput: line => {
                    readiness?.Observe(line: line.Line, stdout: (line.Stream == CliProcessOutputStream.Stdout));
                    onOutput?.Invoke(obj: line);
                },
                retainOutput: retainOutput,
                started: process => {
                    ChildProcessJob.Assign(process: process);
                    readiness = Readiness(process: process);
                },
                timeout: timeout,
                workingDirectory: m_request.WorkingDirectory
            );
        } finally {
            Dispose();
        }
    }
    /// <summary>Starts the admitted World as a process a verb drives line by line, with every output line recorded and
    /// both streams written to <c>&lt;logPrefix&gt;stdout.log</c> and <c>&lt;logPrefix&gt;stderr.log</c>. The returned
    /// process owns the GPU lease from here and releases it once the World has exited and it is disposed.</summary>
    /// <param name="logDirectory">The directory that receives the World's logs.</param>
    /// <param name="logPrefix">The prefix of the log file names.</param>
    /// <returns>The running World.</returns>
    /// <exception cref="InvalidOperationException">This launch already started its World, or the World could not start
    /// or join the job that ends it with this process.</exception>
    public WorldProcess Start(string logDirectory, string logPrefix = "") {
        Use();

        try {
            var process = WorldProcess.Start(fileName: m_request.FileName, arguments: m_request.CommandLine(), environment: m_request.Environment, gpu: m_gpu, logDirectory: logDirectory, logPrefix: logPrefix, readiness: Readiness, workingDirectory: (m_request.WorkingDirectory ?? Environment.CurrentDirectory));

            m_gpu = null;

            return process;
        } finally {
            Dispose();
        }
    }
    /// <summary>Starts the admitted World with its standard streams redirected and unread, for an owner that reads them
    /// itself. The caller owns the returned GPU lease and disposes it once the World has exited, and passes every line it
    /// reads to the returned readiness, so the host admission stops reserving the World's growth once it reports ready.</summary>
    /// <param name="gpu">The GPU lease the World runs under, or <see langword="null"/> for a World that opens no
    /// device.</param>
    /// <param name="readiness">The World's readiness, which the caller feeds the lines it reads.</param>
    /// <returns>The started process.</returns>
    /// <exception cref="InvalidOperationException">This launch already started its World, or the World could not start
    /// or join the job that ends it with this process.</exception>
    public Process StartRedirected(out IDisposable? gpu, out WorldReadiness readiness) {
        Use();

        try {
            var process = Contain(process: Puck.Abstractions.ChildProcess.StartRedirected(arguments: m_request.CommandLine(), encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), environment: m_request.Environment, fileName: m_request.FileName, workingDirectory: m_request.WorkingDirectory));

            readiness = Readiness(process: process);
            gpu = m_gpu;
            m_gpu = null;

            return process;
        } finally {
            Dispose();
        }
    }
    /// <summary>Adds a just-started World to the job that ends it with this process; a World that cannot join is killed,
    /// so no World runs outside the job.</summary>
    /// <param name="process">The started World.</param>
    /// <returns>The same process.</returns>
    /// <exception cref="InvalidOperationException">The World could not join the job.</exception>
    internal static Process Contain(Process process) {
        try {
            ChildProcessJob.Assign(process: process);

            return process;
        } catch {
            WorldProcess.KillTree(process: process);
            process.Dispose();

            throw;
        }
    }
    /// <summary>Releases the GPU lease of a launch whose World never started, or that has finished its captured run.</summary>
    public void Dispose() => Interlocked.Exchange(location1: ref m_gpu, value: null)?.Dispose();

    private void Use() {
        if (m_used) { throw new InvalidOperationException(message: "An admitted World launch starts one World."); }
        m_used = true;
    }
}
/// <summary>The World build a verb boots: its entry assembly and, for a build of the checkout's sources, the store lease
/// that keeps it from being pruned while a World runs from it.</summary>
public sealed class WorldBuild : IDisposable {
    private readonly WorldArtifact? m_lease;

    internal WorldBuild(string path, WorldArtifact? lease) {
        Path = path;
        m_lease = lease;
    }

    /// <summary>Gets the full path of the entry assembly.</summary>
    public string Path { get; }
    /// <summary>Gets the source-state key of a build of the checkout's sources, or <see langword="null"/> for an external
    /// build.</summary>
    public string? Key => m_lease?.Key;
    /// <summary>Gets whether this run reused a build an earlier run made, rather than building it; an external build was
    /// never built here.</summary>
    public bool Reused => (m_lease?.Reused ?? true);

    /// <inheritdoc/>
    public override string ToString() => Path;
    /// <summary>Releases the store lease, after the last World running from the build has exited.</summary>
    public void Dispose() => m_lease?.Dispose();
}
