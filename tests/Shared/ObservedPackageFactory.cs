using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.Testing;

/// <summary>Wraps a package factory so a law can hold what its recorder is handed and what it records itself: the layout
/// of every input and output image each recording receives, and the barriers the device counted while the package's own
/// <see cref="IRenderGraphPackageRecorder.Record"/> ran.</summary>
/// <param name="inner">The package under law.</param>
/// <param name="barriers">Reads the device's running barrier count.</param>
internal sealed class ObservedPackageFactory(IRenderGraphPackageFactory inner, Func<int> barriers) : IRenderGraphPackageFactory {
    private readonly Func<int> m_barriers = barriers;

    /// <summary>Gets the number of recordings observed.</summary>
    public int Records { get; private set; }
    /// <summary>Gets the barriers counted while the package recorded, over every recording.</summary>
    public int PackageBarriers { get; private set; }
    /// <summary>Gets the layouts of the last recording's first input and first output images.</summary>
    public (GpuImageLayout Input, GpuImageLayout Output) Layouts { get; private set; }
    /// <summary>Gets the outcome the last recording returned.</summary>
    public RenderGraphPackageOutcome Outcome { get; private set; }

    /// <inheritdoc/>
    public IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => inner.Build(
        cancellationToken: cancellationToken,
        context: context
    );
    /// <inheritdoc/>
    public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(
        inner: inner.Create(
            built: built,
            context: context,
            groups: groups
        ),
        owner: this
    );
    /// <inheritdoc/>
    public IReadOnlyList<RenderGraphPackageRegion> Regions(RenderGraphPackageRecorderContext context) => inner.Regions(context: context);

    private sealed class Recorder(IRenderGraphPackageRecorder inner, ObservedPackageFactory owner) : IRenderGraphPackageRecorder {
        public void Dispose() => inner.Dispose();
        public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
            var before = owner.m_barriers();
            var outcome = inner.Record(recording: in recording);

            owner.PackageBarriers += (owner.m_barriers() - before);
            owner.Records++;
            owner.Layouts = (recording.Inputs[0].Image.Layout, recording.Outputs[0].Image.Layout);
            owner.Outcome = outcome;

            return outcome;
        }
    }
}
