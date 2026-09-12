using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Applies one ordered generic machine operation after checking named-instance Control authority.</summary>
    /// <param name="operation">The detached operation request from the submission envelope.</param>
    /// <param name="principal">The envelope's authenticated actor.</param>
    /// <param name="connectionId">The submitting connection, retained for future operation narration.</param>
    /// <param name="correlationId">The envelope correlation, retained for future operation narration.</param>
    /// <returns>A typed provider outcome. Preparation and commit remain host-owned so replacement admission is not
    /// confused with live runtime application.</returns>
    private MachineOperationResult ApplyMachineOperation(
        WorldMachineOperation operation,
        WorldPrincipal principal,
        int connectionId,
        long correlationId
    ) {
        _ = connectionId;
        _ = correlationId;

        if (string.IsNullOrWhiteSpace(operation.Instance)) {
            return new MachineOperationResult(
                status: MachineOperationStatus.Refused,
                reason: "machine operation requires a named instance"
            );
        }

        var subject = GrantSubject.Machine(name: operation.Instance);
        var control = m_grants.Allows(
            principal: principal,
            capability: WorldCapability.Control,
            subject: subject
        );

        if (!control.IsAllowed) {
            return new MachineOperationResult(
                status: MachineOperationStatus.Refused,
                reason: control.DescribeRefusal(
                    actor: principal,
                    verb: "control",
                    subject: subject.Describe()
                )
            );
        }

        // Generic operation receipts are not part of the current replay format. Refuse before preparation while
        // recording, rather than let an untaped runtime change contaminate an otherwise valid tape.
        if (ScreenOpTap is not null) {
            return new MachineOperationResult(MachineOperationStatus.Refused,
                reason: "machine operations cannot execute while recording; the current replay format does not capture provider operations");
        }

        var request = new MachineOperationRequest(
            id: operation.OperationId,
            payload: operation.Payload
        );

        if (!m_machines.TryPrepareOperation(
            instance: operation.Instance,
            expectedGeneration: operation.ExpectedGeneration,
            request: request,
            plan: out var plan,
            refusal: out var refusal
        )) {
            return refusal;
        }

        if (plan is null) {
            return new MachineOperationResult(
                status: MachineOperationStatus.Faulted,
                reason: "machine host accepted preparation without a prepared plan"
            );
        }

        // Ownership starts before any candidate check: replacement plans own a newly constructed runtime and must
        // be disposed on every validation or commit refusal.
        using (plan) {
            var currentMachines = m_definition.Machines;
            var candidateMachines = currentMachines.ToArray();
            var candidateIndex = -1;
            for (var index = 0; index < candidateMachines.Length; index++) {
                if (string.Equals(candidateMachines[index].Name, plan.Current.Name, StringComparison.Ordinal)) {
                    candidateIndex = index;
                    break;
                }
            }
            if (candidateIndex < 0 ||
                !JsonElement.DeepEquals(candidateMachines[candidateIndex].Configuration, plan.Current.Configuration)) {
                return new MachineOperationResult(
                    status: MachineOperationStatus.Refused,
                    reason: $"Machine '{operation.Instance}' declaration changed before candidate validation."
                );
            }

            candidateMachines[candidateIndex] = plan.Candidate;
            var candidateDefinition = m_definition with { MachinesRaw = candidateMachines };
            if (!WorldDefinitionValidator.TryValidateLocally(
                definition: candidateDefinition,
                machines: m_machines.ValidationCatalog,
                reason: out var candidateReason
            )) {
                return new MachineOperationResult(
                    status: MachineOperationStatus.Refused,
                    reason: $"machine operation candidate refused: {candidateReason}"
                );
            }

            // A provider can fault after changing hardware. Conservatively close the existing boot-only
            // replay/checkpoint gate at the runtime barrier, including such failed applications.
            AnyScreenOpEverApplied = true;
            var result = m_machines.TryCommitOperation(plan: plan);
            if (result.Status != MachineOperationStatus.Applied) {
                return result;
            }

            // The host has crossed its runtime barrier. Candidate validation happened before commit, so this is only
            // a declaration adoption and delivery flag; no unrelated fallible install or second reconstruction runs.
            m_definition = candidateDefinition;
            m_pendingDefinitionDelivery = true;
            return result;
        }
    }
}
