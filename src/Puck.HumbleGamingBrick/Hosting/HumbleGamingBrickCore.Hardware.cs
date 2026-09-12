using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick;

public sealed partial class HumbleGamingBrickCore : IMachineHardwareAccess {
    /// <summary>The SM83 CPU-visible bus. Inspection bypasses access locks; bus operations honor them and execute
    /// byte accesses in ascending address order without advancing the CPU clock. Patch admits actual RAM and IE,
    /// but refuses mapper registers, I/O, and cartridge windows without a pure RAM mapping.</summary>
    public static IReadOnlyList<MachineMemorySpaceDescriptor> HardwareSpaces { get; } = [
        new("bus", 0, 0xFFFF, [1, 2], true,
            [MachineAccessMode.Inspect, MachineAccessMode.Patch, MachineAccessMode.Bus],
            "SM83 bus; ordered byte bus accesses at the current clock boundary. Patch requires mapped RAM or IE.")
    ];

    /// <inheritdoc/>
    public IReadOnlyList<MachineMemorySpaceDescriptor> MemorySpaces => HardwareSpaces;

    /// <inheritdoc/>
    public MachineAccessResult Read(MachineMemoryAddress address, MachineAccessMode mode = MachineAccessMode.Inspect) {
        if (ValidateHardwareAccess(address, mode, writing: false) is { } refusal) {
            return refusal;
        }
        ulong value = 0;
        for (var offset = 0; offset < address.Width; offset++) {
            var current = (ushort)(address.Address + (ulong)offset);
            var part = mode == MachineAccessMode.Inspect ? m_systemBus.DebugReadByte(current) : m_systemBus.ReadByte(current);
            value |= (ulong)part << (offset * 8);
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
        for (var offset = 0; offset < address.Width; offset++) {
            var current = (ushort)(address.Address + (ulong)offset);
            var part = (byte)(value >> (offset * 8));
            if (mode == MachineAccessMode.Patch) {
                m_systemBus.DebugWriteByte(current, part);
            } else {
                m_systemBus.WriteByte(current, part);
            }
        }
        return new(MachineAccessStatus.Available);
    }

    private MachineAccessResult? ValidateHardwareAccess(MachineMemoryAddress address, MachineAccessMode mode, bool writing) {
        if (address.Space != "bus" || address.Width is not (1 or 2)) {
            return new(MachineAccessStatus.Unsupported, Reason: "SM83 hardware access requires space 'bus' and width 1 or 2.");
        }
        if (address.Address > 0xFFFFUL || (ulong)(address.Width - 1) > 0xFFFFUL - address.Address) {
            return new(MachineAccessStatus.Refused, Reason: $"Address 0x{address.Address:X} with width {address.Width} is outside space 'bus'.");
        }
        if (writing ? mode is not (MachineAccessMode.Patch or MachineAccessMode.Bus)
            : mode is not (MachineAccessMode.Inspect or MachineAccessMode.Bus)) {
            return new(MachineAccessStatus.Unsupported, Reason: $"Access mode '{mode}' does not support this operation.");
        }
        for (var offset = 0; offset < address.Width; offset++) {
            var current = address.Address + (ulong)offset;
            if (mode == MachineAccessMode.Patch && !(current is >= 0x8000 and <= 0xFE9F or >= 0xFF80 and <= 0xFFFF)) {
                return new(MachineAccessStatus.Refused, Reason: $"Address 0x{current:X4} is not patchable RAM or IE; hardware registers require bus access.");
            }
            if (mode != MachineAccessMode.Bus && current is >= 0xA000 and <= 0xBFFF &&
                (!m_cartridge.TryComputeRamWindow(out _, out var length) || current - 0xA000UL >= (ulong)length)) {
                return new(MachineAccessStatus.Unsupported, Reason: $"Address 0x{current:X4} has no side-effect-free cartridge RAM mapping.");
            }
        }
        return null;
    }
}
