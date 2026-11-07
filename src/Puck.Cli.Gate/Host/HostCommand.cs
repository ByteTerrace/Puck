using System.CommandLine;

namespace Puck.Cli.Host;

/// <summary><c>puck host</c> — the verbs about the machine an agent runs on: <c>load</c> reports CPU, memory, disk and
/// GPU busyness as admission lines (see <see cref="HostLoadMonitor"/>).</summary>
public static class HostCommand {
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
                        Console.Out.WriteLine(FormattableString.Invariant($"admission {demand.Kind}: headroom={demand.HeadroomGb:0.0}GB peak={demand.OwnGb:0.0}GB growth={demand.GrowthGb:0.0}GB need={demand.NeedGb:0.0}GB commit-covered={demand.Covered}"));
                    }
                    foreach (var start in sample.Starts ?? []) {
                        Console.Out.WriteLine(FormattableString.Invariant($"start {start.Kind} {start.Id}: private={start.PrivateGb:0.0}GB ready={start.Ready}"));
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
                    ["starts"] = System.Text.Json.JsonSerializer.SerializeToNode(sample.Starts),
                    ["demands"] = System.Text.Json.JsonSerializer.SerializeToNode(demands),
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
    private static Command CreateLoad() {
        var jsonOption = CliOptions.Json(description: "Write one JSON object per transition reading: at, cpuPercent (window mean), freeRamGb, freeDiskGb, commitPercent, commitChargeGb, commitLimitGb, starts, per-kind demands, reuseNodes, gpuHolder, heavyTestHolder and admission.");
        var watchOption = new Option<bool>(name: "--watch") { Description = "Keep reading every --interval seconds and print a line on each transition, until cancelled." };
        var intervalOption = CliOptions.Count(defaultValue: 10, description: "Seconds between readings with --watch.", minimum: 1, name: "--interval");
        var windowOption = CliOptions.Count(defaultValue: 6, description: "Readings the CPU mean covers with --watch; CAPACITY waits until that many exist.", minimum: 1, name: "--window");
        var capacityCpuOption = CliOptions.Amount(defaultValue: HostLoadThresholds.ThisMachine.CapacityCpuPercent, description: "CAPACITY needs the CPU mean below this percentage (with --capacity-ram).", maximum: 100, minimum: 0, name: "--capacity-cpu");
        var capacityRamOption = CliOptions.Amount(defaultValue: HostLoadThresholds.ThisMachine.CapacityRamGb, description: "CAPACITY needs free memory above this many gigabytes (with --capacity-cpu).", minimum: 0, name: "--capacity-ram");
        var capacityCommitOption = CliOptions.Amount(defaultValue: HostLoadThresholds.ThisMachine.CapacityCommitPercent, description: "CAPACITY needs the commit charge below this percentage of the commit limit, when the platform reports one.", maximum: 100, minimum: 0, name: "--capacity-commit");
        var pressureRamOption = CliOptions.Amount(defaultValue: HostLoadThresholds.ThisMachine.PressureRamGb, description: "PRESSURE when free memory is below this many gigabytes.", minimum: 0, name: "--pressure-ram");
        var pressureDiskOption = CliOptions.Amount(defaultValue: HostLoadThresholds.ThisMachine.PressureDiskGb, description: "PRESSURE when free disk on the working directory's drive is below this many gigabytes.", minimum: 0, name: "--pressure-disk");


        var command = new Command(
            description: "Report CPU, memory, disk and GPU busyness as admission lines: GPU busy|idle, PRESSURE, CAPACITY, LOADED.",
            name: "load"
        ) { watchOption, intervalOption, windowOption, capacityCpuOption, capacityRamOption, capacityCommitOption, pressureRamOption, pressureDiskOption, jsonOption };

        command.Detail(detail: $"""
              Lines (each carries cpu=, freeRAM=, freeDisk=, commit= where the platform reports a commit limit, and reuseNodes=):
                GPU busy (<process> <id>) | GPU idle   at the first reading and on every change after
                PRESSURE <why>                         free memory or disk falls under its pressure
                                                       threshold, and again when the reasons change
                CAPACITY                               otherwise, the CPU mean under --capacity-cpu and free
                                                       memory over --capacity-ram and commit charge under
                                                       --capacity-commit of the commit limit, once --window
                                                       readings exist
                LOADED                                 otherwise, once --window readings exist: capacity ended
              One line per transition: a state that holds prints nothing more.
              Each transition also reports per-kind commit demand (expected peak plus remaining growth), and each
              live start's kind, PID, private bytes and readiness. Peaks are measured per machine in the lease root;
              unused growth tapers away over HostStartTracker.Plateau and returns on renewed allocation.
              PRESSURE wins over CAPACITY within one reading. Defaults scale with the machine: CAPACITY needs free
              RAM above 5/16 of the installed memory (rounded up to whole gigabytes) and three logical processors
              idle, but never fewer than 40% of them nor more than half; PRESSURE is free RAM below 1/8 of the
              installed memory or free disk below {HostLoadThresholds.DiskPressureGb}GB. Here: CPU below {HostLoadThresholds.ThisMachine.CapacityCpuPercent:0.#}%, free RAM above
              {HostLoadThresholds.ThisMachine.CapacityRamGb:0.##}GB, commit under {HostLoadThresholds.ThisMachine.CapacityCommitPercent:0.##}% of the commit limit (allocation fails at the limit with RAM still free); pressure below {HostLoadThresholds.ThisMachine.PressureRamGb:0.##}GB RAM. Options override these defaults; gate, lease and the MCP World start admit by them unchanged.

              GPU work is a World that opens a device (Puck.World or Puck.World.dll), or a test host for {string.Join(separator: ", ", values: HostProcesses.DeviceTestAssemblies)}
              whose arguments can select a Gpu-trait test: a run carrying --filter-not-trait Category=Gpu is not. Builds,
              restores, MSBuild nodes, compilers and shells never are, and the verb never counts itself.
              Canary --list/--plan, parity/counters compare, and help run no GPU work.
              reuseNodes counts MSBuild nodes left for reuse by a build or restore run without
              -nodeReuse:false.

              The readings are cheap operating-system queries: the verb starts no process and is never
              itself heavy or GPU work. Without --watch it takes one reading over one second and exits.

              Thresholds must be finite and nonnegative; --capacity-cpu and --capacity-commit must be at most 100.
              Exit codes: 0 done; 2 refused (invalid thresholds or an interval or window below 1); 130 cancelled.
            """);
        command.SetAction(action: (parseResult, cancellationToken) => Task.Run(function: () => Load(
            cancellationToken: cancellationToken,
            interval: parseResult.GetValue(option: intervalOption),
            json: parseResult.GetValue(option: jsonOption),
            thresholds: new HostLoadThresholds(
                CapacityCpuPercent: parseResult.GetValue(option: capacityCpuOption),
                CapacityCommitPercent: parseResult.GetValue(option: capacityCommitOption),
                CapacityRamGb: parseResult.GetValue(option: capacityRamOption),
                PressureDiskGb: parseResult.GetValue(option: pressureDiskOption),
                PressureRamGb: parseResult.GetValue(option: pressureRamOption)
            ),
            watch: parseResult.GetValue(option: watchOption),
            window: parseResult.GetValue(option: windowOption)
        ), cancellationToken: cancellationToken));

        return command;
    }

    /// <summary>Creates <c>puck host</c>.</summary>
    /// <returns>The command.</returns>
    public static Command Create() =>
        new(
            description: "Verbs about the machine an agent runs on.",
            name: "host"
        ) { CreateLoad() };
}
