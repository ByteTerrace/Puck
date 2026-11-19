using System.CommandLine;

namespace Puck.Cli.Host;

/// <summary><c>puck host</c> — the verbs about the machine an agent runs on: <c>load</c> reports CPU, memory, disk and
/// GPU busyness as admission lines (see <see cref="HostLoadMonitor"/>).</summary>
public static class HostCommand {
    private const string HeapVerb = "host heap";
    private const string LoadVerb = "host load";

    private static int Load(bool watch, int interval, int window, HostLoadThresholds thresholds, bool json, CancellationToken cancellationToken) {
        using var records = CliJsonLines.Open(json: json);
        var probe = new HostProbe(checkoutRoot: Environment.CurrentDirectory);
        var spacing = TimeSpan.FromSeconds(seconds: interval);

        // One reading judges its own CPU figure; a watch judges the mean over the last --window readings.
        var monitor = new HostLoadMonitor(cpuSamples: (watch ? window : 1), thresholds: thresholds);

        do {
            var sample = probe.Sample(firstInterval: TimeSpan.FromSeconds(seconds: 1));
            var lines = monitor.Observe(sample: sample);
            var demands = Enum.GetValues<HostStartKind>().Select(kind => HostStartTracker.Session.Judge(
                sample, kind, thresholds, TimeProvider.System, reserve: false)).ToArray();

            if (records is null) {
                foreach (var line in lines) {
                    Console.Out.WriteLine(value: line);
                }
                if (lines.Count > 0) {
                    foreach (var demand in demands) {
                        Console.Out.WriteLine(FormattableString.Invariant($"admission {demand.Kind}: headroom={demand.HeadroomGb:0.0}GB peak={demand.OwnGb:0.0}GB growth={demand.GrowthGb:0.0}GB need={demand.NeedGb:0.0}GB commit-covered={demand.CommitCovered} freeRAM={demand.FreeRamGb:0.0}GB privateResident={demand.ResidentOwnGb:0.0}GB privateResidentGrowth={demand.ResidentGrowthGb:0.0}GB privateResidentNeed={demand.ResidentNeedGb:0.0}GB workingSet={Measured(demand.WorkingSetOwnGb)} ram-covered={demand.PhysicalCovered} admitted={demand.Covered}"));
                    }
                    foreach (var start in sample.Starts ?? []) {
                        Console.Out.WriteLine(FormattableString.Invariant($"start {start.Kind} {start.Id}: private={start.PrivateGb:0.0}GB privateResident={Measured(start.PrivateResidentGb)} workingSet={Measured(start.WorkingSetGb)} judged={start.ResidentGb:0.0}GB ready={start.Ready}"));
                    }
                }
            } else if (lines.Count > 0) {
                records.Write(record: new System.Text.Json.Nodes.JsonObject {
                    ["at"] = sample.At,
                    ["cpuPercent"] = Finite(value: monitor.CpuMean),
                    ["freeRamGb"] = Finite(value: sample.FreeRamGb),
                    ["freeDiskGb"] = Finite(value: sample.FreeDiskGb),
                    ["commitPercent"] = Finite(value: sample.CommitPercent),
                    ["commitChargeGb"] = Finite(sample.CommitChargeGb),
                    ["commitLimitGb"] = Finite(sample.CommitLimitGb),
                    ["starts"] = new System.Text.Json.Nodes.JsonArray([.. (sample.Starts ?? []).Select(static start => new System.Text.Json.Nodes.JsonObject {
                        ["id"] = start.Id,
                        ["kind"] = start.Kind.ToString(),
                        ["privateGb"] = Finite(start.PrivateGb),
                        ["privateResidentGb"] = Finite(start.PrivateResidentGb),
                        ["workingSetGb"] = Finite(start.WorkingSetGb),
                        ["judgedResidentGb"] = Finite(start.ResidentGb),
                        ["ready"] = start.Ready,
                    })]),
                    ["demands"] = new System.Text.Json.Nodes.JsonArray([.. demands.Select(static demand => new System.Text.Json.Nodes.JsonObject {
                        ["kind"] = demand.Kind.ToString(),
                        ["headroomGb"] = Finite(demand.HeadroomGb),
                        ["peakGb"] = Finite(demand.OwnGb),
                        ["growthGb"] = Finite(demand.GrowthGb),
                        ["needGb"] = Finite(demand.NeedGb),
                        ["commitCovered"] = demand.CommitCovered,
                        ["freeRamGb"] = Finite(demand.FreeRamGb),
                        ["privateResidentGb"] = Finite(demand.ResidentOwnGb),
                        ["privateResidentGrowthGb"] = Finite(demand.ResidentGrowthGb),
                        ["privateResidentNeedGb"] = Finite(demand.ResidentNeedGb),
                        ["workingSetGb"] = Finite(demand.WorkingSetOwnGb),
                        ["ramCovered"] = demand.PhysicalCovered,
                        ["admitted"] = demand.Covered,
                    })]),
                    ["reuseNodes"] = sample.ReuseNodes,
                    ["gpuHolder"] = sample.GpuHolder,
                    ["heavyTestHolder"] = sample.HeavyTestHolder,
                    ["admission"] = monitor.Admission,
                });
            }

            Console.Out.Flush();

            if (watch && !cancellationToken.WaitHandle.WaitOne(timeout: spacing)) {
                continue;
            }

            break;
        } while (true);

        return (cancellationToken.IsCancellationRequested
            ? CliExit.Cancelled
            : CliExit.Success);
    }
    private static double? Finite(double value) => (double.IsFinite(d: value) ? Math.Round(value: value, digits: 1) : null);
    private static string Measured(double gb) => (double.IsFinite(d: gb) ? FormattableString.Invariant($"{gb:0.0}GB") : "unmeasured");
    private static Command CreateLoad() {
        var jsonOption = CliOptions.Json(description: "Write one JSON object per transition reading: at, cpuPercent (window mean), freeRamGb, freeDiskGb, commitPercent, commitChargeGb, commitLimitGb, starts (with private, private working-set and working-set GB), per-kind demands, reuseNodes, gpuHolder, heavyTestHolder and admission; an unreported figure is null.");
        var watchOption = new Option<bool>(name: "--watch") { Description = "Keep reading every --interval seconds and print a line on each transition, until cancelled." };
        var intervalOption = CliOptions.Count(defaultValue: 10, description: "Seconds between readings with --watch.", minimum: 1, name: "--interval");
        var windowOption = CliOptions.Count(defaultValue: 6, description: "Readings the CPU mean covers with --watch; CAPACITY waits until that many exist.", minimum: 1, name: "--window");
        var capacityCpuOption = CliOptions.Amount(defaultValue: HostLoadThresholds.ThisMachine.CapacityCpuPercent, description: "CAPACITY needs the CPU mean below this percentage (with --commit-reserve).", maximum: 100, minimum: 0, name: "--capacity-cpu");
        var commitReserveOption = CliOptions.Amount(defaultValue: HostLoadThresholds.ThisMachine.CommitReservePercent, description: "The percentage of the commit limit that must stay unclaimed after a start's commit need, when the platform reports a limit; CAPACITY needs the headroom to hold it.", maximum: 100, minimum: 0, name: "--commit-reserve");
        var pressureRamOption = CliOptions.Amount(defaultValue: HostLoadThresholds.ThisMachine.PressureRamGb, description: "PRESSURE when free memory is below this many gigabytes; a start must also leave this much free once its expected working set is taken.", minimum: 0, name: "--pressure-ram");
        var pressureDiskOption = CliOptions.Amount(defaultValue: HostLoadThresholds.ThisMachine.PressureDiskGb, description: "PRESSURE when free disk on the working directory's drive is below this many gigabytes.", minimum: 0, name: "--pressure-disk");


        var command = new Command(
            description: "Report CPU, memory, disk and GPU busyness as admission lines: GPU busy|idle, PRESSURE, CAPACITY, LOADED.",
            name: "load"
        ) { watchOption, intervalOption, windowOption, capacityCpuOption, commitReserveOption, pressureRamOption, pressureDiskOption, jsonOption };

        command.Detail(detail: $"""
              Lines (each carries cpu=, freeRAM=, freeDisk=, commit= where the platform reports a commit limit, and reuseNodes=):
                GPU busy (<process> <id>) | GPU idle   at the first reading and on every change after
                PRESSURE <why>                         free memory or disk falls under its pressure
                                                       threshold, and again when the reasons change
                CAPACITY                               otherwise, the CPU mean under --capacity-cpu and the
                                                       commit headroom at or above --commit-reserve of the
                                                       commit limit, once --window readings exist
                LOADED                                 otherwise, once --window readings exist: capacity ended
              One line per transition: a state that holds prints nothing more.
              Each transition also reports, per start kind, what a start of that kind needs in two quantities: its
              private bytes (expected peak plus the growth running starts have left) against the commit headroom less
              the reserve (commit-covered), and its private working set (the same sum in privately resident memory,
              privateResidentNeed) against free RAM above the pressure floor (ram-covered). Private bytes are commit
              and overstate residency (a driver's device-memory mirror charges commit and holds no page); the whole
              working set overstates it too (clean mapped-file and image pages are shared and reclaimed), so it is
              printed (workingSet=, unmeasured before the kind's first observation) and never judged. admitted= is
              both, the decision gate, lease and the MCP World start make for that kind. Each live start's kind, PID,
              private bytes, private working set, working set, the figure judged and readiness follow.
              A kind's expected peak is the high percentile of its last ten observed starts, kept per machine in the
              lease root, so one unusual session moves it only briefly; unused growth tapers away over
              HostStartTracker.Plateau and returns on renewed allocation.
              PRESSURE wins over CAPACITY within one reading. Defaults scale with the machine: CAPACITY needs three
              logical processors idle, but never fewer than 40% of them nor more than half; PRESSURE is free RAM
              below 1/8 of the installed memory (rounded up to whole gigabytes) or free disk below
              {HostLoadThresholds.DiskPressureGb}GB. No fixed amount of free RAM or commit gates a start: it needs its own private
              working set free above the pressure floor and its own private bytes plus the reserve in the commit headroom. Here:
              CPU below {HostLoadThresholds.ThisMachine.CapacityCpuPercent:0.#}%, {HostLoadThresholds.ThisMachine.CommitReservePercent:0.##}% of the commit limit kept unclaimed (allocation fails at the limit with RAM still free); pressure below {HostLoadThresholds.ThisMachine.PressureRamGb:0.##}GB RAM. Options override these defaults; gate, lease and the MCP World start admit by them unchanged.

              GPU work is a World that opens a device (Puck.World or Puck.World.dll), or a test host for {string.Join(separator: ", ", values: HostProcesses.DeviceTestAssemblies)}
              whose arguments can select a Gpu-trait test: a run carrying --filter-not-trait Category=Gpu is not. Builds,
              restores, MSBuild nodes, compilers and shells never are, and the verb never counts itself.
              Canary --list/--plan, parity/counters compare, and help run no GPU work.
              reuseNodes counts MSBuild nodes left for reuse by a build or restore run without
              -nodeReuse:false.

              The readings are cheap operating-system queries: the verb starts no process and is never
              itself heavy or GPU work. Without --watch it takes one reading over one second and exits.

              Thresholds must be finite and nonnegative; --capacity-cpu and --commit-reserve must be at most 100.
              Exit codes: 0 done; 2 refused (invalid thresholds or an interval or window below 1); 130 cancelled.
            """);
        command.SetAction(action: (parseResult, cancellationToken) => Task.Run(function: () => Load(
            cancellationToken: cancellationToken,
            interval: parseResult.GetValue(option: intervalOption),
            json: parseResult.GetValue(option: jsonOption),
            thresholds: new HostLoadThresholds(
                CapacityCpuPercent: parseResult.GetValue(option: capacityCpuOption),
                CommitReservePercent: parseResult.GetValue(option: commitReserveOption),
                InstalledRamGb: HostLoadThresholds.ThisMachine.InstalledRamGb,
                PressureDiskGb: parseResult.GetValue(option: pressureDiskOption),
                PressureRamGb: parseResult.GetValue(option: pressureRamOption)
            ),
            watch: parseResult.GetValue(option: watchOption),
            window: parseResult.GetValue(option: windowOption)
        ), cancellationToken: cancellationToken));

        return command;
    }

