using System.Text.Json;

namespace Puck.World.Protocol;

/// <summary>A generic provider operation addressed to one named machine instance.</summary>
public sealed record WorldMachineOperation {
    /// <summary>Creates an operation and detaches its JSON payload from the caller's document.</summary>
    /// <param name="instance">The authored machine instance name.</param>
    /// <param name="expectedGeneration">The incarnation the host must still hold at execution.</param>
    /// <param name="operationId">The provider descriptor operation identifier.</param>
    /// <param name="payload">The detached, descriptor-shaped provider payload.</param>
    public WorldMachineOperation(string instance, ulong expectedGeneration, string operationId, JsonElement payload) {
        Instance = instance;
        ExpectedGeneration = expectedGeneration;
        OperationId = operationId;
        Payload = payload.Clone();
    }
    /// <summary>Gets the authored machine instance name.</summary>
    public string Instance { get; }
    /// <summary>Gets the expected live incarnation.</summary>
    public ulong ExpectedGeneration { get; }
    /// <summary>Gets the provider operation identifier.</summary>
    public string OperationId { get; }
    /// <summary>Gets the detached provider payload.</summary>
    public JsonElement Payload { get; }
}