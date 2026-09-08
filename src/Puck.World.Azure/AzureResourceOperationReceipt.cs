using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World.Azure;

/// <summary>Durable ARM progress metadata. Raw resource bodies and service error messages are not retained.</summary>
/// <param name="Version">The receipt schema version, currently 1.</param>
/// <param name="HttpStatus">The last observed HTTP status.</param>
/// <param name="PollUri">The same-origin ARM status URL, if one was received.</param>
/// <param name="PollKind">Azure-AsyncOperation, Operation-Location, or Location; null if no status URL was received.</param>
/// <param name="RetryAfter">Azure's polling delay header, in seconds or HTTP-date form. The host schedules polls.</param>
/// <param name="RequestId">Azure's service request id for diagnostics; not an idempotency guarantee.</param>
public sealed record AzureResourceOperationReceipt(int Version, int HttpStatus, string? PollUri,
    string? PollKind, string? RetryAfter, string? RequestId) {
    /// <summary>Decodes a durable result for host read-back and scheduling.</summary>
    /// <param name="result">The provider result string from the external operation journal.</param>
    /// <returns>The version-checked receipt.</returns>
    /// <exception cref="JsonException">The result is not a supported receipt.</exception>
    public static AzureResourceOperationReceipt Parse(string result) {
        var receipt = JsonSerializer.Deserialize(result, AzureResourceJsonContext.Default.AzureResourceOperationReceipt);
        if (receipt is not { Version: 1 }) { throw new JsonException("Unsupported Azure resource receipt."); }
        return receipt;
    }

    internal string Encode() => JsonSerializer.Serialize(this, AzureResourceJsonContext.Default.AzureResourceOperationReceipt);
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(AzureResourceOperationReceipt))]
internal sealed partial class AzureResourceJsonContext : JsonSerializerContext;
