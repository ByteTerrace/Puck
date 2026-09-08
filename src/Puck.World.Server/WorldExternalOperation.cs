using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World.Server;

/// <summary>The durable delivery state of an external operation, independent of gameplay undo.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldExternalOperationStatus>))]
public enum WorldExternalOperationStatus {
    /// <summary>Committed but not yet claimed for execution.</summary>
    Pending,
    /// <summary>Claimed before calling the provider; a crash here requires reconciliation.</summary>
    Dispatching,
    /// <summary>The provider accepted asynchronous work; completion requires reconciliation.</summary>
    Running,
    /// <summary>The provider confirmed completion.</summary>
    Succeeded,
    /// <summary>The provider confirmed failure.</summary>
    Failed,
    /// <summary>The result is uncertain. This is not permission to repeat the operation.</summary>
    Unknown,
}

/// <summary>A host-bound operation. Binding names resolve to provider configuration outside world documents.</summary>
/// <param name="Id">Stable, host-scoped id including the originating entity incarnation/request generation.</param>
/// <param name="Binding">The explicitly configured provider/resource/operation binding.</param>
/// <param name="BindingIdentity">Pins the concrete resource incarnation, operation, and provider input schema.</param>
/// <param name="Payload">Provider input in the binding's versioned schema, containing no host credentials.</param>
public sealed record WorldExternalOperation(string Id, string Binding, string BindingIdentity, string Payload);

/// <summary>A durable operation and the causal authority image committed atomically with it.</summary>
/// <param name="Operation">The immutable request.</param>
/// <param name="Cause">Opaque, versioned recovery evidence supplied by the authority host: a checkpoint or replay prefix.</param>
/// <param name="Status">The current delivery state.</param>
/// <param name="Result">Provider result or diagnostic, containing no credentials.</param>
public sealed record WorldExternalOperationEntry(
    [property: JsonRequired] WorldExternalOperation Operation,
    [property: JsonRequired] string Cause,
    [property: JsonRequired] WorldExternalOperationStatus Status = WorldExternalOperationStatus.Pending,
    [property: JsonRequired] string Result = "");

/// <summary>A provider's observation. Pending/Dispatching are journal-owned and cannot be returned by a provider.</summary>
/// <param name="Status">Running, Succeeded, Failed, or Unknown.</param>
/// <param name="Result">A versioned result payload, opaque to the delivery engine.</param>
public sealed record WorldExternalOperationResult(WorldExternalOperationStatus Status, string Result);

/// <summary>A trusted external service binding. Calls run outside the authority lock and never during replay.</summary>
/// <remarks>The same durable id accompanies every call. Reconciliation must observe without repeating side effects.
/// A provider unable to determine an outcome returns Unknown. Credentials and concrete resource selection belong
/// in the binding implementation, not a world-authored arbitrary URL.</remarks>
public interface IWorldExternalOperationProvider {
    /// <summary>Gets this binding's stable resource-incarnation, operation, and schema identity. Changing a target
    /// requires a different identity so recovery cannot silently redirect a pending operation.</summary>
    string Identity { get; }
    /// <summary>Executes a newly claimed request once. A lost response is reconciled rather than retried automatically.</summary>
    /// <param name="operation">The committed id, binding name, and provider input.</param>
    /// <param name="cancellationToken">Requests cancellation; this does not prove absence of side effects.</param>
    /// <returns>The provider's observation of the operation's status.</returns>
    ValueTask<WorldExternalOperationResult> ExecuteAsync(WorldExternalOperation operation, CancellationToken cancellationToken);
    /// <summary>Observes a previously claimed request without repeating its side effect.</summary>
    /// <param name="operation">The original immutable request.</param>
    /// <param name="previous">The last durable provider observation, including any opaque continuation data.
    /// It contains no authority recovery image. Preserve continuation data when an observation is uncertain.</param>
    /// <param name="cancellationToken">Cancels the status query.</param>
    /// <returns>A known status or Unknown when the provider cannot establish one.</returns>
    ValueTask<WorldExternalOperationResult> ReconcileAsync(WorldExternalOperation operation, WorldExternalOperationResult previous, CancellationToken cancellationToken);
}
