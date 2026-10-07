using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The shadow pass partitions its march total by stable slot and active incoming channel, and every
/// visibility store counts. Source contracts pin the uniform gather seam; readback runs through the real counters.</summary>
public sealed class SdfShadowCountingLawTests {
    private static string Code(string path) => Regex.Replace(
        input: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: $"{SdfKernelInterfaces.KernelDirectory}/{path}")),
        pattern: @"//[^\n]*", replacement: string.Empty);

    [Fact]
    public void OnlyConfiguredStableSlotsAndActiveHandoffsReachTheSingleMarchSite() {
        var lights = Code(path: "frame/sdf-lights.hlsli");
        var shadow = Code(path: "surface/sdf-shadow.hlsli");

        Assert.Equal(actual: GpuWork.ShadowSlotCount, expected: (SdfShadowSlots.MaxSlots + SdfShadowSlots.MaxFadeSlots));
        Assert.Contains(actualString: lights, expectedSubstring: "worldShadowStableCount() + worldShadowIncomingCount()");
        Assert.Contains(actualString: lights, expectedSubstring: "min(passGroup.shadowSlotCount, SDF_MAX_SHADOW_SLOTS)");
        Assert.Contains(actualString: lights, expectedSubstring: "sdfShadowHandoffs.GetDimensions(count, stride)");
        Assert.Contains(actualString: lights, expectedSubstring: "min(passGroup.shadowFadeCount, min(count, (uint)SDF_SHADOW_FADE_SLOTS))");
        Assert.Contains(actualString: shadow, expectedSubstring: "shadowSlot < worldShadowMarchCount()");
        Assert.Single(collection: Regex.Matches(input: shadow, pattern: @"\bsoftShadowVisibility\("));
        Assert.Contains(actualString: shadow, expectedSubstring: "puckCountShadow(shadowSlot, (sdfWorkSteps - before));");
        Assert.Matches(actualString: shadow, expectedRegexPattern: @"(?s)puckCountShadow\([^;]+;.*GroupMemoryBarrierWithGroupSync\(\);\s*\}\s*if \(!p.active\)");
        Assert.Matches(actualString: shadow, expectedRegexPattern: @"if \(finalMode && cullOn && !worldUseCameraTileShadowMask\(\) && shadowsOn && hasLight\)\s*\{\s*groupGather = sdfShadowGatherGroup");
    }
    [Fact]
    public void EveryVisibilityWriteCountsAndInactiveFadesWriteNothing() {
        var shadow = Code(path: "surface/sdf-shadow.hlsli");
        var visibility = Code(path: "frame/sdf-visibility.hlsli");

        Assert.Contains(actualString: shadow, expectedSubstring: "sdfStoreVisibilityShadows(record, stableVisibility);");
        Assert.Matches(actualString: visibility, expectedRegexPattern: @"sdfVisibilityStoreWord\(record \+ SdfVisibilityRowK, sdfPackShadowVisibility\(visibility\)\);");
        Assert.Matches(actualString: shadow, expectedRegexPattern: @"(?s)#if SDF_SHADOW_FADE_SLOTS > 0\s*if \(passGroup.shadowFadeCount > 0u\)\s*\{.*incomingVisibilityRW\[p.pixel\] = incoming.x;.*incomingVisibilityRW\[p.pixel\] = incoming;.*sdfWorkTexels \+= 1u;");
    }
    [Fact]
    public void EachLightsOwnDeficitIsWeightedWithoutCrossfadingRadiance() {
        var light = Code(path: "shade/sdf-light.hlsli");

        Assert.Contains(actualString: light, expectedSubstring: "1.0 - ((1.0 - stable[handoff.Slot]) * (1.0 - handoff.Weight))");
        Assert.Contains(actualString: light, expectedSubstring: "1.0 - ((1.0 - incoming[fade]) * handoff.Weight)");
        Assert.Contains(actualString: light, expectedSubstring: "response.diffuse = (light.color * ((light.weight * lambert) * occlusion));");
        Assert.DoesNotContain(actualString: light, expectedSubstring: "light.key");
    }
    [Fact]
    public void UnslottedDirectionalUsesAmbientOcclusionAndSunDiffuseStaysUnscaled() {
        var light = Code(path: "shade/sdf-light.hlsli");
        var visibility = light[light.IndexOf(comparisonType: StringComparison.Ordinal, value: "float sdfLightVisibility(")..light.IndexOf(comparisonType: StringComparison.Ordinal, value: "struct SdfLightResponse")];

        Assert.Contains(actualString: light, expectedSubstring: "sdfLightVisibility(light.index, surface.shadowVisibility, surface.incomingVisibility, surface.ambientOcclusion)");
        Assert.Matches(actualString: visibility, expectedRegexPattern: @"return ambientOcclusion;\s*\}\s*$");
        Assert.Contains(actualString: visibility, expectedSubstring: "return stable[shadowSlot];");
        Assert.Contains(actualString: Code(path: "passes/sdf-light-stage.hlsli"), expectedSubstring: "sunDiffuse *= sdfLightVisibility(passGroup.shadowSlots.x, shadowVisibility, incoming, 1.0);");
    }
    [Fact]
    public void CounterReadbackPreservesEachSlotAndZerosPastActiveWork() {
        var gpu = new UploadModelGpu();
        using var counters = new GpuKernelCounters(buffers: gpu.Services.BufferFactory, slots: 1, rows: 1,
            owner: "shadow-law", part: "counters");
        using var commands = gpu.Services.CommandPoolFactory.Create(name: default);
        var command = commands.CommandBufferHandle;
        var columns = GpuWork.SubmissionKinds.ToArray();

        for (var stable = 0; (stable <= SdfShadowSlots.MaxSlots); stable++) {
            for (var capacity = 0; (capacity <= SdfShadowSlots.MaxFadeSlots); capacity++) {
                for (var active = 0; (active <= Math.Min(val1: stable, val2: capacity)); active++) {
                    var memory = gpu.Memory(bufferHandle: counters.RowOf(row: 0, slot: 0).Buffer.BufferHandle);

                    // Model stale work from the preceding frame in both words of every slot, including inactive ones.
                    memory.AsSpan().Fill(value: 0x5A);
                    counters.RecordClear(commandBuffer: command, recorder: gpu.Services.Recorder, slot: 0);
                    for (var index = 0; (index < (stable + active)); index++) {
                        BinaryPrimitives.WriteUInt64LittleEndian(destination: memory.AsSpan(start: ((GpuWork.ShadowStepsFirstKind + index) * sizeof(ulong))),
                            value: (((ulong)(index + 1)) * 0x1_0000_0001UL));
                    }
                    counters.RecordCopy(commandBuffer: command, recorder: gpu.Services.Recorder, slot: 0);
                    var counts = new long[(2 * columns.Length)];

                    counters.AddTo(counts: counts, rowCount: 1, slot: 0);
                    for (var index = 0; (index < GpuWork.ShadowSlotCount); index++) {
                        var column = Array.IndexOf(array: columns, value: GpuWork.ShadowSteps[index]);

                        Assert.Equal(((index < (stable + active)) ? (((long)(index + 1)) * 0x1_0000_0001L) : 0L),
                            counts[(columns.Length + column)]);
                    }
                }
            }
        }
    }
}
