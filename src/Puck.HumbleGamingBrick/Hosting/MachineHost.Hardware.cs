using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick;

public sealed partial class MachineHost : IMachineHardwareAccess {
    /// <inheritdoc/>
    public IReadOnlyList<MachineMemorySpaceDescriptor> MemorySpaces => HumbleGamingBrickCore.HardwareSpaces;
    /// <inheritdoc/>
    public MachineAccessResult Read(MachineMemoryAddress address, MachineAccessMode mode = MachineAccessMode.Inspect) =>
        Worker.AccessHardware(address, mode);
    /// <inheritdoc/>
    public MachineAccessResult Write(MachineMemoryAddress address, ulong value, MachineAccessMode mode) =>
        Worker.AccessHardware(address, mode, value);
}
