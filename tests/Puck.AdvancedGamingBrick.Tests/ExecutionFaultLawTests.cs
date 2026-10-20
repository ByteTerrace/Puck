using System.Buffers.Binary;
using Puck.Machines;

namespace Puck.AdvancedGamingBrick.Tests;

/// <summary>
/// A hosted core whose program can no longer run stops with a named <see cref="AgbExecutionFaultException"/>, which the
/// queued worker turns into the host's fault, instead of running on as a black screen. Each cartridge reaches one
/// terminal state with the bundled firmware and parks there; a well-behaved loop runs on.
/// </summary>
public sealed class ExecutionFaultLawTests {
    [Theory]
    // An architecturally undefined ARM encoding: the firmware's vector parks the CPU in Undefined mode.
    [InlineData(0xE7F000F0u, AgbExecutionFaultKind.UndefinedInstruction)]
    // MOV PC, #0x10000000: a jump into the unused region above the cartridge windows.
    [InlineData(0xE3A0F201u, AgbExecutionFaultKind.UnmappedExecution)]
    // MSR CPSR_c, #0xD7: Abort mode, which nothing on the machine enters.
    [InlineData(0xE321F0D7u, AgbExecutionFaultKind.Abort)]
    public void TerminalStatesFaultTheCore(uint instruction, AgbExecutionFaultKind kind) {
        using var core = new AdvancedGamingBrickCore(configuration: AgbFirmware.CreateConfiguration(
            bootMode: MachineBootMode.Fast,
            cartridgeRom: Rom(first: instruction)
        ));

        var fault = Assert.Throws<AgbExecutionFaultException>(testCode: () => core.RunCycles(cycles: AdvancedGamingBrickMachine.CyclesPerFrame));

        Assert.Equal(expected: kind, actual: fault.Kind);
    }
    [Fact]
    public void ALinkedConsoleFaultsTheSession() {
        using var healthy = Linkable(first: 0xEAFFFFFEu);
        using var faulted = Linkable(first: 0xE7F000F0u);
        using var session = new AgbLinkSession(healthy, faulted);

        var fault = Assert.Throws<AgbExecutionFaultException>(testCode: () => session.Run(cycles: AdvancedGamingBrickMachine.CyclesPerFrame));

        Assert.Equal(expected: AgbExecutionFaultKind.UndefinedInstruction, actual: fault.Kind);
    }
    [Fact]
    public void ARunningLoopDoesNotFault() {
        using var core = new AdvancedGamingBrickCore(configuration: AgbFirmware.CreateConfiguration(
            bootMode: MachineBootMode.Fast,
            cartridgeRom: Rom(first: 0xEAFFFFFEu)
        ));

        core.RunCycles(cycles: (AdvancedGamingBrickMachine.CyclesPerFrame * 2));
    }

    private static MachineInstance<AdvancedGamingBrickMachine, AgbMachineConfiguration> Linkable(uint first) {
        var instance = AgbMachineFactory.Create(configuration: AgbFirmware.CreateConfiguration(
            bootMode: MachineBootMode.Fast,
            cartridgeRom: Rom(first: first)
        ));

        instance.Machine.DirectBoot();

        return instance;
    }
    // The instruction at the cartridge entry, then a branch-to-self.
    private static byte[] Rom(uint first) {
        var rom = new byte[1024];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: rom, value: first);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: rom.AsSpan(start: 4), value: 0xEAFFFFFEu);

        return rom;
    }
}
