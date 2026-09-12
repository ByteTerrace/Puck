namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Native sound-driver reentry and VSync guard transitions, with optional verified-retail black-box controls.</summary>
/// <remarks>Only caller-owned RAM, sound/DMA registers and original callback instructions are observed.
/// No firmware instruction bytes, internal callback addresses or timing parity are compared.</remarks>
internal sealed class FirmwareSoundGuardStage : IPostStage<PostContext> {
    private const uint Area = 0x02001000;
    private const uint Magic = 0x68736D53;
    private const int PcmSize = 1584 * 2;
    private const uint ModeValue = 0x24A3;

    /// <inheritdoc/>
    public string Name => "firmware-sound-guards";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;
    /// <inheritdoc/>
    public bool IsConcurrent => true;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var failures = new List<string>();
        var cases = 0;
        RunImage(name: "Puck", bios: null);
        var retail = AgbBiosProfile.Identify(image: context.BiosImage.Span).Kind == AgbBiosKind.RealVerified;
        if (retail) {
            RunImage(name: "retail", bios: context.BiosImage.ToArray());
        }
        return failures.Count == 0
            ? PostStageOutcome.Pass(detail: $"66 bundled ARM/Thumb cases: native busy callback and bounded nested Main/Mode refusal; guarded Main/Mode RAM preservation; VSyncOff/On and busy VSync count/PCM/DMA decisions; PCM voice clear, queued-PCM preservation and installed PSG oscillator-off callbacks. {(retail ? "66 verified-retail controls also passed." : "Verified-retail controls not run: no verified external BIOS.")} Functional state contracts, not sound timing parity.")
            : PostStageOutcome.Fail(detail: $"{failures.Count}/{cases} cases failed: {string.Join(separator: "; ", values: failures)}");

