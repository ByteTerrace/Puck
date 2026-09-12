using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

public sealed partial class AdvancedMachineHost : IMachineHardwareAccess {
    /// <inheritdoc/>
    public IReadOnlyList<MachineMemorySpaceDescriptor> MemorySpaces => AdvancedGamingBrickCore.HardwareSpaces;
    /// <inheritdoc/>
    public MachineAccessResult Read(MachineMemoryAddress address, MachineAccessMode mode = MachineAccessMode.Inspect) =>
        Worker.AccessHardware(address, mode);
    /// <inheritdoc/>
    public MachineAccessResult Write(MachineMemoryAddress address, ulong value, MachineAccessMode mode) =>
        Worker.AccessHardware(address, mode, value);
}
