using Xunit;
using System.Text.Json;

namespace Puck.World.Schema.Tests;

public sealed class ViewSlotDefaultsLawTests {
    [Fact]
    public void ConstructingAnUnspecifiedSlotUsesTheFullWindow() {
        Assert.Equal(new WorldViewSlot(X: 0f, Y: 0f, Width: 1f, Height: 1f, Camera: null), new WorldViewSlot());
        var loaded = JsonSerializer.Deserialize(json: "{}", jsonTypeInfo: WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(WorldViewSlot)));

        Assert.Equal(new WorldViewSlot(), Assert.IsType<WorldViewSlot>(@object: loaded));
    }
}
