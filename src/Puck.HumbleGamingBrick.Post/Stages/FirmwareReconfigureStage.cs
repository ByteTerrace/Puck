using Puck.Abstractions.Machines;
using Puck.GamingBricks;
using Puck.Hosting;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>Checks construction-fixed firmware selection and the native-boot boundary of live hardware changes.</summary>
internal sealed class FirmwareReconfigureStage : IPostStage<PostContext> {
    private const int DotsPerFrame = 70_224;
    private const ulong HostStepTicks = EngineTicks.PerSecond / 32UL;
    private const ushort CounterAddress = 0xC100;
    private const byte CounterSeed = 0x28;

    /// <inheritdoc/>
    public string Name => "firmware-reconfigure";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;
    /// <inheritdoc/>
    public bool IsConcurrent => true;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var directory = Path.Combine(path1: context.ArtifactsDirectory, path2: $"firmware-reconfigure-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(path: directory);
        try {
            CheckExternalHost(directory: directory);
            CheckCoreSelections(mode: MachineBootMode.Fast);
            CheckCoreSelections(mode: MachineBootMode.Cold);
            CheckBootBoundary(source: ConsoleModel.DmgC, target: ConsoleModel.CgbE, sourceToken: "dmg", targetToken: "cgb");
            CheckBootBoundary(source: ConsoleModel.CgbE, target: ConsoleModel.DmgC, sourceToken: "cgb", targetToken: "dmg");
            return PostStageOutcome.Pass(detail: "fast external-image hosts retain exact firmware bytes and canonical paths after the source file is changed and deleted; " +
                "hardware-only and repeated unchanged selections reach the worker without rereading the path; mode/image refusals preserve full state and subsequent execution; " +
                "both DMG-to-Color and Color-to-DMG changes refuse during native boot, unchanged hardware remains allowed, and both directions succeed after cartridge handoff");
        } catch (InvalidOperationException exception) {
            return PostStageOutcome.Fail(detail: exception.Message);
        }
    }

    private static void CheckExternalHost(string directory) {
        var path = Path.Combine(path1: directory, path2: "external firmware with spaces.bin");
        // This stationary native image would never hand off. Fast startup must retain it without executing it.
        var image = new byte[BootRomBuilder.ColorLength];
        image[0] = 0x18;
        image[1] = 0xFE;
        image[^1] = 0x7D;
        File.WriteAllBytes(path: path, bytes: image);
        using var host = (MachineHost)new GamingBrickEngine().Create(
            options: $"cgb dmgspeed fast bios=\"{path}\"", contentBytes: CreateCartridge(model: ConsoleModel.CgbE));
        RequireHostImage(host: host, image: image, model: ConsoleModel.CgbE, options: $"cgb dmgspeed fast bios={path}");
        RequireHostExecution(host: host);

        File.WriteAllBytes(path: path, bytes: [0]); // Any reload would now fail the image-length guard.
        Require(condition: host.TryReconfigure(options: "dmg", reason: out var changedReason), detail: $"bare hardware change reread or lost external firmware: {changedReason}");
        RequireHostImage(host: host, image: image, model: ConsoleModel.DmgC, options: $"dmg dmgspeed fast bios={path}");
        Require(condition: host.TryReconfigure(options: $"cgb fast bios=\"{path}\"", reason: out var repeatedReason), detail: $"unchanged explicit image selection failed: {repeatedReason}");
        RequireHostImage(host: host, image: image, model: ConsoleModel.CgbE, options: $"cgb dmgspeed fast bios={path}");

        File.Delete(path: path);
        Require(condition: host.TryReconfigure(options: $"dmgb fast bios=\"{path}\"", reason: out var deletedReason), detail: $"live host forwarding reread a deleted image path: {deletedReason}");
        RequireHostImage(host: host, image: image, model: ConsoleModel.DmgB, options: $"dmgb dmgspeed fast bios={path}");

        RequireHostRefusal(host: host, options: "dmgb cold", reasonContains: "construction-fixed");
        RequireHostRefusal(host: host, options: "dmgb bios=puck", reasonContains: "construction-fixed");
        RequireHostRefusal(host: host, options: $"dmgb bios={path}.absent", reasonContains: "construction-fixed");
        RequireHostRefusal(host: host, options: "dmgb cold fast", reasonContains: "Choose cold or fast");
        RequireHostRefusal(host: host, options: "dmgb bios=", reasonContains: "bios= requires");
        RequireHostImage(host: host, image: image, model: ConsoleModel.DmgB, options: $"dmgb dmgspeed fast bios={path}");
    }

