using System.Text.Json;
using Xunit;

namespace Puck.World.Azure.Tests;

public sealed class AzureApplicationHealthTests {
    [Theory]
    [InlineData(true, "Healthy")]
    [InlineData(false, "Unhealthy")]
    public void RichHealthUsesAzuresCaseSensitiveWireContract(bool live, string expected) {
        using var response = JsonDocument.Parse(AzureApplicationHealth.Response(live));
        var field = Assert.Single(response.RootElement.EnumerateObject());
        Assert.Equal("ApplicationHealthState", field.Name);
        Assert.Equal(expected, field.Value.GetString());
    }
}
