using Puck.Abstractions.Counting;
using Puck.Hosting;
using Xunit;

namespace Puck.World.Client.Tests;

[Collection(AllocationCollection.Name)]
public sealed class WorldBloomLawTests {
    [Fact]
    public void AnEmptyAuthoredLensKeepsItsDefaultsWhenReadAsAValue() {
        var lens = System.Text.Json.JsonSerializer.Deserialize<CameraBloom>("{}");

        Assert.Equal(new CameraBloom(Cutoff: 0f, Radius: .08f, Scatter: 0f), lens);
    }
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void ResolvingEverySeatAndFilmedCameraAllocatesNothingWithBloomOnOrOff(bool authored, bool enabled) {
        var render = new WorldRenderDefaults(Bloom: (authored ? new CameraBloom(.04f) : null));
        var settings = new WorldRenderSettings(defaults: render) { BloomOverride = enabled };
        var camera = new CameraBloom(.2f, .1f);

        settings.SetBloomScale(scale: 0f, seat: 1);
        var checksum = 0f;
        var allocated = AllocationWindow.Least(window: () => {
            for (var frame = 0; (frame < 64); frame++) {
                checksum += settings.ResolveBloom(render).Scatter;
                checksum += settings.ResolveBloom(render, camera).Scatter;
                checksum += settings.ResolveBloom(render, camera, settings.BloomScale(seat: 1)).Scatter;
            }
        });

        Assert.Equal(actual: allocated, expected: 0L);
        Assert.Equal((enabled ? .2f : 0f), settings.ResolveBloom(render, camera).Scatter);
        Assert.Equal(0f, settings.ResolveBloom(render, camera, settings.BloomScale(seat: 1)).Scatter);
        Assert.Equal(.08f, settings.ResolveBloom(new WorldRenderDefaults()).Radius);
        Assert.True(condition: float.IsFinite(f: checksum));
    }
    [InlineData(0f, .2f)]
    [InlineData(.1f, 0f)]
    [Theory]
    public void TheActiveEndpointProgramOwnsTheLensIncludingAnExplicitOff(float seatScatter, float cameraScatter) {
        var seat = WorldViewDefaults.Absent.SeatRig with { Bloom = new CameraBloom(seatScatter) };
        var camera = seat with { Bloom = new CameraBloom(cameraScatter) };
        var endpoint = Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(Bloom: new CameraBloom(.8f)),
            ViewsRaw = WorldViewDefaults.Absent with { SeatRigRaw = seat, CameraRig = camera },
        };
        var settings = new WorldRenderSettings(defaults: new WorldRenderDefaults(Bloom: new CameraBloom(.9f)));

        Assert.Equal(seatScatter, settings.ResolveBloom(endpoint.Render,
            WorldSeatViewState.ProgramOf(endpoint.Views, false).Bloom).Scatter);
        Assert.Equal(cameraScatter, settings.ResolveBloom(endpoint.Render,
            WorldSeatViewState.ProgramOf(endpoint.Views, true).Bloom).Scatter);
        var inherited = endpoint.Views with { SeatRigRaw = seat with { Bloom = null }, CameraRig = null };

        Assert.Equal(.8f, settings.ResolveBloom(endpoint.Render, WorldSeatViewState.ProgramOf(cameraMode: true, views: inherited).Bloom).Scatter);
    }
}
