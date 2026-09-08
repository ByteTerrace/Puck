namespace Puck.World.Azure;

/// <summary>Formats the Azure Application Health extension's rich health response.</summary>
public static class AzureApplicationHealth {
    /// <summary>Returns the v2 HTTP response body for the host's current liveness.</summary>
    /// <param name="live">Whether the host is making simulation progress and is not retiring.</param>
    /// <returns>A JSON object with Azure's case-sensitive ApplicationHealthState field.</returns>
    /// <remarks>Serve both states with HTTP 200 and application/json. A non-success status or absent body means Unknown to Azure.</remarks>
    public static string Response(bool live) => live
        ? "{\"ApplicationHealthState\":\"Healthy\"}"
        : "{\"ApplicationHealthState\":\"Unhealthy\"}";
}
