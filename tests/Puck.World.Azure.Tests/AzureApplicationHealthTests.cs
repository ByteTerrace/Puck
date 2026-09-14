using System.Text.Json;
using Xunit;

namespace Puck.World.Azure.Tests;

public sealed class AzureApplicationHealthTests {
    [InlineData(true, "Healthy")]
    [InlineData(false, "Unhealthy")]
    [Theory]
    public void RichHealthUsesAzuresCaseSensitiveWireContract(bool live, string expected) {
        using var response = JsonDocument.Parse(AzureApplicationHealth.Response(live: live));
        var field = Assert.Single(collection: response.RootElement.EnumerateObject());

        Assert.Equal(
            "ApplicationHealthState",
            field.Name
        );
        Assert.Equal(
            expected,
            field.Value.GetString()
        );
    }
}
