using System.CommandLine;

using Puck.Cli.Gate;

namespace Puck.Cli.Host;

/// <summary><c>puck host</c> — the verbs about the machine an agent runs on: <c>load</c> reports CPU, memory, disk and
/// GPU busyness as admission lines (see <see cref="HostLoadMonitor"/>).</summary>
public static class HostCommand {
    private const string LoadVerb = "host load";

    private static void ValidateThreshold(Option<double?> option, double? maximum = null) {
        option.Validators.Add(item: result => {
            if ((result.GetValueOrDefault<double?>() is { } value) && (!double.IsFinite(d: value) || (value < 0) || ((maximum is { } limit) && (value > limit)))) {
                result.AddError(errorMessage: $"{option.Name} must be finite and {((maximum is { } upper) ? $"between 0 and {upper}" : "nonnegative")}.");
            }
        });
    }
    private static int Load(bool watch, int interval, int window, HostLoadThresholds thresholds, GateComposition composition, CancellationToken cancellationToken) {
        if ((interval < 1) || (window < 1)) {
            return CliExit.Refuse(verb: LoadVerb, what: "--interval and --window", why: "must each be at least 1.");
        }

        var probe = new HostProbe(checkoutRoot: Environment.CurrentDirectory, gpuVerbGrammars: composition.GpuVerbGrammars);
        var spacing = TimeSpan.FromSeconds(seconds: interval);

        // One reading judges its own CPU figure; a watch judges the mean over the last --window readings.
        var monitor = new HostLoadMonitor(cpuSamples: (watch ? window : 1), thresholds: thresholds);

        do {
            foreach (var line in monitor.Observe(sample: probe.Sample(firstInterval: TimeSpan.FromSeconds(seconds: 1)))) {
                Console.Out.WriteLine(value: line);
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
    private static Command CreateLoad(GateComposition composition) {
        var watchOption = new Option<bool>(name: "--watch") { Description = "Keep reading every --interval seconds and print a line on each transition, until cancelled." };
        var intervalOption = new Option<int>(name: "--interval") { DefaultValueFactory = _ => 10, Description = "Seconds between readings with --watch." };
        var windowOption = new Option<int>(name: "--window") { DefaultValueFactory = _ => 6, Description = "Readings the CPU mean covers with --watch; CAPACITY waits until that many exist." };
        var capacityCpuOption = new Option<double?>(name: "--capacity-cpu") { DefaultValueFactory = _ => HostLoadThresholds.ThisMachine.CapacityCpuPercent, Description = "CAPACITY needs the CPU mean below this percentage (with --capacity-ram)." };
        var capacityRamOption = new Option<double?>(name: "--capacity-ram") { DefaultValueFactory = _ => HostLoadThresholds.ThisMachine.CapacityRamGb, Description = "CAPACITY needs free memory above this many gigabytes (with --capacity-cpu)." };
        var pressureRamOption = new Option<double?>(name: "--pressure-ram") { DefaultValueFactory = _ => HostLoadThresholds.ThisMachine.PressureRamGb, Description = "PRESSURE when free memory is below this many gigabytes." };
        var pressureDiskOption = new Option<double?>(name: "--pressure-disk") { DefaultValueFactory = _ => HostLoadThresholds.ThisMachine.PressureDiskGb, Description = "PRESSURE when free disk on the working directory's drive is below this many gigabytes." };

        ValidateThreshold(maximum: 100, option: capacityCpuOption);
        ValidateThreshold(option: capacityRamOption);
        ValidateThreshold(option: pressureRamOption);
        ValidateThreshold(option: pressureDiskOption);
        var command = new Command(
            description: "Report CPU, memory, disk and GPU busyness as admission lines: GPU busy|idle, PRESSURE, CAPACITY, LOADED.",
            name: "load"
        ) { watchOption, intervalOption, windowOption, capacityCpuOption, capacityRamOption, pressureRamOption, pressureDiskOption };

        command.Detail(detail: $"""
              Lines (each carries cpu=, freeRAM=, freeDisk= and reuseNodes=):
                GPU busy (<process> <id>) | GPU idle   at the first reading and on every change after
                PRESSURE <why>                         free memory or disk falls under its pressure
                                                       threshold, and again when the reasons change
                CAPACITY                               otherwise, the CPU mean under --capacity-cpu and free
                                                       memory over --capacity-ram, once --window readings exist
                LOADED                                 otherwise, once --window readings exist: capacity ended
              One line per transition: a state that holds prints nothing more.
              PRESSURE wins over CAPACITY within one reading. Defaults scale with the machine: CAPACITY needs free
              RAM above 5/16 of the installed memory (rounded up to whole gigabytes) and three logical processors
              idle, but never fewer than 40% of them nor more than half; PRESSURE is free RAM below 1/8 of the
              installed memory or free disk below {HostLoadThresholds.DiskPressureGb}GB. Here: CPU below {HostLoadThresholds.ThisMachine.CapacityCpuPercent:0.#}%, free RAM above
              {HostLoadThresholds.ThisMachine.CapacityRamGb:0.##}GB; pressure below {HostLoadThresholds.ThisMachine.PressureRamGb:0.##}GB RAM. Options override these defaults; gate uses them unchanged.

              GPU work is the World (Puck.World or Puck.World.dll), a canary, parity or counters verb,
              or a test host for Puck.DirectX.Tests, Puck.Vulkan.Tests, Puck.World.Tests or Puck.Platform.Windows.Tests
              whose arguments can select a Gpu-trait test: a run carrying --filter-not-trait Category=Gpu is not. Builds,
              restores, MSBuild nodes, compilers and shells never are, and the verb never counts itself.
              Canary --list/--plan, parity/counters compare, and help run no GPU work.
              reuseNodes counts MSBuild nodes left for reuse by a build or restore run without
              -nodeReuse:false.

              The readings are cheap operating-system queries: the verb starts no process and is never
              itself heavy or GPU work. Without --watch it takes one reading over one second and exits.

              Thresholds must be finite and nonnegative; --capacity-cpu must be at most 100.
              Exit codes: 0 done; 2 refused (invalid thresholds or an interval or window below 1); 130 cancelled.
            """);
        command.SetAction(action: (parseResult, cancellationToken) => Task.Run(function: () => Load(
            cancellationToken: cancellationToken,
            composition: composition,
            interval: parseResult.GetValue(option: intervalOption),
            thresholds: new HostLoadThresholds(
                CapacityCpuPercent: parseResult.GetValue(option: capacityCpuOption),
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
    /// <param name="composition">The verbs composed beside this one whose own arguments decide whether a run is GPU work.</param>
    /// <returns>The command.</returns>
    public static Command Create(GateComposition composition) =>
        new(
            description: "Verbs about the machine an agent runs on.",
            name: "host"
        ) { CreateLoad(composition: composition) };
}
