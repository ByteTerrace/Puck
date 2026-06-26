using Puck.HumbleGamingBrick.Conformance.Protocol;

namespace Puck.HumbleGamingBrick.Conformance;

/// <summary>Executes a single classified ROM end to end: loads the image, builds a machine for the case's model,
/// dispatches to the runner for the case's protocol, and returns the outcome. Any load or emulation error is
/// converted to an inconclusive result so one broken ROM never aborts a run.</summary>
public static class ConformanceEngine {
    /// <summary>Runs one test case to completion.</summary>
    /// <param name="romCase">The classified ROM to run.</param>
    /// <returns>The outcome, always tagged with <paramref name="romCase"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="romCase"/> is <see langword="null"/>.</exception>
    public static TestOutcome Execute(RomCase romCase) {
        ArgumentNullException.ThrowIfNull(argument: romCase);

        byte[] rom;

        try {
            rom = File.ReadAllBytes(path: romCase.FullPath);
        }
        catch (IOException exception) {
            return new(Case: romCase, Status: TestStatus.Inconclusive, Detail: "read error: " + exception.Message);
        }

#pragma warning disable CA1031 // A test ROM can fault the core in many ways; we record the fault, not crash the run.
        try {
            using var handle = MachineFactory.Create(rom: rom, model: romCase.Model);
            var machine = handle.Machine;

            return romCase.Protocol switch {
                ResultProtocol.Mooneye => MooneyeRunner.Run(romCase: romCase, machine: machine),
                ResultProtocol.Blargg => BlarggRunner.Run(romCase: romCase, machine: machine),
                ResultProtocol.GbMicrotest => GbMicrotestRunner.Run(romCase: romCase, machine: machine),
                ResultProtocol.Screenshot => ScreenshotRunner.Run(romCase: romCase, machine: machine),
                _ => new(Case: romCase, Status: TestStatus.Inconclusive, Detail: "unknown protocol"),
            };
        }
        catch (Exception exception) {
            return new(Case: romCase, Status: TestStatus.Inconclusive, Detail: exception.GetType().Name + ": " + exception.Message);
        }
#pragma warning restore CA1031
    }
}
