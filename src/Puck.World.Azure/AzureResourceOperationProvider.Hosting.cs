using System.Globalization;
using System.Text.Json;
using Puck.World.Server;

namespace Puck.World.Azure;

public sealed partial class AzureResourceOperationProvider {
    /// <summary>Registers this binding with the shared extension host, including input discovery and Azure polling hints.</summary>
    /// <param name="description">The host's caller-visible description. Do not include credentials or private resource details.</param>
    /// <returns>A registration for explicit host composition; no code discovery, credential lookup, or service call.</returns>
    public WorldExtensionOperation Register(string description) {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        return new(new(m_binding.Name, description, "{\"type\":\"object\"}"), this, CreateOperation, PollDelay);
    }

    private static TimeSpan? PollDelay(WorldExternalOperationResult result, DateTimeOffset now) {
        AzureResourceOperationReceipt receipt;
        try { receipt = AzureResourceOperationReceipt.Parse(result.Result); }
        catch (JsonException) { return null; }
        if (long.TryParse(receipt.RetryAfter, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0) {
            return TimeSpan.FromTicks(Math.Min(seconds, TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond) * TimeSpan.TicksPerSecond);
        }
        return DateTimeOffset.TryParse(receipt.RetryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var due)
            ? due > now ? due - now : TimeSpan.Zero : null;
    }
}
