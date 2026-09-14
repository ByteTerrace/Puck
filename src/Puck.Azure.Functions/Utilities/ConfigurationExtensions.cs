using Microsoft.Extensions.Configuration;

namespace Puck.Azure.Functions.Utilities;

internal static class ConfigurationExtensions {
    private const string ActorsBaseUrlKey = "Onboarding:ActorsBaseUrl";

    /// <summary>
    /// Returns the base URL of the internal Actors API with any trailing separator removed, or
    /// <c>null</c> when it is not configured.
    /// </summary>
    public static string? GetActorsBaseUrl(this IConfiguration configuration) =>
        configuration
            .GetValue<string>(key: ActorsBaseUrlKey)
            ?.TrimEnd(trimChar: '/');
    /// <summary>
    /// Returns the base URL of the internal Actors API with any trailing separator removed, and throws
    /// when it is not configured.
    /// </summary>
    public static string GetRequiredActorsBaseUrl(this IConfiguration configuration) =>
        (configuration.GetActorsBaseUrl()
            ?? throw new InvalidOperationException(message: $"The \"{ActorsBaseUrlKey}\" configuration value is required."));
}
