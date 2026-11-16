using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>
/// What a render-graph package's build produces when its pass is one pipeline of the device's pass-pipeline cache: the
/// lease on that pipeline, held from the build until the package's recorder is disposed. A package whose pass is one
/// cached pipeline builds it with <see cref="AcquireAsync"/>, hands it back as its <see cref="IDisposable"/> build, and
/// recovers it in <c>Create</c> with <see cref="Of"/>.
/// </summary>
public sealed class PassPipelineBuild : IDisposable {
    private readonly GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> m_lease;
    private readonly string m_package;

    private PassPipelineBuild(GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> lease, string package) {
        m_lease = lease;
        m_package = package;
        Pipeline = lease.Current!;
    }

    /// <summary>Gets the pipeline the build leased, whose handles a recording binds.</summary>
    public GpuPassPipeline Pipeline { get; }
    /// <summary>Gets the render pass a graphics pipeline draws in; the build of a compute pass has none.</summary>
    public IGpuRenderPass RenderPass => Pipeline.RenderPass!;

    /// <summary>Leases a pipeline from the context's cache and waits for it to build.</summary>
    /// <param name="context">The recorder context, whose device and pipeline cache the lease is on.</param>
    /// <param name="key">The pipeline's key.</param>
    /// <param name="package">The package's name, which <see cref="Of"/> checks, so a package handed another package's build
    /// refuses it.</param>
    /// <param name="cancellationToken">The token that cancels the wait; the lease is released when it does or when the
    /// build fails.</param>
    /// <returns>The build, which the caller disposes to release the lease.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static async ValueTask<PassPipelineBuild> AcquireAsync(RenderGraphPackageRecorderContext context, GpuPassPipelineKey key, string package, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var lease = context.Pipelines.Acquire(
            device: context.Device,
            key: key
        );

        try {
            _ = await lease.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        } catch {
            lease.Release();

            throw;
        }

        return new PassPipelineBuild(lease: lease, package: package);
    }
    /// <summary>Recovers the build a package's <c>BuildAsync</c> returned.</summary>
    /// <param name="built">The build the graph hands <c>Create</c>.</param>
    /// <param name="package">The package's name, as it passed to <see cref="AcquireAsync"/>.</param>
    /// <returns>The build.</returns>
    /// <exception cref="ArgumentException"><paramref name="built"/> is not a build of <paramref name="package"/>; it is
    /// disposed first.</exception>
    public static PassPipelineBuild Of(IDisposable? built, string package) {
        if (built is not PassPipelineBuild build || (build.m_package != package)) {
            built?.Dispose();

            throw new ArgumentException(
                message: $"Package '{package}' was handed another package's build.",
                paramName: nameof(built)
            );
        }

        return build;
    }

    /// <summary>Releases the lease.</summary>
    public void Dispose() =>
        m_lease.Release();
}
