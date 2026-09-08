using System.Net.Http.Json;

namespace Puck.Azure.Functions.Services;

public sealed class CheckNameAvailabilityRequest
{
    public string? Name { get; set; }
    public string? Type { get; set; }
}

public sealed class CheckNameAvailabilityResponse
{
    public string? Message { get; set; }
    public bool NameAvailable { get; set; }
    public string? Reason { get; set; }
}

public interface IArmNameAvailabilityService
{
    Task<bool> IsAvailableAsync(
        string name,
        string subscriptionId,
        string type,
        string apiVersion = "",
        CancellationToken cancellationToken = default
    );
}

public sealed class DefaultArmNameAvailabilityService(IHttpClientFactory httpClientFactory) : IArmNameAvailabilityService
{
    public async Task<bool> IsAvailableAsync(
        string name,
        string subscriptionId,
        string type,
        string apiVersion = "2021-04-01",
        CancellationToken cancellationToken = default
    ) {
        var firstSlashIndex = type.IndexOf(value: '/');

        if ((-1 == firstSlashIndex) || !type.Contains(value: '.')) {
            throw new InvalidOperationException(message: "BYTRC_ARMNAMECHECK_000: invalid type");
        }

        var httpClient = httpClientFactory.CreateClient(name: "AzureResourceManager");
        var providerNamespace = type[0..firstSlashIndex];

        using var httpResponseMessage = await httpClient.PostAsJsonAsync(
            cancellationToken: cancellationToken,
            options: default,
            requestUri: $"https://management.azure.com/subscriptions/{subscriptionId}/providers/{providerNamespace}/checkNameAvailability?api-version={apiVersion}",
            value: new CheckNameAvailabilityRequest {
                Name = name,
                Type = type,
            }
        );

        httpResponseMessage.EnsureSuccessStatusCode();

        return (await httpResponseMessage
            .Content
            .ReadFromJsonAsync<CheckNameAvailabilityResponse>(cancellationToken: cancellationToken))!
            .NameAvailable;
    }
}

