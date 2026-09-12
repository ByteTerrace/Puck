using Puck.Abstractions.Machines;
using Puck.GamingBricks;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>Exercises packaged native firmware, startup selection, mid-boot restoration, and explicit fault paths.</summary>
internal sealed class BundledFirmwareStage : IPostStage<PostContext> {
    private const int DotsPerFrame = 70_224;
    private const int SampleRate = 32_000;
    private const ushort ResultAddress = 0xC100;
    private const byte ResultValue = 0xA7;

    /// <inheritdoc/>
    public string Name => "bundled-firmware";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;
    /// <inheritdoc/>
    public bool IsConcurrent => true;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var models = Enum.GetValues<ConsoleModel>();
        foreach (var model in models) {
            if (VerifyRevision(model: model) is { } failure) {
                return PostStageOutcome.Fail(detail: $"{model}: {failure}");
            }
        }

        if ((VerifyQueuedStartup() ?? VerifyOverridesAndRefusals()) is { } refusalFailure) {
            return PostStageOutcome.Fail(detail: refusalFailure);
        }

        return PostStageOutcome.Pass(detail: $"{models.Length} bundled images match their native generator and return private copies; " +
            "every revision cold-boots a valid cartridge, fast-starts at its seeded entry with the same firmware identity, " +
            "and restores/forks visible mid-boot state byte-identically; handheld boot emits PCM while companion boot stays silent; " +
            "the screen engine cold-boots by default and honors fast startup; external images execute without fallback, " +
            "wrong-image snapshots refuse without mutation, and malformed startup inputs refuse");
    }

    private static string? VerifyRevision(ConsoleModel model) {
        var image = HgbFirmware.GetImage(model: model);
        var generated = BootRomBuilder.Build(model: model);
        if (!image.AsSpan().SequenceEqual(other: generated)) {
            return "the bundled image differs from the current generator; regenerate firmware before shipping";
        }
        image[0] ^= 0xFF;
        if (!HgbFirmware.GetImage(model: model).AsSpan().SequenceEqual(other: generated)) {
            return "a caller mutated the package's shared firmware image";
        }

        var rom = CreateCartridge(model: model);
        using var cold = new HumbleGamingBrickCore(model: model, cartridgeRom: rom, dmgSpeed: true);
        using var fast = new HumbleGamingBrickCore(model: model, cartridgeRom: rom, dmgSpeed: true,
            bootMode: MachineBootMode.Fast);
        using var seeded = MachineFactory.Create(configuration: new MachineConfiguration(model: model, cartridgeRom: rom));
        var coldIdentity = cold.Instance.Machine.Snapshot().Identity;
        var fastSnapshot = fast.Instance.Machine.Snapshot();
        if (cold.Instance.Configuration.BootMode != MachineBootMode.Cold || cold.PeekByte(address: MemoryMap.BootRomDisable) != 0xFE ||
            cold.Instance.GetRequiredService<ICpu>().ProgramCounter != 0 || cold.PeekByte(address: 0) != generated[0]) {
            return "the convenience constructor did not begin in bundled native firmware";
        }
        if (fast.Instance.Configuration.BootMode != MachineBootMode.Fast || fast.PeekByte(address: MemoryMap.BootRomDisable) != 0xFF ||
            fast.Instance.GetRequiredService<ICpu>().ProgramCounter != 0x0100 || fast.PeekByte(address: 0) != rom[0]) {
            return "fast startup left a boot overlay or did not begin at the cartridge entry";
        }
        if (coldIdentity != fastSnapshot.Identity || coldIdentity == seeded.Machine.Snapshot().Identity ||
            !fastSnapshot.Data.SequenceEqual(other: seeded.Machine.Snapshot().Data)) {
            return "fast startup lost selected-image identity or changed the seeded machine state";
        }
        fast.RunCycles(cycles: 128);
        if (fast.PeekByte(address: ResultAddress) != ResultValue) {
            return "fast startup did not immediately execute the cartridge's result write";
        }

        cold.ConfigureAudio(sampleRate: SampleRate);
        cold.RunCycles(cycles: DotsPerFrame * 8L);
        if ((cold.PeekByte(address: MemoryMap.BootRomDisable) & 1) != 0 ||
            !cold.Framebuffer.ContainsAnyExcept(value: cold.Framebuffer[0])) {
            return "the bundled cold boot skipped its visible native startup";
        }
        var samples = new short[SampleRate * 2];
        var count = cold.DrainAudioSamples(destination: samples);
        if (count <= 0 || (count & 1) != 0 ||
            (samples.AsSpan(start: 0, length: count).ContainsAnyExcept(value: samples[0]) == model.IsSuperGameBoy())) {
            return "the pre-handoff native audio did not match the revision's chime/silence contract";
        }

        var middle = cold.Instance.Machine.Snapshot();
        using var fork = cold.Instance.Fork();
        fast.Instance.Machine.Restore(snapshot: middle);
        if (fast.PeekByte(address: MemoryMap.BootRomDisable) != 0xFE ||
            !fast.Instance.Machine.Snapshot().Data.SequenceEqual(other: middle.Data)) {
            return "restoring mid-boot state into a fast-created machine did not restore its overlay";
        }
        cold.RunCycles(cycles: DotsPerFrame * 5L);
        fast.RunCycles(cycles: DotsPerFrame * 5L);
        fork.Machine.Run(tCycles: DotsPerFrame * 5UL);
        var expected = cold.Instance.Machine.Snapshot();
        if (!expected.Data.SequenceEqual(other: fast.Instance.Machine.Snapshot().Data) ||
            !expected.Data.SequenceEqual(other: fork.Machine.Snapshot().Data) ||
            !cold.Framebuffer.SequenceEqual(other: fast.Framebuffer)) {
            return "visible startup did not replay identically across snapshot restore and fork";
        }
        cold.Instance.Machine.Restore(snapshot: middle);
        cold.RunCycles(cycles: DotsPerFrame * 5L);
        if (!expected.Data.SequenceEqual(other: cold.Instance.Machine.Snapshot().Data)) {
            return "rewinding the same cold machine did not reproduce its startup state";
        }

        cold.RunCycles(cycles: DotsPerFrame * 27L);
        if (cold.PeekByte(address: MemoryMap.BootRomDisable) != 0xFF || cold.PeekByte(address: ResultAddress) != ResultValue) {
            return "bundled startup did not hand execution to the cartridge within 40 frames";
        }
        return null;
    }

    private static string? VerifyQueuedStartup() {
        var rom = CreateCartridge(model: ConsoleModel.DmgC);
        var engine = new GamingBrickEngine();
        using var cold = (MachineHost)engine.Create(options: null, contentBytes: rom);
        using var fast = (MachineHost)engine.Create(options: "dmg fast", contentBytes: rom);
        if (cold.Options != "dmg cold" || fast.Options != "dmg fast" ||
            cold.PeekByte(address: MemoryMap.BootRomDisable) != 0xFE || fast.PeekByte(address: MemoryMap.BootRomDisable) != 0xFF) {
            return "the queued screen engine did not preserve/default its selected startup mode";
        }
        var pad = new MachinePadState();
        if (!fast.Step(deltaTicks: LinkedHostFixture.FrameTicks, input: in pad) || fast.PeekByte(address: ResultAddress) != ResultValue) {
            return "the fast queued host did not run its cartridge on the first step";
        }
        for (var frame = 0; frame < 40; ++frame) {
            if (!cold.Step(deltaTicks: LinkedHostFixture.FrameTicks, input: in pad)) {
                return "the cold queued host rejected a startup step";
            }
        }
        return cold.PeekByte(address: MemoryMap.BootRomDisable) == 0xFF && cold.PeekByte(address: ResultAddress) == ResultValue
            ? null : "the default queued host never handed execution from bundled firmware to the cartridge";
    }

    private static string? VerifyOverridesAndRefusals() {
        const ConsoleModel Model = ConsoleModel.DmgB;
        var rom = CreateCartridge(model: Model);
        var external = new byte[BootRomBuilder.MonochromeLength];
        external[0] = 0x18;
        external[1] = 0xFE; // A deliberately stationary native image: observing its loop proves the selected image ran.
        using var custom = new HumbleGamingBrickCore(model: Model, cartridgeRom: rom, bootRom: external);
        custom.RunCycles(cycles: 128);
        if (custom.PeekByte(address: 0) != 0x18 || custom.PeekByte(address: MemoryMap.BootRomDisable) != 0xFE ||
            custom.Instance.GetRequiredService<ICpu>().ProgramCounter != 0 || custom.PeekByte(address: ResultAddress) != 0) {
            return "an external boot image was ignored, silently replaced, or skipped";
        }
        using var ordinary = new HumbleGamingBrickCore(model: Model, cartridgeRom: rom);
        var before = custom.Instance.Machine.Snapshot();
        try {
            custom.Instance.Machine.Restore(snapshot: ordinary.Instance.Machine.Snapshot());
            return "a bundled-image snapshot was accepted by an external-image machine";
        } catch (InvalidOperationException exception) when (exception.Message.Contains(value: "Snapshot identity", comparisonType: StringComparison.Ordinal)) {
            if (!before.Data.SequenceEqual(other: custom.Instance.Machine.Snapshot().Data)) {
                return "wrong-image snapshot refusal changed the destination machine";
            }
        }

        foreach (var mode in Enum.GetValues<MachineBootMode>()) {
            if (RefusesArgument(action: () => HgbFirmware.CreateConfiguration(model: Model, cartridgeRom: rom,
                bootMode: mode, bootRom: new byte[BootRomBuilder.MonochromeLength - 1]), parameter: "bootRom") is { } shortImage) {
                return $"{mode} startup: {shortImage}";
            }
        }
        return RefusesArgument(action: () => HgbFirmware.GetImage(model: (ConsoleModel)255), parameter: "model") ??
            RefusesArgument(action: () => HgbFirmware.CreateConfiguration(model: Model, cartridgeRom: rom,
                bootMode: (MachineBootMode)255), parameter: "bootMode") ??
            RefusesArgument(action: () => new MachineConfiguration(model: Model, cartridgeRom: rom,
                bootMode: MachineBootMode.Cold), parameter: "bootRom") ??
            RefusesArgument(action: () => new GamingBrickEngine().Create(options: "dmg cold fast", contentBytes: rom), parameter: "options") ??
            RefusesArgument(action: () => new GamingBrickEngine().Create(options: "dmg fast bios=", contentBytes: rom), parameter: "options");
    }

    private static string? RefusesArgument(Action action, string parameter) {
        try {
            action();
            return $"invalid {parameter} was accepted";
        } catch (ArgumentException exception) when (exception.ParamName == parameter) {
            return null;
        }
    }

    private static byte[] CreateCartridge(ConsoleModel model) {
        var rom = BootRomProbeCartridge.Create(probe: BootRomLayout.For(model: model).Probes[0]);
        // Header-safe entry jump, followed by an observable result write and a harmless spin.
        rom[0x0100] = 0xC3;
        rom[0x0101] = 0x50;
        rom[0x0102] = 0x01;
        ReadOnlySpan<byte> program = [0x3E, ResultValue, 0xEA, 0x00, 0xC1, 0x18, 0xFE];
        program.CopyTo(destination: rom.AsSpan(start: 0x0150));
        return rom;
    }
}
