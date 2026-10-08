namespace Puck.Shaders.Tests;

public sealed partial class SdfPassPlanLawTests {
    [Fact]
    public void ShadowHistoryIsWriterOrderedAtTheRenderExtentAndAbsentFromSpatialViews() {
        Assert.DoesNotContain(collection: SdfWorldPackage.NativeFragment.Resources, filter: static resource => (resource.Name == SdfWorldPackage.ShadowHistory));
        Assert.DoesNotContain(collection: SdfWorldPackage.Fragment.Resources, filter: static resource => (resource.Name == SdfWorldPackage.ShadowHistory));
        foreach (var capacity in new[] { 0, 1, 2 }) {
            var fragment = SdfWorldPackage.FragmentFor(fadeCapacity: capacity, reconstructs: true, temporal: true);
            var history = Assert.Single(collection: fragment.Resources, predicate: static resource => (resource.Name == SdfWorldPackage.ShadowHistory));

            Assert.True(condition: history.History);
            Assert.False(condition: history.Transient);
            Assert.Equal(ShaderPipelineInitialization.Zero, history.Initialization);
            Assert.Equal(((20UL * 960) * 540), history.ResolveSizeBytes(counts: new ShaderPipelineStorageCounts(Height: 1080, Width: 1920) {
                RenderHeight = 540,
                RenderWidth = 960,
                Viewports = 1,
            }));
            var shadow = fragment.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.Shadow));

            Assert.Contains(collection: shadow.Inputs, filter: static input => ((input.Name == SdfWorldPackage.ShadowHistory) && input.PreviousFrame));
            Assert.Contains(collection: shadow.Outputs, filter: static output => ((output.Name == SdfWorldPackage.ShadowHistory) && !output.PreviousFrame));
            Assert.True(condition: shadow.CountsKernelWork);
            var views = fragment.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.Views));

            Assert.Contains(collection: views.Inputs, filter: static input => ((input.Name == SdfWorldPackage.ShadowHistory) && !input.PreviousFrame));
        }
    }
    [Fact]
    public void IncomingVisibilityIsTwoBytesAPixelAtEveryNonzeroCapacityAndAbsentAtZero() {
        foreach (var (reconstructs, temporal) in new[] { (false, false), (true, false), (false, true) }) {
            Assert.DoesNotContain(collection: SdfWorldPackage.FragmentFor(fadeCapacity: 0, reconstructs: reconstructs, temporal: temporal).Resources,
                filter: static resource => (resource.Name == SdfWorldPackage.IncomingVisibility));
            foreach (var capacity in new[] { 1, 2 }) {
                var fragment = SdfWorldPackage.FragmentFor(fadeCapacity: capacity, reconstructs: reconstructs, temporal: temporal);
                var image = Assert.Single(collection: fragment.Resources, predicate: static resource => (resource.Name == SdfWorldPackage.IncomingVisibility));

                // One shadow kernel writes both fade channels, so one channel's capacity still allocates two bytes a
                // pixel, at the grid the shadow pass marches.
                Assert.True(condition: image.Retained);
                Assert.Equal(expected: Puck.Abstractions.Gpu.GpuPixelFormat.R8G8Unorm.ToString(), actual: image.Format);
                Assert.Equal(expected: 2U, actual: Puck.Abstractions.Gpu.GpuPixelFormats.UnitBytes(format: Puck.Abstractions.Gpu.GpuPixelFormat.R8G8Unorm));
                Assert.Equal(expected: ((reconstructs || temporal) ? ShaderPipelineDimensions.Render() : ShaderPipelineDimensions.Relative()), actual: image.Dimensions);
            }
        }
    }
}
