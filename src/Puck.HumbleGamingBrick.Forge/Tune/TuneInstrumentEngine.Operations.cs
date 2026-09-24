using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick.Forge.Tune;

public sealed partial class TuneInstrumentEngine {
    /// <inheritdoc/>
    public MachineOperationPreparation PrepareOperation(MachineCreationRequest current, MachineOperationRequest request) =>
        MachineOperationValidation.PrepareStandardOperation(
            current: current,
            descriptor: Descriptor,
            providerName: "tune-instrument",
            request: request
        );
}
