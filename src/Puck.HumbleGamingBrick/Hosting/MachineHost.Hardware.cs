using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick;

public sealed partial class MachineHost {
    /// <inheritdoc/>
    public override IReadOnlyList<MachineMemorySpaceDescriptor> MemorySpaces => HumbleGamingBrickCore.HardwareSpaces;
}
