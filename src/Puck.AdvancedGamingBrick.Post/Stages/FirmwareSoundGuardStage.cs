namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Native sound-driver reentry and VSync guard transitions, with optional verified-retail black-box controls.</summary>
/// <remarks>Only caller-owned RAM, sound/DMA registers and original callback instructions are observed.
/// No firmware instruction bytes, internal callback addresses or timing parity are compared.</remarks>
internal sealed class FirmwareSoundGuardStage : IPostStage<PostContext> {
    private const uint Area = 0x02001000;
    private const uint Magic = 0x68736D53;
    private const uint ModeValue = 0x24A3;
    private const int PcmSize = (1584 * 2);

    /// <inheritdoc/>
    public bool IsConcurrent => true;
    /// <inheritdoc/>
    public string Name => "firmware-sound-guards";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    private static void CheckCallback(bool thumb, bool nested, byte[]? bios) {
        using var core = Initialize(
            bios: bios,
            thumb: thumb
        );
        var bus = core.Instance.Machine.Bus;

        FirmwareSoundGuardCallback.Install(
            bus: bus,
            area: Area,
            nested: nested
        );
        Write(
            address: (Area + 0x20),
            bus: bus,
            value: FirmwareSoundGuardCallback.Entry
        );
        Write(
            address: (Area + 0x24),
            bus: bus,
            value: 0
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1C,
            thumb: thumb
        );
        var count = Read(
            address: FirmwareSoundGuardCallback.Record,
            bus: bus
        );
        var during = Read(
            address: (FirmwareSoundGuardCallback.Record + 4),
            bus: bus
        );
        var afterNested = Read(
            address: (FirmwareSoundGuardCallback.Record + 8),
            bus: bus
        );
        var after = Read(
            address: Area,
            bus: bus
        );

        Require(
            condition: ((count == 1) && (during == (Magic + 1)) && (afterNested == (Magic + 1)) && (after == Magic)),
            detail: $"callbacks={count}, during={during:X8}, after nested={afterNested:X8}, returned={after:X8}; expected one callback, busy/busy/ready"
        );
    }
    private static void CheckChannelClear(bool thumb, byte[]? bios) {
        using var core = Initialize(
            bios: bios,
            thumb: thumb
        );
        var bus = core.Instance.Machine.Bus;
        const uint Psg = 0x02005000;

        FirmwareSoundGuardCallback.InstallClearRecorder(bus: bus);
        Write(
            address: (Area + 0x1C),
            bus: bus,
            value: Psg
        );
        Write(
            address: (Area + 0x2C),
            bus: bus,
            value: FirmwareSoundGuardCallback.ClearEntry
        );
        SeedPcm(bus: bus);
        for (var index = 0u; (index < 12); ++index) {
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: ((Area + 0x50) + (index * 64)),
                value: 0x80
            );
        }
        for (var index = 0u; (index < 4); ++index) {
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: (Psg + (index * 64)),
                value: 0x80
            );
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: ((Psg + (index * 64)) + 1),
                value: ((byte)(index + 1))
            );
        }
        var observedGuards = new HashSet<uint>();

        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1E,
            thumb: thumb,
            afterStep: machine => observedGuards.Add(item: Read(
                bus: machine.Bus,
                address: Area
            ))
        );
        var pcmStatuses = new byte[12];
        var psgStatuses = new byte[4];

        for (var index = 0; (index < pcmStatuses.Length); ++index) {
            pcmStatuses[index] = bus.Read8(
                access: BusAccessType.NonSequential,
                address: ((Area + 0x50) + (((uint)index) * 64))
            );
        }
        for (var index = 0; (index < psgStatuses.Length); ++index) {
            psgStatuses[index] = bus.Read8(
                access: BusAccessType.NonSequential,
                address: (Psg + (((uint)index) * 64))
            );
        }
        var callbacks = Read(
            address: FirmwareSoundGuardCallback.ClearRecord,
            bus: bus
        );
        var pcm = ReadPcm(bus: bus);
        // The installed oscillator-off callback owns any PSG record mutation;
        // this original recorder deliberately leaves those records intact.
        Require(
            condition: ((pcmStatuses.AsSpan().IndexOfAnyExcept(value: ((byte)0)) < 0) && (psgStatuses.AsSpan().IndexOfAnyExcept(value: ((byte)0x80)) < 0)
            && (callbacks == 4) && (Read(
                address: Area,
                bus: bus
            ) == Magic) && (pcm.AsSpan().IndexOfAnyExcept(value: ((byte)0x6D)) < 0)
            && observedGuards.SetEquals(other: [Magic, (Magic + 1)])),
            detail: $"PCM statuses={Convert.ToHexString(inArray: pcmStatuses)}, PSG statuses={Convert.ToHexString(inArray: psgStatuses)}, off callbacks={callbacks}, PCM first={pcm[0]:X2}, observed guards={GuardValues(values: observedGuards)}; expected cleared PCM voices, four callbacks, temporary busy guard, preserved PSG records and queued PCM"
        );
        for (var index = 0u; (index < 4); ++index) {
            Require(
                condition: (Read(
                    address: ((FirmwareSoundGuardCallback.ClearRecord + 4) + (index * 4)),
                    bus: bus
                ) == (index + 1)),
                detail: "oscillator-off callback channel order mismatch"
            );
        }
    }
    private static void CheckMainGuard(bool thumb, uint offset, byte[]? bios) {
        using var core = Initialize(
            bios: bios,
            thumb: thumb
        );
        var bus = core.Instance.Machine.Bus;

        FirmwareSoundGuardCallback.Install(
            bus: bus,
            area: Area,
            nested: true
        );
        Write(
            address: (Area + 0x20),
            bus: bus,
            value: FirmwareSoundGuardCallback.Entry
        );
        SeedPcm(bus: bus);
        Write(
            address: Area,
            bus: bus,
            value: unchecked((Magic + offset))
        );
        var before = ReadArea(bus: bus);

        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1C,
            thumb: thumb
        );
        Require(
            condition: before.AsSpan().SequenceEqual(other: ReadArea(bus: bus)),
            detail: "guarded Main changed caller-owned sound area"
        );
        Require(
            condition: (Read(
                address: FirmwareSoundGuardCallback.Record,
                bus: bus
            ) == 0),
            detail: "guarded Main invoked callback"
        );
    }
    private static void CheckModeDuringMain(bool thumb, byte[]? bios) {
        using var core = Initialize(
            bios: bios,
            thumb: thumb
        );
        var bus = core.Instance.Machine.Bus;

        FirmwareSoundGuardCallback.Install(
            area: Area,
            bus: bus,
            nested: true,
            nestedInput: ModeValue,
            nestedNumber: 0x1B
        );
        Write(
            address: (Area + 0x20),
            bus: bus,
            value: FirmwareSoundGuardCallback.Entry
        );
        Write(
            address: (Area + 0x24),
            bus: bus,
            value: 0
        );
        var before = ReadBytes(
            address: (Area + 5),
            bus: bus,
            length: 3
        );

        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1C,
            thumb: thumb
        );
        var count = Read(
            address: FirmwareSoundGuardCallback.Record,
            bus: bus
        );
        var during = Read(
            address: (FirmwareSoundGuardCallback.Record + 4),
            bus: bus
        );
        var afterNested = Read(
            address: (FirmwareSoundGuardCallback.Record + 8),
            bus: bus
        );

        Require(
            condition: ((count == 1) && (during == (Magic + 1)) && (afterNested == (Magic + 1)) && (Read(
                address: Area,
                bus: bus
            ) == Magic)
            && before.AsSpan().SequenceEqual(other: ReadBytes(
                address: (Area + 5),
                bus: bus,
                length: 3
            ))),
            detail: $"callbacks={count}, during={during:X8}, after nested={afterNested:X8}, mode={Convert.ToHexString(inArray: ReadBytes(
                address: (Area + 5),
                bus: bus,
                length: 3
            ))}; expected busy callback, refused Mode and restored ready guard"
        );
    }
    private static void CheckModeGuard(bool thumb, uint offset, byte[]? bios) {
        using var core = Initialize(
            bios: bios,
            thumb: thumb
        );
        var bus = core.Instance.Machine.Bus;

        SeedPcm(bus: bus);
        var guard = unchecked((Magic + offset));

        Write(
            address: Area,
            bus: bus,
            value: guard
        );
        var expected = ReadArea(bus: bus);

        if (offset == 0) {
            expected[5] = 35;
            expected[6] = 4;
            expected[7] = 2;
        }
        var observedGuards = new HashSet<uint>();

        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1B,
            thumb: thumb,
            r0: ModeValue,
            afterStep: machine => observedGuards.Add(item: Read(
                bus: machine.Bus,
                address: Area
            ))
        );
        var actual = ReadArea(bus: bus);
        var guardMatches = ((offset == 0)
            ? observedGuards.SetEquals(other: [guard, (guard + 1)])
            : observedGuards.SetEquals(other: [guard])
        );

        Require(
            condition: (actual.AsSpan().SequenceEqual(other: expected) && guardMatches),
            detail: $"mode reverb/channels/volume={actual[5]}/{actual[6]}/{actual[7]}, guard={Read(
                address: Area,
                bus: bus
            ):X8}, observed guards={GuardValues(values: observedGuards)}; expected {expected[5]}/{expected[6]}/{expected[7]}, unchanged other RAM, temporary increment={(offset == 0)}"
        );
    }
    private static void CheckVSync(bool thumb, byte number, uint offset, byte[]? bios) {
        using var core = Initialize(
            bios: bios,
            thumb: thumb
        );
        var bus = core.Instance.Machine.Bus;
        var off = (number == 0x28);

        SeedPcm(bus: bus);
        Write(
            address: Area,
            bus: bus,
            value: unchecked((Magic + offset))
        );
        bus.Write8(
            access: BusAccessType.NonSequential,
            address: (Area + 4),
            value: 3
        );
        bus.Write16(
            access: BusAccessType.NonSequential,
            address: 0x040000C6,
            value: (off
            ? (ushort)0xB600
            : (ushort)0)
        );
        bus.Write16(
            access: BusAccessType.NonSequential,
            address: 0x040000D2,
            value: (off
            ? (ushort)0xB600
            : (ushort)0)
        );
        var observedGuards = new HashSet<uint>();

        FirmwareSwiProbe.Call(
            core: core,
            number: number,
            thumb: thumb,
            afterStep: machine => observedGuards.Add(item: Read(
                bus: machine.Bus,
                address: Area
            ))
        );
        var accepted = (off && (offset <= 1));
        // Both controls retain the original identity in the retail black-box
        // observations. On restarts DMA without a count or PCM transition.
        var expectedGuard = unchecked((Magic + offset));
        var actualMagic = Read(
            address: Area,
            bus: bus
        );
        var count = bus.Read8(
            access: BusAccessType.NonSequential,
            address: (Area + 4)
        );
        var dma1 = ((bus.Read16(
            access: BusAccessType.NonSequential,
            address: 0x040000C6
        ) & 0x8000) != 0);
        var dma2 = ((bus.Read16(
            access: BusAccessType.NonSequential,
            address: 0x040000D2
        ) & 0x8000) != 0);
        var pcm = ReadPcm(bus: bus);
        var expectedPcm = (accepted
            ? (byte)0
            : (byte)0x6D
        );
        var expectedDma = !accepted;
        var guardMatches = (accepted
            ? observedGuards.SetEquals(other: [expectedGuard, (expectedGuard + 1)])
            : observedGuards.SetEquals(other: [expectedGuard])
        );

        Require(
            condition: ((actualMagic == expectedGuard) && (count == (accepted
            ? 0
            : 3))
            && (dma1 == expectedDma) && (dma2 == expectedDma) && (pcm.AsSpan().IndexOfAnyExcept(value: expectedPcm) < 0)
            && guardMatches),
            detail: $"guard={actualMagic:X8}, count={count}, DMA={dma1}/{dma2}, PCM first={pcm[0]:X2}, uniform={(pcm.AsSpan().IndexOfAnyExcept(value: expectedPcm) < 0)}, observed guards={GuardValues(values: observedGuards)}; expected guard={expectedGuard:X8}, count={(accepted
            ? 0
            : 3)}, DMA={expectedDma}, PCM={expectedPcm:X2}, temporary increment={accepted}"
        );
    }
    private static void CheckVSyncTick(bool thumb, uint offset, byte count, byte[]? bios) {
        using var core = Initialize(
            bios: bios,
            thumb: thumb
        );
        var bus = core.Instance.Machine.Bus;

        SeedPcm(bus: bus);
        Write(
            address: Area,
            bus: bus,
            value: (Magic + offset)
        );
        bus.Write8(
            access: BusAccessType.NonSequential,
            address: (Area + 4),
            value: count
        );
        bus.Write8(
            access: BusAccessType.NonSequential,
            address: (Area + 0xB),
            value: 7
        );
        FirmwareSwiProbe.Call(
            core: core,
            number: 0x1D,
            thumb: thumb
        );
        var expectedCount = ((offset != 0)
            ? count
            : ((count == 1)
                ? 7
                : (count - 1)
        ));
        var actualCount = bus.Read8(
            access: BusAccessType.NonSequential,
            address: (Area + 4)
        );
        var restarted = ((offset == 0) && (count == 1));
        var dma1 = ((bus.Read16(
            access: BusAccessType.NonSequential,
            address: 0x040000C6
        ) & 0x8000) != 0);
        var dma2 = ((bus.Read16(
            access: BusAccessType.NonSequential,
            address: 0x040000D2
        ) & 0x8000) != 0);

        Require(
            condition: ((Read(
                address: Area,
                bus: bus
            ) == (Magic + offset)) && (actualCount == expectedCount) && (dma1 == restarted) && (dma2 == restarted)
            && (ReadPcm(bus: bus).AsSpan().IndexOfAnyExcept(value: ((byte)0x6D)) < 0)),
            detail: $"count={actualCount}, DMA={dma1}/{dma2}; expected count={expectedCount}, DMA={restarted}, unchanged guard and PCM"
        );
    }
    private static string GuardValues(HashSet<uint> values) => string.Join(
        separator: ",",
        values: values.Select(selector: value => value.ToString(format: "X8"))
    );
    private static AdvancedGamingBrickCore Initialize(bool thumb, byte[]? bios) {
        var core = FirmwareSwiProbe.Run(
            number: 0x1A,
            thumb: thumb,
            r0: Area,
            bios: bios
        );
        // Keep all peripheral time observers quiescent. This stage examines
        // guard decisions, not incidental sample-DMA or timer progression.
        core.Instance.Machine.Bus.Write16(
            access: BusAccessType.NonSequential,
            address: 0x04000102,
            value: 0
        );
        core.Instance.Machine.Bus.Write16(
            access: BusAccessType.NonSequential,
            address: 0x040000C6,
            value: 0
        );
        core.Instance.Machine.Bus.Write16(
            access: BusAccessType.NonSequential,
            address: 0x040000D2,
            value: 0
        );
        return core;
    }
    private static uint Read(IAgbBus bus, uint address) => bus.Read32(
        access: BusAccessType.NonSequential,
        address: address
    );
    private static byte[] ReadArea(IAgbBus bus) => ReadBytes(
        address: Area,
        bus: bus,
        length: (0x350 + PcmSize)
    );
    private static byte[] ReadBytes(IAgbBus bus, uint address, int length) {
        var bytes = new byte[length];

        for (var offset = 0; (offset < length); ++offset) {
            bytes[offset] = bus.Read8(
                access: BusAccessType.NonSequential,
                address: (address + ((uint)offset))
            );
        }
        return bytes;
    }
    private static byte[] ReadPcm(IAgbBus bus) => ReadBytes(
        address: (Area + 0x350),
        bus: bus,
        length: PcmSize
    );
    private static void Require(bool condition, string detail) => FirmwareSwiProbe.Require(
        condition: condition,
        detail: detail
    );
    private static void SeedPcm(IAgbBus bus) {
        for (var offset = 0; (offset < PcmSize); ++offset) {
            bus.Write8(
                access: BusAccessType.NonSequential,
                address: ((Area + 0x350) + ((uint)offset)),
                value: 0x6D
            );
        }
    }
    private static void Write(IAgbBus bus, uint address, uint value) => bus.Write32(
        access: BusAccessType.NonSequential,
        address: address,
        value: value
    );

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var failures = new List<string>();
        var cases = 0;

        RunImage(
            bios: null,
            name: "Puck"
        );
        var retail = (AgbBiosProfile.Identify(image: context.BiosImage.Span).Kind == AgbBiosKind.RealVerified);

        if (retail) {
            RunImage(
                name: "retail",
                bios: context.BiosImage.ToArray()
            );
        }
        return ((failures.Count == 0)
            ? PostStageOutcome.Pass(detail: $"66 bundled ARM/Thumb cases: native busy callback and bounded nested Main/Mode refusal; guarded Main/Mode RAM preservation; VSyncOff/On and busy VSync count/PCM/DMA decisions; PCM voice clear, queued-PCM preservation and installed PSG oscillator-off callbacks. {(retail
                ? "66 verified-retail controls also passed."
                : "Verified-retail controls not run: no verified external BIOS.")} Functional state contracts, not sound timing parity.")
            : PostStageOutcome.Fail(detail: $"{failures.Count}/{cases} cases failed: {string.Join(
                separator: "; ",
                values: failures
            )}")
        );

        void RunImage(string name, byte[]? bios) {
            foreach (var thumb in new[] { false, true }) {
                foreach (var nested in new[] { false, true }) {
                    Check(
                        name: $"{name} Main Thumb={thumb} nested={nested}",
                        test: () => CheckCallback(
                            bios: bios,
                            nested: nested,
                            thumb: thumb
                        )
                    );
                }
                foreach (var offset in new uint[] { 1, 10, 11, uint.MaxValue }) {
                    Check(
                        name: $"{name} Main guard Thumb={thumb} offset={offset}",
                        test: () => CheckMainGuard(
                            bios: bios,
                            offset: offset,
                            thumb: thumb
                        )
                    );
                }
                foreach (var offset in new uint[] { uint.MaxValue, 0, 1, 10, 11 }) {
                    Check(
                        name: $"{name} Mode guard Thumb={thumb} offset={offset}",
                        test: () => CheckModeGuard(
                            bios: bios,
                            offset: offset,
                            thumb: thumb
                        )
                    );
                }
                Check(
                    name: $"{name} Mode during Main Thumb={thumb}",
                    test: () => CheckModeDuringMain(
                        bios: bios,
                        thumb: thumb
                    )
                );
                foreach (var number in new byte[] { 0x28, 0x29 }) {
                    foreach (var offset in new uint[] { uint.MaxValue, 0, 1, 9, 10, 11, 12 }) {
                        Check(
                            name: $"{name} SWI={number:X2} Thumb={thumb} offset={offset}",
                            test: () => CheckVSync(
                                bios: bios,
                                number: number,
                                offset: offset,
                                thumb: thumb
                            )
                        );
                    }
                }
                foreach (var offset in new uint[] { 0, 1, 10 }) {
                    foreach (var count in new byte[] { 1, 3 }) {
                        Check(
                            name: $"{name} VSync Thumb={thumb} offset={offset} count={count}",
                            test: () => CheckVSyncTick(
                                bios: bios,
                                count: count,
                                offset: offset,
                                thumb: thumb
                            )
                        );
                    }
                }
                Check(
                    name: $"{name} ChannelClear Thumb={thumb}",
                    test: () => CheckChannelClear(
                        bios: bios,
                        thumb: thumb
                    )
                );
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
}
