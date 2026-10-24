using Puck.Commands;
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
    /// <param name="addonDerived">The operation comes from a mounted addon's guest, which re-derives it deterministically
    /// under receipt-pinned replay, so it needs no tape entry and is allowed while a recording is armed. It is honored
    /// only for an <see cref="PrincipalKind.Addon"/> principal. A <c>content.insert</c> payload names a file in a
    /// content folder that is read at apply time, so a re-driven tape diverges if that folder changed.</param>
    /// <returns>A typed provider outcome. Preparation and commit remain host-owned so replacement admission is not
    /// confused with live runtime application.</returns>
    public MachineOperationResult ApplyMachineOperation(
        WorldMachineOperation operation,
        Principal principal,
        int connectionId,
        long correlationId,
        bool addonDerived = false
    ) {
        _ = connectionId;
        _ = correlationId;

        if (string.IsNullOrWhiteSpace(value: operation.Instance)) {
            return new MachineOperationResult(
                status: MachineOperationStatus.Refused,
                reason: "machine operation requires a named instance"
            );
        }

        var subject = GrantSubject.Machine(name: operation.Instance);
        var control = m_grants.Allows(
            capability: WorldCapability.Control,
            principal: principal,
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
        if (ScreenOpTap is not null && !(addonDerived && principal.Kind == PrincipalKind.Addon)) {
            return new MachineOperationResult(
                MachineOperationStatus.Refused,
                reason: $"machine operations cannot execute while recording ({nameof(WorldMachineOperationRefusal.WhileRecording)}); the current replay format does not capture provider operations"
            );
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
            var currentMachines = m_document.Definition.Machines;
            var candidateMachines = currentMachines.ToArray();
            var candidateIndex = -1;

            for (var index = 0; (index < candidateMachines.Length); index++) {
                if (string.Equals(
                    a: candidateMachines[index].Name,
                    b: plan.Current.Name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    candidateIndex = index;
                    break;
                }
            }
            if (
                (candidateIndex < 0) ||
                !JsonElement.DeepEquals(
                element1: candidateMachines[candidateIndex].Configuration,
                element2: plan.Current.Configuration
            )
            ) {
                return new MachineOperationResult(
                    status: MachineOperationStatus.Refused,
                    reason: $"Machine '{operation.Instance}' declaration changed before candidate validation."
                );
            }

            candidateMachines[candidateIndex] = plan.Candidate;
            var candidateDefinition = m_document.Definition with { MachinesRaw = candidateMachines };

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

            // A provider can fault after changing hardware, so the live state leaves its boot image at the runtime
            // barrier, failed applications included.
            AnyScreenOpEverApplied = true;
            var result = m_machines.TryCommitOperation(plan: plan);

            if (result.Status != MachineOperationStatus.Applied) {
                return result;
            }

            // The host has crossed its runtime barrier. Candidate validation happened before commit, so this is only
            // a declaration adoption and delivery flag; no unrelated fallible install or second reconstruction runs.
            m_document.AdoptDefinition(definition: candidateDefinition);
            m_document.MarkDefinitionDeliveryPending();
            return result;
        }
    }
}