        void RunImage(string name, byte[]? bios) {
            foreach (var thumb in new[] { false, true }) {
                foreach (var nested in new[] { false, true }) {
                    Check(name: $"{name} Main Thumb={thumb} nested={nested}", test: () => CheckCallback(thumb: thumb, nested: nested, bios: bios));
                }
                foreach (var offset in new uint[] { 1, 10, 11, uint.MaxValue }) {
                    Check(name: $"{name} Main guard Thumb={thumb} offset={offset}", test: () => CheckMainGuard(thumb: thumb, offset: offset, bios: bios));
                }
                foreach (var offset in new uint[] { uint.MaxValue, 0, 1, 10, 11 }) {
                    Check(name: $"{name} Mode guard Thumb={thumb} offset={offset}", test: () => CheckModeGuard(thumb: thumb, offset: offset, bios: bios));
                }
                Check(name: $"{name} Mode during Main Thumb={thumb}", test: () => CheckModeDuringMain(thumb: thumb, bios: bios));
                foreach (var number in new byte[] { 0x28, 0x29 }) {
                    foreach (var offset in new uint[] { uint.MaxValue, 0, 1, 9, 10, 11, 12 }) {
                        Check(name: $"{name} SWI={number:X2} Thumb={thumb} offset={offset}", test: () => CheckVSync(thumb: thumb, number: number, offset: offset, bios: bios));
                    }
                }
                foreach (var offset in new uint[] { 0, 1, 10 }) {
                    foreach (var count in new byte[] { 1, 3 }) {
                        Check(name: $"{name} VSync Thumb={thumb} offset={offset} count={count}", test: () => CheckVSyncTick(thumb: thumb, offset: offset, count: count, bios: bios));
                    }
                }
                Check(name: $"{name} ChannelClear Thumb={thumb}", test: () => CheckChannelClear(thumb: thumb, bios: bios));
            }
        }
        void Check(string name, Action test) {
            ++cases;
            try {
                test();
            } catch (InvalidOperationException exception) {
                failures.Add(item: $"{name}: {exception.Message}");
            }
        }
    }

    private static void CheckCallback(bool thumb, bool nested, byte[]? bios) {
        using var core = Initialize(thumb: thumb, bios: bios);
        var bus = core.Instance.Machine.Bus;
        FirmwareSoundGuardCallback.Install(bus: bus, area: Area, nested: nested);
        Write(bus: bus, address: Area + 0x20, value: FirmwareSoundGuardCallback.Entry);
        Write(bus: bus, address: Area + 0x24, value: 0);
        FirmwareSwiProbe.Call(core: core, number: 0x1C, thumb: thumb);
        var count = Read(bus: bus, address: FirmwareSoundGuardCallback.Record);
        var during = Read(bus: bus, address: FirmwareSoundGuardCallback.Record + 4);
        var afterNested = Read(bus: bus, address: FirmwareSoundGuardCallback.Record + 8);
        var after = Read(bus: bus, address: Area);
        Require(condition: count == 1 && during == Magic + 1 && afterNested == Magic + 1 && after == Magic,
            detail: $"callbacks={count}, during={during:X8}, after nested={afterNested:X8}, returned={after:X8}; expected one callback, busy/busy/ready");
    }

    private static void CheckMainGuard(bool thumb, uint offset, byte[]? bios) {
        using var core = Initialize(thumb: thumb, bios: bios);
        var bus = core.Instance.Machine.Bus;
        FirmwareSoundGuardCallback.Install(bus: bus, area: Area, nested: true);
        Write(bus: bus, address: Area + 0x20, value: FirmwareSoundGuardCallback.Entry);
        SeedPcm(bus: bus);
        Write(bus: bus, address: Area, value: unchecked(Magic + offset));
        var before = ReadArea(bus: bus);
        FirmwareSwiProbe.Call(core: core, number: 0x1C, thumb: thumb);
        Require(condition: before.AsSpan().SequenceEqual(other: ReadArea(bus: bus)), detail: "guarded Main changed caller-owned sound area");
        Require(condition: Read(bus: bus, address: FirmwareSoundGuardCallback.Record) == 0, detail: "guarded Main invoked callback");
    }

    private static void CheckVSync(bool thumb, byte number, uint offset, byte[]? bios) {
        using var core = Initialize(thumb: thumb, bios: bios);
        var bus = core.Instance.Machine.Bus;
        var off = number == 0x28;
        SeedPcm(bus: bus);
        Write(bus: bus, address: Area, value: unchecked(Magic + offset));
        bus.Write8(address: Area + 4, value: 3, access: BusAccessType.NonSequential);
        bus.Write16(address: 0x040000C6, value: off ? (ushort)0xB600 : (ushort)0, access: BusAccessType.NonSequential);
        bus.Write16(address: 0x040000D2, value: off ? (ushort)0xB600 : (ushort)0, access: BusAccessType.NonSequential);
        var observedGuards = new HashSet<uint>();
        FirmwareSwiProbe.Call(core: core, number: number, thumb: thumb,
            afterStep: machine => observedGuards.Add(item: Read(bus: machine.Bus, address: Area)));
        var accepted = off && offset <= 1;
        // Both controls retain the original identity in the retail black-box
        // observations. On restarts DMA without a count or PCM transition.
        var expectedGuard = unchecked(Magic + offset);
        var actualMagic = Read(bus: bus, address: Area);
        var count = bus.Read8(address: Area + 4, access: BusAccessType.NonSequential);
        var dma1 = (bus.Read16(address: 0x040000C6, access: BusAccessType.NonSequential) & 0x8000) != 0;
        var dma2 = (bus.Read16(address: 0x040000D2, access: BusAccessType.NonSequential) & 0x8000) != 0;
        var pcm = ReadPcm(bus: bus);
        var expectedPcm = accepted ? (byte)0 : (byte)0x6D;
        var expectedDma = !accepted;
        var guardMatches = accepted ? observedGuards.SetEquals(other: [expectedGuard, expectedGuard + 1])
            : observedGuards.SetEquals(other: [expectedGuard]);
        Require(condition: actualMagic == expectedGuard && count == (accepted ? 0 : 3)
            && dma1 == expectedDma && dma2 == expectedDma && pcm.AsSpan().IndexOfAnyExcept(value: expectedPcm) < 0
            && guardMatches,
            detail: $"guard={actualMagic:X8}, count={count}, DMA={dma1}/{dma2}, PCM first={pcm[0]:X2}, uniform={pcm.AsSpan().IndexOfAnyExcept(value: expectedPcm) < 0}, observed guards={GuardValues(values: observedGuards)}; expected guard={expectedGuard:X8}, count={(accepted ? 0 : 3)}, DMA={expectedDma}, PCM={expectedPcm:X2}, temporary increment={accepted}");
    }

    private static void CheckModeGuard(bool thumb, uint offset, byte[]? bios) {
        using var core = Initialize(thumb: thumb, bios: bios);
        var bus = core.Instance.Machine.Bus;
        SeedPcm(bus: bus);
        var guard = unchecked(Magic + offset);
        Write(bus: bus, address: Area, value: guard);
        var expected = ReadArea(bus: bus);
        if (offset == 0) {
            expected[5] = 35;
            expected[6] = 4;
            expected[7] = 2;
        }
        var observedGuards = new HashSet<uint>();
        FirmwareSwiProbe.Call(core: core, number: 0x1B, thumb: thumb, r0: ModeValue,
            afterStep: machine => observedGuards.Add(item: Read(bus: machine.Bus, address: Area)));
        var actual = ReadArea(bus: bus);
        var guardMatches = offset == 0 ? observedGuards.SetEquals(other: [guard, guard + 1]) : observedGuards.SetEquals(other: [guard]);
        Require(condition: actual.AsSpan().SequenceEqual(other: expected) && guardMatches,
            detail: $"mode reverb/channels/volume={actual[5]}/{actual[6]}/{actual[7]}, guard={Read(bus: bus, address: Area):X8}, observed guards={GuardValues(values: observedGuards)}; expected {expected[5]}/{expected[6]}/{expected[7]}, unchanged other RAM, temporary increment={offset == 0}");
    }

    private static void CheckModeDuringMain(bool thumb, byte[]? bios) {
        using var core = Initialize(thumb: thumb, bios: bios);
        var bus = core.Instance.Machine.Bus;
        FirmwareSoundGuardCallback.Install(bus: bus, area: Area, nested: true, nestedNumber: 0x1B, nestedInput: ModeValue);
        Write(bus: bus, address: Area + 0x20, value: FirmwareSoundGuardCallback.Entry);
        Write(bus: bus, address: Area + 0x24, value: 0);
        var before = ReadBytes(bus: bus, address: Area + 5, length: 3);
        FirmwareSwiProbe.Call(core: core, number: 0x1C, thumb: thumb);
        var count = Read(bus: bus, address: FirmwareSoundGuardCallback.Record);
        var during = Read(bus: bus, address: FirmwareSoundGuardCallback.Record + 4);
        var afterNested = Read(bus: bus, address: FirmwareSoundGuardCallback.Record + 8);
        Require(condition: count == 1 && during == Magic + 1 && afterNested == Magic + 1 && Read(bus: bus, address: Area) == Magic
            && before.AsSpan().SequenceEqual(other: ReadBytes(bus: bus, address: Area + 5, length: 3)),
            detail: $"callbacks={count}, during={during:X8}, after nested={afterNested:X8}, mode={Convert.ToHexString(inArray: ReadBytes(bus: bus, address: Area + 5, length: 3))}; expected busy callback, refused Mode and restored ready guard");
    }

    private static AdvancedGamingBrickCore Initialize(bool thumb, byte[]? bios) {
        var core = FirmwareSwiProbe.Run(number: 0x1A, thumb: thumb, r0: Area, bios: bios);
        // Keep all peripheral time observers quiescent. This stage examines
        // guard decisions, not incidental sample-DMA or timer progression.
        core.Instance.Machine.Bus.Write16(address: 0x04000102, value: 0, access: BusAccessType.NonSequential);
        core.Instance.Machine.Bus.Write16(address: 0x040000C6, value: 0, access: BusAccessType.NonSequential);
        core.Instance.Machine.Bus.Write16(address: 0x040000D2, value: 0, access: BusAccessType.NonSequential);
        return core;
    }

    private static void CheckVSyncTick(bool thumb, uint offset, byte count, byte[]? bios) {
        using var core = Initialize(thumb: thumb, bios: bios);
        var bus = core.Instance.Machine.Bus;
        SeedPcm(bus: bus);
        Write(bus: bus, address: Area, value: Magic + offset);
        bus.Write8(address: Area + 4, value: count, access: BusAccessType.NonSequential);
        bus.Write8(address: Area + 0xB, value: 7, access: BusAccessType.NonSequential);
        FirmwareSwiProbe.Call(core: core, number: 0x1D, thumb: thumb);
        var expectedCount = offset != 0 ? count : count == 1 ? 7 : count - 1;
        var actualCount = bus.Read8(address: Area + 4, access: BusAccessType.NonSequential);
        var restarted = offset == 0 && count == 1;
        var dma1 = (bus.Read16(address: 0x040000C6, access: BusAccessType.NonSequential) & 0x8000) != 0;
        var dma2 = (bus.Read16(address: 0x040000D2, access: BusAccessType.NonSequential) & 0x8000) != 0;
        Require(condition: Read(bus: bus, address: Area) == Magic + offset && actualCount == expectedCount && dma1 == restarted && dma2 == restarted
            && ReadPcm(bus: bus).AsSpan().IndexOfAnyExcept(value: (byte)0x6D) < 0,
            detail: $"count={actualCount}, DMA={dma1}/{dma2}; expected count={expectedCount}, DMA={restarted}, unchanged guard and PCM");
    }

    private static void CheckChannelClear(bool thumb, byte[]? bios) {
        using var core = Initialize(thumb: thumb, bios: bios);
        var bus = core.Instance.Machine.Bus;
        const uint Psg = 0x02005000;
        FirmwareSoundGuardCallback.InstallClearRecorder(bus: bus);
        Write(bus: bus, address: Area + 0x1C, value: Psg);
        Write(bus: bus, address: Area + 0x2C, value: FirmwareSoundGuardCallback.ClearEntry);
        SeedPcm(bus: bus);
        for (var index = 0u; index < 12; ++index) {
            bus.Write8(address: Area + 0x50 + index * 64, value: 0x80, access: BusAccessType.NonSequential);
        }
        for (var index = 0u; index < 4; ++index) {
            bus.Write8(address: Psg + index * 64, value: 0x80, access: BusAccessType.NonSequential);
            bus.Write8(address: Psg + index * 64 + 1, value: (byte)(index + 1), access: BusAccessType.NonSequential);
        }
        var observedGuards = new HashSet<uint>();
        FirmwareSwiProbe.Call(core: core, number: 0x1E, thumb: thumb,
            afterStep: machine => observedGuards.Add(item: Read(bus: machine.Bus, address: Area)));
        var pcmStatuses = new byte[12];
        var psgStatuses = new byte[4];
        for (var index = 0; index < pcmStatuses.Length; ++index) {
            pcmStatuses[index] = bus.Read8(address: Area + 0x50 + (uint)index * 64, access: BusAccessType.NonSequential);
        }
        for (var index = 0; index < psgStatuses.Length; ++index) {
            psgStatuses[index] = bus.Read8(address: Psg + (uint)index * 64, access: BusAccessType.NonSequential);
        }
        var callbacks = Read(bus: bus, address: FirmwareSoundGuardCallback.ClearRecord);
        var pcm = ReadPcm(bus: bus);
        // The installed oscillator-off callback owns any PSG record mutation;
        // this original recorder deliberately leaves those records intact.
        Require(condition: pcmStatuses.AsSpan().IndexOfAnyExcept(value: (byte)0) < 0 && psgStatuses.AsSpan().IndexOfAnyExcept(value: (byte)0x80) < 0
            && callbacks == 4 && Read(bus: bus, address: Area) == Magic && pcm.AsSpan().IndexOfAnyExcept(value: (byte)0x6D) < 0
            && observedGuards.SetEquals(other: [Magic, Magic + 1]),
            detail: $"PCM statuses={Convert.ToHexString(inArray: pcmStatuses)}, PSG statuses={Convert.ToHexString(inArray: psgStatuses)}, off callbacks={callbacks}, PCM first={pcm[0]:X2}, observed guards={GuardValues(values: observedGuards)}; expected cleared PCM voices, four callbacks, temporary busy guard, preserved PSG records and queued PCM");
        for (var index = 0u; index < 4; ++index) {
            Require(condition: Read(bus: bus, address: FirmwareSoundGuardCallback.ClearRecord + 4 + index * 4) == index + 1,
                detail: "oscillator-off callback channel order mismatch");
        }
    }
    private static void SeedPcm(IAgbBus bus) {
        for (var offset = 0; offset < PcmSize; ++offset) {
            bus.Write8(address: Area + 0x350 + (uint)offset, value: 0x6D, access: BusAccessType.NonSequential);
        }
    }
    private static byte[] ReadArea(IAgbBus bus) => ReadBytes(bus: bus, address: Area, length: 0x350 + PcmSize);
    private static byte[] ReadPcm(IAgbBus bus) => ReadBytes(bus: bus, address: Area + 0x350, length: PcmSize);
    private static byte[] ReadBytes(IAgbBus bus, uint address, int length) {
        var bytes = new byte[length];
        for (var offset = 0; offset < length; ++offset) {
            bytes[offset] = bus.Read8(address: address + (uint)offset, access: BusAccessType.NonSequential);
        }
        return bytes;
    }
    private static uint Read(IAgbBus bus, uint address) => bus.Read32(address: address, access: BusAccessType.NonSequential);
    private static string GuardValues(HashSet<uint> values) => string.Join(separator: ",", values: values.Select(value => value.ToString(format: "X8")));
    private static void Write(IAgbBus bus, uint address, uint value) => bus.Write32(address: address, value: value, access: BusAccessType.NonSequential);
    private static void Require(bool condition, string detail) => FirmwareSwiProbe.Require(condition: condition, detail: detail);
}
