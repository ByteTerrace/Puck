using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

public sealed partial class AdvancedGamingBrickEngine {
    /// <inheritdoc/>
    public MachineOperationPreparation PrepareOperation(MachineCreationRequest current, MachineOperationRequest request) =>
        MachineOperationValidation.PrepareStandardOperation(
            current: current,
            descriptor: Descriptor,
            providerName: "advanced-gaming-brick",
            request: request
        );
}
