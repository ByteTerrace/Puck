using System.CommandLine;
using Puck.AdvancedGamingBrick.Forge;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.Cli.CartridgeCost;

/// <summary>
/// <c>puck cartridge-cost</c> — boots <see cref="CartridgeCostMeasurement"/>'s probes on the real machines and prints the
/// largest per-frame iteration count each sustains, the evidence <c>CartridgeCost</c>'s weights are folded from.
/// </summary>
internal static class CartridgeCostCommand {
    private static readonly string[] Targets = ["cgb", "agb"];

    // Every probe boots its own machine and shares nothing with another, so shapes are measured concurrently and
    // reported in sweep order; the counts do not depend on the schedule.
    private static int Run(string? target, TextWriter output) {
        foreach (var measured in ((target is null)
            ? Targets
            : [target])) {
            output.WriteLine(value: $"{measured} grain {CartridgeCostMeasurement.Shapes(target: measured)[0].Granularity}");
            foreach (var capacity in CartridgeCostMeasurement.Shapes(target: measured).AsParallel().AsOrdered().Select(selector: static shape => CartridgeCostMeasurement.LargestSustained(
                run: Ticks,
                shape: shape
            ))) {
                output.WriteLine(value: $"{measured} {capacity.Shape.Name,-18} {(capacity.Clipped
                    ? $"{capacity.Iterations} CLIPPED"
                    : $"{capacity.Iterations,6}  units {(capacity.Cost.IsKnown
                        ? capacity.Cost.Cycles.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
                        : "unmodeled")}")}");
            }
        }

        return 0;
    }
    private static int Ticks(CartridgeDocument document, int frames) {
        var compiled = ((document.Target == "agb")
            ? new AgbCartridgeCompiler().Compile(document: document)
            : new HgbCartridgeCompiler().Compile(document: document)
        );
        var address = compiled.Variables[CartridgeCostMeasurement.Ticks];

        if (compiled.Target == "agb") {
            using var advanced = new AgbVerifyMachineDriver(
                label: "cartridge-cost",
                rom: compiled.Rom
            );

            advanced.RunFrames(
                frames: frames,
                keys: AgbKeys.None
            );
            return advanced.ReadByte(address: address);
        }

        using var humble = new VerifyMachineDriver(
            label: "cartridge-cost",
            rom: compiled.Rom
        );

        humble.RunFrames(
            buttons: JoypadButtons.None,
            frames: frames
        );
        return humble.Read(address: ((ushort)address));
    }

    public static Command Create() {
        var target = new Option<string?>(name: "--target") { Description = "Measure one target only: cgb or agb. Both are measured when omitted." };

        target.AcceptOnlyFromAmong(values: Targets);
        var command = new Command(
            description: """
            Measure each cartridge primitive's sustained per-frame capacity on the real machines.

            Boots probe cartridges on the Color and Advanced machines and bisects, per shape, the largest per-frame
            iteration count each machine sustains at full frame rate. Cost per iteration is inversely proportional to
            that capacity; fold the reported capacities into CartridgeCost's weights and CartridgeCostProfile's
            reservations. A row marked CLIPPED reached the search's ceiling rather than the machine's.
            """,
            name: "cartridge-cost"
        ) { target };

        command.SetAction(action: result => Run(
            output: Console.Out,
            target: result.GetValue(option: target)
        ));
        return command;
    }
}
