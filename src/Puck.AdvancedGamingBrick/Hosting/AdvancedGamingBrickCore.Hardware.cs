using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

public sealed partial class AdvancedGamingBrickCore : IMachineHardwareAccess {
    /// <summary>The AGB address space. Inspect is coherent and does not charge clocks; patch admits EWRAM/IWRAM.
    /// Bus accesses use native widths, non-sequential wait states, and real register/DMA side effects, including their
    /// elapsed peripheral cycles. Inspection of save devices requires a dedicated pure provider capability.</summary>
    public static IReadOnlyList<MachineMemorySpaceDescriptor> HardwareSpaces { get; } = [
        new("bus", 0, uint.MaxValue, [1, 2, 4], true,
            [MachineAccessMode.Inspect, MachineAccessMode.Patch, MachineAccessMode.Bus],
            "AGB bus; naturally aligned scalar accesses. Patch admits EWRAM/IWRAM; bus accesses charge native wait states.")
    ];

    /// <inheritdoc/>
    public IReadOnlyList<MachineMemorySpaceDescriptor> MemorySpaces => HardwareSpaces;

    /// <inheritdoc/>
    public MachineAccessResult Read(MachineMemoryAddress address, MachineAccessMode mode = MachineAccessMode.Inspect) {
        if (ValidateHardwareAccess(address, mode, writing: false) is { } refusal) {
            return refusal;
        }
        var bus = (AgbBus)m_instance.GetRequiredService<IAgbBus>();
        var location = (uint)address.Address;
        uint value;
        if (mode == MachineAccessMode.Inspect) {
            value = address.Width switch {
                1 when location >> 24 == 4 => (uint)(bus.DebugRead16(location & ~1U) >> ((int)(location & 1) * 8)) & 0xFFU,
                1 => bus.DebugRead8(location),
                2 => bus.DebugRead16(location),
                _ => bus.DebugRead32(location),
            };
        } else {
            value = address.Width switch {
                1 => bus.Read8(location, BusAccessType.NonSequential),
                2 => bus.Read16(location, BusAccessType.NonSequential),
                _ => bus.Read32(location, BusAccessType.NonSequential),
            };
            bus.ProcessEvents();
        }
        return new(MachineAccessStatus.Available, value);
    }

    /// <inheritdoc/>
    public MachineAccessResult Write(MachineMemoryAddress address, ulong value, MachineAccessMode mode) {
        if (ValidateHardwareAccess(address, mode, writing: true) is { } refusal) {
            return refusal;
        }
        if (value >> (address.Width * 8) != 0) {
            return new(MachineAccessStatus.Refused, Reason: $"Value {value} does not fit {address.Width} byte(s).");
        }
        var bus = (AgbBus)m_instance.GetRequiredService<IAgbBus>();
        var location = (uint)address.Address;
        if (mode == MachineAccessMode.Patch) {
            for (var offset = 0; offset < address.Width; offset++) {
                bus.DebugWrite8(location + (uint)offset, (byte)(value >> (offset * 8)));
            }
        } else {
            switch (address.Width) {
                case 1: bus.Write8(location, (byte)value, BusAccessType.NonSequential); break;
                case 2: bus.Write16(location, (ushort)value, BusAccessType.NonSequential); break;
                case 4: bus.Write32(location, (uint)value, BusAccessType.NonSequential); break;
            }
            bus.ProcessEvents();
        }
        return new(MachineAccessStatus.Available);
    }

    private static MachineAccessResult? ValidateHardwareAccess(MachineMemoryAddress address, MachineAccessMode mode, bool writing) {
        if (address.Space != "bus" || address.Width is not (1 or 2 or 4)) {
            return new(MachineAccessStatus.Unsupported, Reason: "AGB hardware access requires space 'bus' and width 1, 2, or 4.");
        }
        if (address.Address > uint.MaxValue || (ulong)(address.Width - 1) > uint.MaxValue - address.Address ||
            address.Address % (ulong)address.Width != 0) {
            return new(MachineAccessStatus.Refused, Reason: $"Address 0x{address.Address:X} with width {address.Width} is outside space 'bus' or is unaligned.");
        }
        if (writing ? mode is not (MachineAccessMode.Patch or MachineAccessMode.Bus)
            : mode is not (MachineAccessMode.Inspect or MachineAccessMode.Bus)) {
            return new(MachineAccessStatus.Unsupported, Reason: $"Access mode '{mode}' does not support this operation.");
        }
        var region = address.Address >> 24;
        if (mode == MachineAccessMode.Patch && region is not (2 or 3)) {
            return new(MachineAccessStatus.Refused, Reason: $"Address 0x{address.Address:X8} is not patchable EWRAM/IWRAM; device registers require bus access.");
        }
        if (mode == MachineAccessMode.Inspect && region is 0xE or 0xF) {
            return new(MachineAccessStatus.Unsupported, Reason: "Save-device inspection is not exposed as a side-effect-free observation.");
        }
        return null;
    }
}