    private static int Heap(int processId, int minimumMebibytes) {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        HeapWalk walk;

        try {
            walk = ManagedHeapWalk.Run(minimumBytes: (((long)minimumMebibytes) << 20), processId: processId);
        } catch (Exception exception) when (exception is Microsoft.Diagnostics.NETCore.Client.ServerNotAvailableException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException) {
            return CliExit.Refuse(verb: HeapVerb, what: $"--pid {processId}", why: exception.Message);
        }

        var named = walk.Tenants.Sum(selector: static tenant => tenant.Bytes);

        Console.Out.WriteLine(value: FormattableString.Invariant($"[host heap: pid={processId} collection={walk.Collection} live-objects={walk.Nodes} | large objects (at least {ManagedHeapWalk.LargeObjectBytes} bytes) objects={walk.Objects} bytes={walk.Bytes} | tenants of at least {minimumMebibytes} MiB: {walk.Tenants.Count} holding {named} bytes | walk-ms={clock.ElapsedMilliseconds}"));
        foreach (var tenant in walk.Tenants) {
            Console.Out.WriteLine(value: FormattableString.Invariant($"tenant {System.Text.Json.JsonSerializer.Serialize(tenant.Type)} held-by={System.Text.Json.JsonSerializer.Serialize(tenant.Holder)} objects={tenant.Objects} bytes={tenant.Bytes}"));
        }
        Console.Out.WriteLine(value: "The runtime wrote the heap during one blocking full collection with every managed thread suspended; the holder is the type of the first referrer found, or the kind of root.]");

        return CliExit.Success;
    }
    private static Command CreateHeap() {
        var pidOption = new Option<int>(name: "--pid") { Description = "The .NET process to walk, such as a World's process id from puck_world_start.", Required = true };
        var minimumOption = CliOptions.Count(defaultValue: 4, description: "The smallest large-object-heap tenant to name, in MiB; smaller large objects count only in the totals.", minimum: 0, name: "--min-mib");
        var command = new Command(
            description: "Name the large object heap's tenants of another .NET process from the runtime's own heap dump.",
            name: "heap"
        ) { pidOption, minimumOption };

        command.Detail(detail: """
            Opens an EventPipe session on the process's diagnostics port with the runtime provider's heap-collect and
            heap-dump keywords. The runtime runs one blocking full collection with every managed thread suspended and
            writes each live object and root to the stream, which this process reads: the target pays the collection's
            pause and nothing else, and no engine process carries the trace-parsing package. The report gives the
            large object heap's totals (objects of at least 85,000 bytes) and each tenant of at least --min-mib MiB by
            type and first holder (the type of an object referring to it, or the kind of root that keeps it).
            Reading the stream takes seconds on a heap of a few gigabytes. Never attach during a frame-time reading:
            the pause is one long frame in the target's host.frames.

            Exit codes: 0 done; 2 refused (the process exposes no diagnostics port, or the runtime wrote no dump).
            """);
        command.SetAction(action: (parseResult, cancellationToken) => Task.Run(function: () => Heap(
            minimumMebibytes: parseResult.GetValue(option: minimumOption),
            processId: parseResult.GetValue(option: pidOption)
        ), cancellationToken: cancellationToken));

        return command;
    }

    /// <summary>Creates <c>puck host</c>.</summary>
    /// <returns>The command.</returns>
    public static Command Create() =>
        new(
            description: "Verbs about the machine an agent runs on.",
            name: "host"
        ) { CreateLoad(), CreateHeap() };
}