    private static void CheckCoreSelections(MachineBootMode mode) {
        using var core = new HumbleGamingBrickCore(model: ConsoleModel.DmgC, cartridgeRom: CreateCartridge(model: ConsoleModel.DmgC), bootMode: mode);
        var other = mode == MachineBootMode.Fast ? "cold" : "fast";
        RequireCoreRefusal(core: core, options: $"dmg {other}", reasonContains: "construction-fixed");
        RequireCoreRefusal(core: core, options: "dmg bios=puck", reasonContains: "construction-fixed");
        RequireCoreRefusal(core: core, options: "dmg bios=does-not-exist.bin", reasonContains: "construction-fixed");
        var before = core.Instance.Machine.Snapshot();
        Require(condition: core.Reconfigure(options: $"dmg {mode.ToString().ToLowerInvariant()}", reason: out var reason), detail: $"repeating core startup mode was refused: {reason}");
        RequireSameSnapshot(expected: before, actual: core.Instance.Machine.Snapshot(), detail: "an unchanged core selection mutated the machine");
        core.RunCycles(cycles: DotsPerFrame * 40L);
        RequireCoreExecution(core: core);
    }

    private static void CheckBootBoundary(ConsoleModel source, ConsoleModel target, string sourceToken, string targetToken) {
        var rom = CreateCartridge(model: source);
        using var core = new HumbleGamingBrickCore(model: source, cartridgeRom: rom, dmgSpeed: true);
        using var host = new MachineHost(model: source, cartridgeRom: rom, dmgSpeed: true);

        for (var observation = 0; observation < 2; ++observation) {
            Require(condition: core.PeekByte(address: MemoryMap.BootRomDisable) == 0xFE, detail: $"{source}: core left firmware before the boundary check");
            Require(condition: host.PeekByte(address: MemoryMap.BootRomDisable) == 0xFE, detail: $"{source}: host left firmware before the boundary check");
            var coreBefore = core.Instance.Machine.Snapshot();
            var hostBefore = ObserveHost(host: host);
            Require(condition: core.Reconfigure(options: sourceToken, reason: out var coreReason), detail: $"{source}: unchanged core model during boot was refused: {coreReason}");
            Require(condition: host.TryReconfigure(options: sourceToken, reason: out var hostReason), detail: $"{source}: unchanged host model during boot was refused: {hostReason}");
            RequireSameSnapshot(expected: coreBefore, actual: core.Instance.Machine.Snapshot(), detail: $"{source}: unchanged core model altered native boot");
            RequireSameSnapshot(expected: hostBefore.Snapshot, actual: ObserveHost(host: host).Snapshot, detail: $"{source}: unchanged host model altered native boot");
            RequireCoreRefusal(core: core, options: targetToken, reasonContains: "boot ROM is still running");
            RequireHostRefusal(host: host, options: targetToken, reasonContains: "boot ROM is still running", checkExecution: false);
            core.RunCycles(cycles: DotsPerFrame * 8L);
            StepHost(host: host, segments: 8);
        }

        core.RunCycles(cycles: DotsPerFrame * 24L);
        StepHost(host: host, segments: 24);
        Require(condition: core.PeekByte(address: MemoryMap.BootRomDisable) == 0xFF && host.PeekByte(address: MemoryMap.BootRomDisable) == 0xFF,
            detail: $"{source}: refused model changes interrupted cartridge handoff");
        var pc = core.Instance.GetRequiredService<ICpu>().ProgramCounter;
        var clock = core.Instance.Machine.Snapshot().TakenAt;
        var counter = core.PeekByte(address: CounterAddress);
        Require(condition: core.Reconfigure(options: targetToken, reason: out var acceptedCore), detail: $"{source}->{target}: post-handoff core change failed: {acceptedCore}");
        Require(condition: host.TryReconfigure(options: targetToken, reason: out var acceptedHost), detail: $"{source}->{target}: post-handoff host change failed: {acceptedHost}");
        Require(condition: core.Instance.Machine.Model == target && core.Instance.GetRequiredService<ICpu>().ProgramCounter == pc &&
            core.Instance.Machine.Snapshot().TakenAt == clock && core.PeekByte(address: CounterAddress) == counter,
            detail: $"{source}->{target}: core model change rebooted or advanced cartridge execution");
        RequireHostImage(host: host, image: HgbFirmware.GetImage(model: source), model: target, options: $"{targetToken} dmgspeed cold");
        RequireCoreExecution(core: core);
        RequireHostExecution(host: host);
    }

    private static void RequireCoreRefusal(HumbleGamingBrickCore core, string options, string reasonContains) {
        var before = core.Instance.Machine.Snapshot();
        Require(condition: !core.Reconfigure(options: options, reason: out var reason) && reason.Contains(value: reasonContains, comparisonType: StringComparison.Ordinal),
            detail: $"core accepted '{options}' or refused it for the wrong reason: {reason}");
        RequireSameSnapshot(expected: before, actual: core.Instance.Machine.Snapshot(), detail: $"core refusal of '{options}' changed machine state");
    }

