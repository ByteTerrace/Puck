using System.Text.Json;

namespace Puck.Abstractions.Machines;

/// <summary>Prepares a provider operation without mutating a live machine.</summary>
/// <remarks>The host validates authority, instance generation, and prepared assets before applying the returned
/// operation. A replacement is constructed and committed by the host; a runtime operation is applied only after the
/// host has crossed its execution barrier.</remarks>
public interface IMachineOperationProvider {
    /// <summary>Validates and prepares one descriptor-declared operation.</summary>
    /// <param name="current">The host-prepared configuration currently mounted on the instance.</param>
    /// <param name="request">The provider operation identifier and typed JSON payload.</param>
    /// <returns>A host replacement, a runtime action, or a refusal. Preparation never reads a path or mutates the
    /// supplied machine.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is <see langword="null"/>.</exception>
    MachineOperationPreparation PrepareOperation(MachineCreationRequest current, MachineOperationRequest request);
}

/// <summary>A provider operation envelope before authority and generation are applied by its host.</summary>
public readonly record struct MachineOperationRequest {
    /// <summary>Creates a request and detaches its payload from the caller's JSON document.</summary>
    /// <param name="id">The exact descriptor operation identifier.</param>
    /// <param name="payload">The operation payload, including its descriptor schema tag.</param>
    /// <exception cref="InvalidOperationException">The payload is not cloneable because its source document is invalid.</exception>
    public MachineOperationRequest(string id, JsonElement payload) { Id = id; Payload = payload.Clone(); }
    /// <summary>Gets the provider operation identifier.</summary>
    public string Id { get; }
    /// <summary>Gets the detached typed JSON payload.</summary>
    public JsonElement Payload { get; }
}

/// <summary>The outcome of applying a prepared runtime operation.</summary>
public enum MachineOperationStatus {
    /// <summary>The operation was invalid or violates a provider constraint.</summary>
    Refused,
    /// <summary>The provider operation completed.</summary>
    Applied,
    /// <summary>The provider or runtime does not support the requested capability.</summary>
    Unsupported,
    /// <summary>A provider or runtime fault prevented completion.</summary>
    Faulted,
}

/// <summary>The explicit result of an operation application or refusal.</summary>
public readonly record struct MachineOperationResult {
    /// <summary>Creates an explicit operation result and detaches its optional value.</summary>
    /// <param name="status">The operation outcome.</param>
    /// <param name="value">An optional provider result value.</param>
    /// <param name="reason">An optional diagnostic reason.</param>
    public MachineOperationResult(MachineOperationStatus status, JsonElement? value = null, string? reason = null) {
        Status = status; Value = value?.Clone(); Reason = reason;
    }
    /// <summary>Gets whether the operation completed or was refused.</summary>
    public MachineOperationStatus Status { get; }
    /// <summary>Gets the optional detached provider result.</summary>
    public JsonElement? Value { get; }
    /// <summary>Gets the diagnostic reason.</summary>
    public string? Reason { get; }
}

/// <summary>An operation prepared without changing the live instance.</summary>
public abstract record MachineOperationPreparation {
    private MachineOperationPreparation() { }
    /// <summary>A host-owned replacement configuration, prepared and committed atomically by the host.</summary>
    public sealed record Replacement : MachineOperationPreparation {
        /// <summary>Creates a replacement and detaches its configuration.</summary>
        /// <param name="configuration">The complete provider configuration to construct.</param>
        public Replacement(JsonElement configuration) => Configuration = configuration.Clone();
        /// <summary>Gets the canonical replacement configuration.</summary>
        public JsonElement Configuration { get; }
    }
    /// <summary>A runtime action to apply at the host's coherent execution barrier.</summary>
    public sealed record Runtime : MachineOperationPreparation {
        /// <summary>Creates a runtime action and detaches its optional configuration.</summary>
        /// <param name="operation">The validated runtime action.</param>
        /// <param name="configuration">The configuration to adopt after an applied action, if any.</param>
        /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
        public Runtime(IMachinePreparedOperation operation, JsonElement? configuration = null) {
            ArgumentNullException.ThrowIfNull(operation); Operation = operation; Configuration = configuration?.Clone();
        }
        /// <summary>Gets the prepared runtime action.</summary>
        public IMachinePreparedOperation Operation { get; }
        /// <summary>Gets the canonical configuration to adopt after success.</summary>
        public JsonElement? Configuration { get; }
    }
    /// <summary>A provider refusal produced before runtime mutation.</summary>
    public sealed record Refusal : MachineOperationPreparation {
        /// <summary>Creates a refusal; an applied result cannot be represented as one.</summary>
        /// <param name="result">The refusal, unsupported, or faulted result.</param>
        /// <exception cref="ArgumentException"><paramref name="result"/> reports <see cref="MachineOperationStatus.Applied"/>.</exception>
        public Refusal(MachineOperationResult result) {
            if (result.Status == MachineOperationStatus.Applied) { throw new ArgumentException("An applied operation result cannot be a preparation refusal.", nameof(result)); }
            Result = result;
        }
        /// <summary>Gets the refusal result.</summary>
        public MachineOperationResult Result { get; }
    }
}

/// <summary>A provider operation whose validation and target data are complete and safe to apply to one runtime.</summary>
public interface IMachinePreparedOperation {
    /// <summary>Applies this action at the host's coherent execution barrier.</summary>
    /// <param name="runtime">The live runtime selected by the host after its authority and generation checks.</param>
    /// <returns>An applied, unsupported, refused, or faulted result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is <see langword="null"/>.</exception>
    MachineOperationResult Apply(IMachineRuntime runtime);
}

/// <summary>Shared descriptor lookup and payload validation for provider operation adapters.</summary>
public static class MachineOperationValidation {
    /// <summary>Finds and validates an operation payload against one engine descriptor.</summary>
    /// <param name="descriptor">The provider descriptor whose operation list is authoritative.</param>
    /// <param name="request">The operation identifier and payload to validate.</param>
    /// <param name="operation">Receives the matching descriptor on success; otherwise <see langword="null"/>.</param>
    /// <param name="failure">Receives Unsupported for an unknown operation or Refused for an invalid payload.</param>
    /// <returns><see langword="true"/> when the request names a descriptor operation and its payload is valid.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
    public static bool TryValidate(MachineEngineDescriptor descriptor, MachineOperationRequest request,
        out MachineOperationDescriptor? operation, out MachineOperationResult failure) {
        ArgumentNullException.ThrowIfNull(descriptor);
        operation = null;
        foreach (var candidate in descriptor.Operations) {
            if (string.Equals(candidate.Id, request.Id, StringComparison.Ordinal)) { operation = candidate; break; }
        }
        if (operation is null) {
            failure = new(MachineOperationStatus.Unsupported, reason: $"provider does not support operation '{request.Id}'");
            return false;
        }
        var errors = new List<string>();
        if (!MachineConfigurationValidation.TryValidate(operation.Payload, request.Payload, schemaTag: true, errors)) {
            failure = new(MachineOperationStatus.Refused, reason: string.Join(" ", errors));
            return false;
        }
        failure = default;
        return true;
    }
}