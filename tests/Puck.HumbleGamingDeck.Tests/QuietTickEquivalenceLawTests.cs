using Puck.HumbleGamingDeck.Forge;
using Puck.Machines;

namespace Puck.HumbleGamingDeck.Tests;

/// <summary>Verifies that <see cref="HgdMachine.RunCycles"/>, which skips the master ticks that end no dot and carry no
/// M2 edge or read point, leaves the state the reference stepping of every tick through
/// <see cref="HgdMachine.StepMasterTick"/> leaves.</summary>
public sealed class QuietTickEquivalenceLawTests {
    /// <summary>Runs each shipped cartridge at one CPU/PPU alignment both ways over budgets that end on every phase,
    /// changing the pads between budgets, and compares the whole serialized machine after each budget.</summary>
    /// <param name="alignment">The configured master-tick alignment.</param>
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [Theory]
    public void SkippedRunsMatchReferenceStepping(int alignment) {
        foreach (var (name, build) in DeckTestCartridges.All) {
            var configuration = new HgdMachineConfiguration(
                cartridge: HgdCartridge.Load(image: build()),
                powerOn: new HgdPowerOnProfile(alignmentPhase: alignment)
            );
            var skipped = new HgdMachine(configuration: configuration);
            var reference = new HgdMachine(configuration: configuration);
            var skippedState = new StateWriter();
            var referenceState = new StateWriter();
            var budgets = new ulong[] { 1, 2, 3, 5, 7, 11, 13, 357_366, 100_003, 23, 357_368, 4_099, 250_000, 17, 600_000 };

            for (var step = 0; (step < budgets.Length); ++step) {
                var buttons = ((HgdButtons)((step * 37) & 0xFF));

                skipped.Controllers.SetButtons(buttons: buttons, port: 0);
                reference.Controllers.SetButtons(buttons: buttons, port: 0);
                skipped.RunCycles(masterTicks: budgets[step]);
                reference.Clock.AddBudget(masterTicks: budgets[step]);
                while (reference.MasterTicks < reference.Clock.RunTargetCycles) {
                    reference.StepMasterTick();
                }

                skippedState.Reset();
                referenceState.Reset();
                skipped.SerializeState(writer: skippedState);
                reference.SerializeState(writer: referenceState);
                Assert.True(
                    condition: skippedState.WrittenSpan.SequenceEqual(other: referenceState.WrittenSpan),
                    userMessage: $"{name} at alignment {alignment} diverged after budget {step} (master tick {reference.MasterTicks})"
                );
            }
            Assert.True(condition: (reference.FrameIndex > 3), userMessage: $"{name} ran {reference.FrameIndex} frames");
        }
    }
}
