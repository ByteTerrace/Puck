using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

public sealed partial class AdvancedMachineHost {
    /// <inheritdoc/>
    public override IReadOnlyList<MachineMemorySpaceDescriptor> MemorySpaces => AdvancedGamingBrickCore.HardwareSpaces;
}
