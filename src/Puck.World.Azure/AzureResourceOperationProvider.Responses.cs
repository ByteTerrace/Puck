using System.Text.Json;
using Azure;
using Puck.World.Server;

namespace Puck.World.Azure;

public sealed partial class AzureResourceOperationProvider {
    private async ValueTask<WorldExternalOperationResult> InterpretAsync(Response response,
        AzureResourceOperationReceipt? previous, CancellationToken cancellationToken) {
        var receipt = new AzureResourceOperationReceipt(1, response.Status, previous?.PollUri, previous?.PollKind,
            Header(response, "Retry-After") ?? previous?.RetryAfter, Header(response, "x-ms-request-id") ?? previous?.RequestId);
        var success = response.Status is >= 200 and < 300;
        if (!success) {
            // A status-query failure says nothing definitive about the original effect. Neither do server
            // errors or timeouts on the initial mutation. Keep the poll reference for a future observation.
            var rejected = previous is null && response.Status is 400 or 401 or 403 or 404 or 405 or 409 or 412 or 422;
            return new(rejected ? WorldExternalOperationStatus.Failed : WorldExternalOperationStatus.Unknown, receipt.Encode());
        }
        if (previous is null) {
            foreach (var kind in new[] { "Azure-AsyncOperation", "Operation-Location", "Location" }) {
                if (Header(response, kind) is not { } poll) { continue; }
                if (!TryPollUri(poll, out var uri)) { return new(WorldExternalOperationStatus.Unknown, receipt.Encode()); }
                receipt = receipt with { PollUri = uri.AbsoluteUri, PollKind = kind };
                return new(WorldExternalOperationStatus.Running, receipt.Encode());
            }
        }

        // Preserve receipt even when a body is truncated, oversized, malformed, or lost after its headers.
        string? state;
        try { state = await ReadStateAsync(response, previous?.PollKind, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or JsonException or OperationCanceledException) {
            return new(WorldExternalOperationStatus.Unknown, receipt.Encode());
        }
        var status = state?.ToUpperInvariant() switch {
            "SUCCEEDED" => WorldExternalOperationStatus.Succeeded,
            "FAILED" or "CANCELED" or "CANCELLED" => WorldExternalOperationStatus.Failed,
            not null => WorldExternalOperationStatus.Running,
            null when previous?.PollKind is "Azure-AsyncOperation" or "Operation-Location" => WorldExternalOperationStatus.Unknown,
            null when response.Status == 202 => WorldExternalOperationStatus.Running,
            _ => WorldExternalOperationStatus.Succeeded,
        };
        return new(status, receipt.Encode());
    }

    private async ValueTask<string?> ReadStateAsync(Response response, string? pollKind, CancellationToken cancellationToken) {
        if (response.ContentStream is not { } stream) { return null; }
        using var bytes = new MemoryStream();
        var buffer = new byte[Math.Min(8192, m_maximumResponseBytes)];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0) {
            if (bytes.Length + count > m_maximumResponseBytes) { throw new IOException("Azure response exceeds its byte budget."); }
            bytes.Write(buffer, 0, count);
        }
        if (bytes.Length == 0) { return null; }
        using var body = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)));
        var root = body.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { throw new JsonException("ARM status must be an object."); }
        if (pollKind is "Azure-AsyncOperation" or "Operation-Location") { return State(root, "status"); }
        if (State(root, "provisioningState") is { } topState) { return topState; }
        return root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? State(properties, "provisioningState") : null;
    }

    private static string? State(JsonElement value, string property) {
        if (!value.TryGetProperty(property, out var state)) { return null; }
        if (state.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(state.GetString())) {
            throw new JsonException("ARM operation status must be a nonempty string.");
        }
        return state.GetString();
    }

    private static string? Header(Response response, string name) => response.Headers.TryGetValue(name, out var value) ? value : null;
}