    private static void RequireHostRefusal(MachineHost host, string options, string reasonContains, bool checkExecution = true) {
        var before = ObserveHost(host: host);
        var beforeOptions = host.Options;
        Require(condition: !host.TryReconfigure(options: options, reason: out var reason) && reason.Contains(value: reasonContains, comparisonType: StringComparison.Ordinal),
            detail: $"host accepted '{options}' or refused it for the wrong reason: {reason}");
        Require(condition: host.Options == beforeOptions, detail: $"host refusal of '{options}' changed canonical options");
        RequireSameSnapshot(expected: before.Snapshot, actual: ObserveHost(host: host).Snapshot, detail: $"host refusal of '{options}' changed machine state");
        if (checkExecution) {
            RequireHostExecution(host: host);
        }
    }

    private static void RequireHostImage(MachineHost host, byte[] image, ConsoleModel model, string options) {
        var observed = ObserveHost(host: host);
        Require(condition: host.Options == options && observed.Model == model && observed.Firmware.AsSpan().SequenceEqual(other: image),
            detail: $"host did not retain exact selected-image bytes/model/options; expected '{options}', got '{host.Options}'");
    }

    private static void RequireSameSnapshot(MachineSnapshot expected, MachineSnapshot actual, string detail) =>
        Require(condition: expected.Identity == actual.Identity && expected.Data.SequenceEqual(other: actual.Data), detail: detail);

    private static void RequireCoreExecution(HumbleGamingBrickCore core) {
        core.PokeByte(address: CounterAddress, value: CounterSeed);
        core.RunCycles(cycles: 96);
        Require(condition: core.PeekByte(address: MemoryMap.BootRomDisable) == 0xFF && core.PeekByte(address: CounterAddress) != CounterSeed,
            detail: "core did not continue cartridge execution after reconfiguration");
    }

    private static void RequireHostExecution(MachineHost host) {
        host.PokeByte(address: CounterAddress, value: CounterSeed);
        StepHost(host: host, segments: 1);
        Require(condition: host.PeekByte(address: MemoryMap.BootRomDisable) == 0xFF && host.PeekByte(address: CounterAddress) != CounterSeed && host.Worker.QueueFault is null,
            detail: "host did not continue cartridge execution after reconfiguration");
    }

    private static void StepHost(MachineHost host, int segments) {
        var pad = new MachinePadState();
        for (var segment = 0; segment < segments; ++segment) {
            Require(condition: host.Step(deltaTicks: HostStepTicks, input: in pad), detail: "queued host rejected a cartridge segment");
        }
    }

    private static (MachineSnapshot Snapshot, byte[] Firmware, ConsoleModel Model) ObserveHost(MachineHost host) {
        // The public lease stops the worker before observation; reconfiguration itself always uses the live worker.
        // At pinned DMG speed, each 32 Hz segment buys exactly 131,072 cycles, leaving a zero tick remainder.
        var core = (HumbleGamingBrickCore?)host.Worker.LendCore(lender: ObservationLender.Instance);
        Require(condition: core is not null, detail: "the host had no core to observe");
        try {
            Require(condition: (HostStepTicks * core!.CyclesPerSecond % EngineTicks.PerSecond) == 0,
                detail: "the observation lease requires an exactly divisible host cycle budget");
            return (core!.Instance.Machine.Snapshot(), core.Instance.Configuration.BootRom!.ToArray(), core.Instance.Machine.Model);
        } finally {
            host.Worker.ReturnCore(hostAccumulator: 0);
        }
    }

    private static byte[] CreateCartridge(ConsoleModel model) {
        var rom = BootRomProbeCartridge.Create(probe: BootRomLayout.For(model: model).Probes[0]);
        rom[0x0100] = 0xC3;
        rom[0x0101] = 0x50;
        rom[0x0102] = 0x01;
        // Header-safe entry; increment a shared-RAM counter forever, without interrupts or cartridge-specific recipes.
        ReadOnlySpan<byte> program = [0x21, 0x00, 0xC1, 0x34, 0x18, 0xFD];
        program.CopyTo(destination: rom.AsSpan(start: 0x0150));
        return rom;
    }

    private static void Require(bool condition, string detail) {
        if (!condition) {
            throw new InvalidOperationException(message: detail);
        }
    }

    // An observation lease never runs a linked machine or accepts host work while the core is borrowed.
    private sealed class ObservationLender : IMachineCoreLender {
        public static ObservationLender Instance { get; } = new();
        public bool RunOnLinkThread(Action work) => throw new InvalidOperationException(message: "host work raced an observation lease");
        public void InvalidateLinkHistory() => throw new InvalidOperationException(message: "observation unexpectedly changed link history");
        public void SeverLink() => throw new InvalidOperationException(message: "observation lease was not returned before disposal");
    }
}
