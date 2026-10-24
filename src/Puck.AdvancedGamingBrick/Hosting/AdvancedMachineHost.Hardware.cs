using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

public sealed partial class AdvancedMachineHost : IMachineHardwareAccess, IMachineMemoryPeek {
    /// <inheritdoc/>
    public byte PeekByte(int address) => Worker.PeekByte(address);
    /// <inheritdoc/>
    public void PeekBytes(int address, Span<byte> destination) => Worker.PeekBytes(address, destination);
    /// <inheritdoc/>
    public void PokeByte(int address, byte value) => Worker.PokeByte(address, value);
    /// <inheritdoc/>
    public IReadOnlyList<MachineMemorySpaceDescriptor> MemorySpaces => AdvancedGamingBrickCore.HardwareSpaces;

    /// <inheritdoc/>
    public MachineAccessResult Read(MachineMemoryAddress address, MachineAccessMode mode = MachineAccessMode.Inspect) =>
        Worker.AccessHardware(
            address,
            mode
        );
    /// <inheritdoc/>
    public MachineAccessResult Write(MachineMemoryAddress address, ulong value, MachineAccessMode mode) =>
        Worker.AccessHardware(
            address: address,
            mode: mode,
            value: value
        );
}
