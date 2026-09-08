using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Puck.GamingBricks;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Compares instruction stepping with cycle-budget execution, including complete state and drained audio.
/// Each packet must stop at the same instruction boundary, including the final instruction's cycle overshoot.
/// This is a differential execution contract; it does not independently establish hardware conformance.</summary>
internal static class ExecutionComparisonProbe {
    internal static (bool Pass, string Detail) Run(byte[] rom, ReadOnlyMemory<byte> bios, string label,
        int packets = 192, bool mutableRam = false, bool frames = false) {
        // A timer decorator keeps the reference on the interface-driven clock path. It forwards hardware state
        // verbatim, but cannot publish the built-in controllers' shared readiness cache to the reference bus.
        using var reference = AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(bios: bios, rom: rom),
            compose: static services => {
                services.AddScoped<AgbTimerController>();
                services.AddScoped<IAgbTimerController>(implementationFactory: static provider =>
                    new ReferenceTimerController(inner: provider.GetRequiredService<AgbTimerController>()));
            });
        using var candidate = AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(bios: bios, rom: rom));
        var expected = reference.Machine;
        var actual = candidate.Machine;
        expected.DirectBoot();
        actual.DirectBoot();
        expected.Apu.ConfigureOutput(sampleRate: 32_000);
        actual.Apu.ConfigureOutput(sampleRate: 32_000);

        if (mutableRam) {
            foreach (var machine in new[] { expected, actual }) {
                var bus = (AgbBus)machine.Bus;
                for (var offset = 0; offset < 32; ++offset) {
                    bus.DebugWrite8(address: (0x03000000u + ((uint)offset)), value: rom[offset]);
                }
                machine.Cpu.SetupDirectBoot(entryPoint: 0x03000000u);
            }
        }

        short[] expectedAudio = new short[4096], actualAudio = new short[4096];
        long referenceTicks = 0, candidateTicks = 0, audioSamples = 0;
        AgbMachineSnapshot? checkpoint = null;
        ReadOnlySpan<int> budgets = [0, -1, 1, 2, 3, 17, 127, 1024, 8192, 32768];

        for (var packet = 0; packet < packets; ++packet) {
            if (!frames && (packet == 64)) {
                checkpoint = expected.Snapshot();
            }
            if (mutableRam && (packet == 64)) {
                ((AgbBus)expected.Bus).DebugWrite8(address: 0x03000018u, value: 7);
                ((AgbBus)actual.Bus).DebugWrite8(address: 0x03000018u, value: 7);
            }
            if (!frames && (packet == 96)) {
                expected.Restore(snapshot: checkpoint!);
                actual.Restore(snapshot: checkpoint!);
            }
            if (mutableRam && (packet == 128)) {
                foreach (var machine in new[] { expected, actual }) {
                    // DMA3 replaces an instruction that may already be in the fetch pipeline.
                    machine.Bus.Write32(address: 0x02000100u, value: 0xE2833009u, access: BusAccessType.NonSequential);
                    machine.Bus.Write32(address: 0x040000D4u, value: 0x02000100u, access: BusAccessType.NonSequential);
                    machine.Bus.Write32(address: 0x040000D8u, value: 0x03000018u, access: BusAccessType.NonSequential);
                    machine.Bus.Write32(address: 0x040000DCu, value: 0x84000001u, access: BusAccessType.NonSequential);
                }
            }

            var keys = ((ushort)(0x3FF ^ ((packet % 31 == 0) ? 9 : 0)));
            expected.SetKeyInput(keys: keys);
            actual.SetKeyInput(keys: keys);
            var budget = (frames ? AdvancedGamingBrickMachine.CyclesPerFrame : budgets[(packet % budgets.Length)]);
            int expectedSteps = 0, actualSteps = 0;
            // Alternate order so one backend does not always inherit the other's hot host CPU.
            for (var turn = 0; turn < 2; ++turn) {
                var start = Stopwatch.GetTimestamp();
                if (((packet + turn) & 1) == 0) {
                    var target = expected.Cycles + budget;
                    while (expected.Cycles < target) {
                        expected.Step();
                        ++expectedSteps;
                    }
                    referenceTicks += Stopwatch.GetTimestamp() - start;
                } else {
                    actualSteps = actual.RunCycles(cycles: budget);
                    candidateTicks += Stopwatch.GetTimestamp() - start;
                }
            }

            if ((expectedSteps != actualSteps) || !expected.Snapshot().ContentEquals(other: actual.Snapshot())) {
                return (false, $"{label}: packet {packet}, budget {budget} diverged "
                    + $"(instruction boundaries {expectedSteps}/{actualSteps}; master clocks {expected.Cycles}/{actual.Cycles})");
            }
            while (true) {
                var samples = expected.Apu.DrainSamples(destination: expectedAudio);
                if ((samples != actual.Apu.DrainSamples(destination: actualAudio))
                    || !expectedAudio.AsSpan(start: 0, length: samples).SequenceEqual(other: actualAudio.AsSpan(start: 0, length: samples))) {
                    return (false, $"{label}: audio diverged at packet {packet}");
                }
                if (samples == 0) {
                    break;
                }
                audioSamples += samples;
            }
        }

        return (true, $"{label}: {packets} state/audio checkpoints, {audioSamples:N0} PCM samples; "
            + $"execution {referenceTicks * 1000.0 / Stopwatch.Frequency:F1} ms stepping / "
            + $"{candidateTicks * 1000.0 / Stopwatch.Frequency:F1} ms cycle-budget execution (includes JIT warm-up)");
    }

    private sealed class ReferenceTimerController(AgbTimerController inner) : IAgbTimerController, ISnapshotable {
        public bool HasPendingLatch => inner.HasPendingLatch;
        public void EnsureScheduled(long now) => inner.EnsureScheduled(now: now);
        public void EnsurePerCycle(long now) => inner.EnsurePerCycle(now: now);
        public void RunCycle(long clock) => inner.RunCycle(clock: clock);
        public ushort ReadRegister(uint offset) => inner.ReadRegister(offset: offset);
        public void WriteRegister(uint offset, ushort value) => inner.WriteRegister(offset: offset, value: value);
        public void SaveState(StateWriter writer) => inner.SaveState(writer: writer);
        public void LoadState(StateReader reader) => inner.LoadState(reader: reader);
    }
}
