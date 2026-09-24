using System.Text.Json;
using Microsoft.Extensions.AI;
using Puck.Abstractions;
using Puck.World.Agents.Harness;
using Puck.World.Agents.Harness.Azure;
using Xunit;

namespace Puck.World.Agents.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the <c>azure.openai</c> chat client provider authenticates only with identity. It registers
/// under its provider name, creates a client from an endpoint and deployment without calling the service, and refuses
/// a key, a non-https endpoint, or a blank deployment by name.
/// </summary>
public sealed class AzureOpenAiChatClientLawTests {
    private static JsonElement Settings(string json) => JsonDocument.Parse(json: json).RootElement.Clone();
    private static string Refusal(string json) => Assert.Throws<ArgumentException>(testCode: () =>
        AzureOpenAiChatClientExtension.Create(settings: Settings(json: json))).Message;

    [Fact]
    public void TheProviderRegistersUnderItsNameAndCreatesAClientWithoutCallingTheService() {
        var extensions = PuckExtensionSet.Compose(extensions: [new AzureOpenAiChatClientExtension()]);

        Assert.Equal(
            expected: ["Puck.World.AgentHarness.Azure: ChatClientProvider azure.openai"],
            actual: extensions.Describe()
        );
        using var client = ChatClientProvider.Select(
            extensions: extensions,
            name: AzureOpenAiChatClientExtension.ProviderName
        ).Create(arg: Settings(json: """{ "endpoint": "https://example.openai.azure.com/", "deployment": "chat" }"""));

        Assert.IsAssignableFrom<IChatClient>(@object: client);
    }
    [InlineData("""{ "endpoint": "https://example.openai.azure.com/", "deployment": "chat", "apiKey": "secret" }""", "'apiKey'")]
    [InlineData("""{ "endpoint": "http://example.openai.azure.com/", "deployment": "chat" }""", "must be an absolute https URI")]
    [InlineData("""{ "endpoint": "https://example.openai.azure.com/", "deployment": " " }""", "deployment must be non-blank.")]
    [InlineData("""{ "deployment": "chat" }""", "'endpoint'")]
    [Theory]
    public void KeysAndMalformedSettingsAreRefusedByName(string json, string expected) => Assert.Contains(
        actualString: Refusal(json: json),
        expectedSubstring: expected
    );
}
