using System.Globalization;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;

using Puck.Abstractions.Counting;
using Puck.Overlays;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldInspectorDomainLawTests {
    [InlineData(0, "light from state.level: -1.25 requires positive; held 2.5")]
    [InlineData(1, "light from state.level: (-1.25, 4) requires positive; held (2.5, 5)")]
    [InlineData(2, "light from state.level: (-1.25, 4, 6) requires positive; held (2.5, 5, 7)")]
    [Theory]
    public void SpanAndConsoleFormatsPreserveTheSameCompleteDiagnostic(int dimensions, string expected) {
        var value = new WorldValueDomainDiagnostic("light", "state.level", -1.25, 2.5, "positive", "held",
            SecondValue: ((dimensions > 0) ? 4 : null), SecondUsed: ((dimensions > 0) ? 5 : null),
            ThirdValue: ((dimensions > 1) ? 6 : null), ThirdUsed: ((dimensions > 1) ? 7 : null));
        var formatter = Assert.IsAssignableFrom<ISpanFormattable>(@object: value);
        Span<char> chars = stackalloc char[256];

        Assert.True(condition: formatter.TryFormat(chars, out var written, default, new NumberFormatInfo { NumberDecimalSeparator = "," }));
        Assert.Equal(expected, new string(value: chars[..written]));
        Assert.Equal(expected, value.ToString());
        Assert.False(condition: formatter.TryFormat(chars[..2], out written, default, null));
        Assert.Equal(actual: written, expected: 0);
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public void ActiveDiagnosticsWrapWithoutAllocationAndDisappearOnRecovery(int dimensions) {
        var guard = new WorldValueDomainGuard();
        const string Field = "render.sky.layers[night].stars.angularRadius";

        if (dimensions == 0) { _ = guard.Scalar(Field, "clock night keys (state.radius)", -1, 2, WorldValueDomain.Positive); } else if (dimensions == 1) { _ = guard.OrderedPair(field: Field, high: 1, initialHigh: 2, initialLow: 1, low: 2, source: "clock night keys (state.radius)"); } else { _ = guard.Direction(Field, "clock night keys (state.radius)", Vector3.Zero, Vector3.UnitY); }
        var diagnostic = Assert.Single(collection: guard.Diagnostics);
        var expected = diagnostic.ToString();
        var text = new WorldInspectorText();
        var snapshot = new WorldInspectorSnapshot { ReloadError = "none" };

        void Format() {
            text.Format(snapshot: in snapshot);
            text.Diagnostics(diagnostics: guard.Diagnostics);
            text.Finish();
        }
        Format();
        Assert.False(condition: text.Refused);
        Assert.Contains(expected, new string(value: text.Text).Replace(comparisonType: StringComparison.Ordinal, newValue: "", oldValue: "\n"));
        foreach (var line in text.Text.Split(separator: '\n')) { Assert.True(condition: (text.Text[line].Length <= InspectorWriter.MaxLineChars)); }
        for (var index = 0; (index < 100); index++) { Format(); }
        Assert.Equal(0L, AllocationWindow.Least(() => { for (var index = 0; (index < 100); index++) { Format(); } }));
        if (dimensions == 0) { _ = guard.Scalar(Field, "clock night keys (state.radius)", 3, 2, WorldValueDomain.Positive); } else if (dimensions == 1) { _ = guard.OrderedPair(field: Field, high: 2, initialHigh: 2, initialLow: 1, low: 1, source: "clock night keys (state.radius)"); } else { _ = guard.Direction(Field, "clock night keys (state.radius)", Vector3.UnitX, Vector3.UnitY); }
        Assert.Empty(collection: guard.Diagnostics);
        Format();
        Assert.DoesNotContain(Field, new string(value: text.Text));
        Assert.Contains("hit=none", new string(value: text.Text));
    }
    [Fact]
    public void AnOversizedDiagnosticUsesTheExistingNamedReservationRefusal() {
        var text = new WorldInspectorText();
        var diagnostic = new WorldValueDomainDiagnostic("light", new string(c: 's', count: 4000), -1, 2, "positive", "held");

        text.Format(snapshot: new WorldInspectorSnapshot { ReloadError = "none" });
        text.Diagnostic(diagnostic: in diagnostic);
        text.Finish();
        Assert.True(condition: text.Refused);
        Assert.Equal("[world.inspect: editor refused text beyond its declared 32-line/96-column reservation]", new string(value: text.Text));
        text.Format(snapshot: new WorldInspectorSnapshot { ReloadError = "none" });
        text.Finish();
        Assert.False(condition: text.Refused);
    }
    [Fact]
    public void ResidencyIdentityReplacementAndRetirementNeverBorrowAnotherWorldsGuard() {
        using var files = new TemporaryDirectory();
        using var host = WorldBootHarness.Compose(files, WorldHostPresentation.Windowed,
            "tests/Puck.World.Canaries/editor-grid/fixture.world.json").Build();
        var probe = host.Services.GetRequiredService<IWorldEngineReadiness>();
        var type = probe.GetType();
        var primary = new WorldEnvironmentResolve();
        var first = new WorldEnvironmentResolve();
        var replacement = new WorldEnvironmentResolve();
        var pipelines = new GpuPassPipelineCache();
        var catalog = new SdfWorldPipelineCatalog(
            regionCopy: new GpuRegionCopyPass(kernel: new byte[] { 1 }, pipelines: pipelines),
            meshRaster: new SdfMeshRasterPass(fragment: new byte[] { 1 }, pipelines: pipelines, vertex: new byte[] { 1 }));
        using var boot = Residency(catalog: catalog, name: "boot");
        using var pane = Residency(catalog: catalog, name: "pane");
        using var next = Residency(catalog: catalog, name: "pane");

        type.GetProperty(name: "Residency")!.SetValue(obj: probe, value: boot);
        type.GetProperty(name: "PrimaryEnvironment")!.SetValue(obj: probe, value: primary);
        var lookup = type.GetMethod(name: "DomainsOf")!;

        WorldValueDomainGuard? Domains(SdfWorldResidency? residency) => ((WorldValueDomainGuard?)lookup.Invoke(obj: probe, parameters: [residency]));
        var register = type.GetMethod(name: "RegisterView")!;

        Assert.Null(@object: Domains(residency: null));
        Assert.Same(primary.Domains, Domains(residency: boot));
        Assert.Null(@object: Domains(residency: pane));
        register.Invoke(obj: probe, parameters: ["pane", pane.Work, pane.WorkLifetime, first]);
        Assert.Same(first.Domains, Domains(residency: pane));
        Assert.Null(@object: Domains(residency: next));
        register.Invoke(obj: probe, parameters: ["pane", next.Work, next.WorkLifetime, replacement]);
        Assert.Null(@object: Domains(residency: pane));
        Assert.Same(replacement.Domains, Domains(residency: next));
        type.GetMethod(name: "UnregisterView")!.Invoke(obj: probe, parameters: ["pane"]);
        Assert.Null(@object: Domains(residency: next));
        Assert.Same(primary.Domains, Domains(residency: boot));
    }

    internal static SdfWorldResidency Residency(string name, SdfWorldPipelineCatalog catalog) => new(
        pipelines: catalog, frameSource: new EmptyFrameSource(),
        kernels: new SdfKernelSet(bytecode: new ReadOnlyMemory<byte>[SdfKernelSet.Kernels.Count]), name: name, width: 32, height: 32);

    private sealed class EmptyFrameSource : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            new(Program: new SdfProgramBuilder().Build(), ProgramChanged: false, Views: [], Time: 0);
    }
}
