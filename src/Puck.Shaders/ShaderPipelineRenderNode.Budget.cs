using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// The node's memory account. Every byte count the node reports or refuses by comes from the plan through Footprint and
// GraphBytes, which mirror what Allocate and the build create: one image or buffer per frame slot of each storage the
// node owns (the images a graphics pass draws into among them), one geometry buffer per geometry pass and per
// fullscreen pass that reads the Position input, and one float-preview image per frame slot.
// ShaderPipelineRenderNode.Retirement.cs's LiveBytes counts the same kinds from a replaced graph's objects. The capture
// readback is the node's, not a graph's: it creates its staging buffer on the first capture, sized to the published
// surface, and OwnedBytes counts it from then on, so every later replacement's peak includes it. Like every other count
// here it is the logical size, not the backend's allocation, which may pad rows.
public sealed partial class ShaderPipelineRenderNode {
    private const ulong FullscreenVertexBytes = (FullscreenTriangle.StrideBytes * FullscreenTriangle.VertexCount);

    private ulong? m_budgetCapBytes;
    // The capture readback's staging buffer, counted like every other resource at its logical size: the published RGBA8
    // surface's width times its height times four. Zero until the first capture creates it.
    private ulong m_readbackBytes;

    /// <summary>Gets the budget every replacement's peak is refused against, in bytes: the device's
    /// <see cref="ShaderPipelineMemoryBudget.For"/>, lowered to <see cref="BudgetCapBytes"/> when that is smaller.</summary>
    public ulong BudgetBytes => ((m_budgetCapBytes is { } cap)
        ? Math.Min(
            val1: cap,
            val2: DeviceBudgetBytes
        )
        : DeviceBudgetBytes
    );
    /// <summary>Gets or sets a cap, in bytes, that lowers <see cref="BudgetBytes"/> below the device's budget, or
    /// <see langword="null"/> for none. A cap never raises the budget, and lowering it never frees the installed graph:
    /// it refuses the next replacement whose peak does not fit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero.</exception>
    public ulong? BudgetCapBytes {
        get => m_budgetCapBytes;
        set {
            if (value == 0UL) {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "A pipeline budget cap must be positive."
                );
            }

            m_budgetCapBytes = value;
        }
    }
    /// <summary>Gets the device's budget, in bytes: <see cref="ShaderPipelineMemoryBudget.For"/> over the device's
    /// memory profile.</summary>
    public ulong DeviceBudgetBytes => ShaderPipelineMemoryBudget.For(profile: m_device.MemoryProfile);
    /// <summary>Gets the installed graph's account: the bytes it owns, and the peak a reload of it at its extent and
    /// selection would reach from what the node owns now. Both are zero when no graph is installed.</summary>
    public ShaderPipelineMemoryAccount InstalledAccount => (((m_pipeline is { } pipeline) && m_ready)
        ? Account(
            extent: (m_width, m_height),
            plan: pipeline.Plan,
            preview: ((m_preview is { } preview)
                ? (preview.Width, preview.Height)
                : null
            )
        )
        : new ShaderPipelineMemoryAccount(
            BudgetBytes: BudgetBytes,
            PeakBytes: 0UL,
            SteadyBytes: 0UL
        )
    );

    // The bytes of the readback staging buffer that reads a published RGBA8 surface of an extent.
    private static ulong ReadbackBytes(uint width, uint height) => checked(((((ulong)width) * height) * 4UL));
    // The bytes of the float preview's targets, one per frame slot, at an extent.
    private static ulong PreviewBytes((uint Width, uint Height)? extent, uint inFlight) =>
        ((extent is { } preview)
            ? checked((((((ulong)preview.Width) * preview.Height) * 4UL) * inFlight))
            : 0UL
        );
    // One storage's extent and the bytes its instances occupy; a host-owned storage occupies nothing. An image is
    // allocated at its declared extent and format, which the planner requires, whichever passes write it.
    private static (uint Width, uint Height, ulong Bytes) Footprint(ShaderPipelinePlannedStorage storage, (uint Width, uint Height) frame, int count) {
        var declaration = storage.Declaration;

        if (declaration.Kind == ShaderPipelineResourceKind.Buffer) {
            return (0U, 0U, (declaration.IsExternal
                ? 0UL
                : checked(((declaration.SizeBytes ?? 0UL) * ((ulong)count)))
            ));
        }

        var extent = (declaration.Dimensions?.Resolve(
            frameHeight: frame.Height,
            frameWidth: frame.Width
        ) ?? frame);

        return (extent.Width, extent.Height, (declaration.IsExternal
            ? 0UL
            : checked((((((ulong)extent.Width) * extent.Height) * BytesPerPixel(format: declaration.Format)) * ((ulong)count)))
        ));
    }
    // The bytes a graph planned at a frame extent owns, before its float preview: every storage's instances, and the
    // geometry buffer of each pass that has one.
    private static ulong GraphBytes(ShaderPipelinePlan plan, (uint Width, uint Height) extent, uint inFlight) {
        var bytes = 0UL;

        foreach (var storage in plan.Storages) {
            bytes = checked((bytes + Footprint(
                count: ((int)inFlight),
                frame: extent,
                storage: storage
            ).Bytes));
        }
        foreach (var pass in plan.Passes) {
            if (pass.Declaration is { } declaration) {
                bytes = checked((bytes + GeometryBytes(pass: declaration)));
            }
        }

        return bytes;
    }
    // The bytes of a pass's one geometry buffer: a geometry pass's vertices and indices, the shared fullscreen triangle
    // of a fullscreen pass that reads the Position input, and nothing for any other pass.
    private static ulong GeometryBytes(ShaderPipelinePass pass) =>
        ((pass.Geometry is { } geometry)
            ? geometry.SizeBytes
            : (((pass.Kind == ShaderPipelineDocumentPassKind.Fullscreen) && (pass.Vertex == ShaderPipelineVertexInput.Position))
                ? FullscreenVertexBytes
                : 0UL));
    private static ulong BytesPerPixel(string? format) => ParseFormat(format: format) switch {
        GpuPixelFormat.R8G8B8A8Unorm or GpuPixelFormat.B8G8R8A8Unorm or GpuPixelFormat.D32Float => 4UL,
        GpuPixelFormat.R16G16B16A16Float => 8UL,
        GpuPixelFormat.R32G32B32A32Float => 16UL,
        _ => throw new InvalidDataException(message: $"Unsupported format '{format}'.")
    };
    // What installing a graph planned at an extent, with the float preview its selection needs, costs from what the node
    // owns now. History the graph carries from the installed one is moved, not allocated, so the peak holds its bytes
    // once.
    private ShaderPipelineMemoryAccount Account(ShaderPipelinePlan plan, (uint Width, uint Height) extent, (uint Width, uint Height)? preview) {
        var steady = checked((GraphBytes(
            extent: extent,
            inFlight: m_inFlight,
            plan: plan
        ) + PreviewBytes(
            extent: preview,
            inFlight: m_inFlight
        )));
        var carried = 0UL;

        foreach (var index in CarriedHistoryOf(
            extent: extent,
            plan: plan
        ).Keys) {
            carried = checked((carried + Footprint(
                count: ((int)m_inFlight),
                frame: extent,
                storage: plan.Storages[index]
            ).Bytes));
        }

        return new ShaderPipelineMemoryAccount(
            BudgetBytes: BudgetBytes,
            PeakBytes: checked(((OwnedBytes + steady) - carried)),
            SteadyBytes: steady
        );
    }
    // The history a graph planned at an extent carries from the installed graph, by the index of the storage that
    // receives it: a history storage of the same name, shape and resolved extent, whose instances were created with every
    // usage the graph gives it. A carried storage allocates nothing.
    private Dictionary<int, CarriedHistory> CarriedHistoryOf(ShaderPipelinePlan plan, (uint Width, uint Height) extent) {
        var carried = new Dictionary<int, CarriedHistory>();

        if (
            (m_pipeline is null) ||
            !m_ready
        ) {
            return carried;
        }

        foreach (var storage in plan.Storages) {
            if (!storage.History) {
                continue;
            }

            foreach (var old in m_resources) {
                if (
                    !string.Equals(
                        a: old.Spec.Name,
                        b: storage.Name,
                        comparisonType: StringComparison.Ordinal
                    ) ||
                    !old.History ||
                    !CompatibleHistory(
                        current: storage.Declaration,
                        extent: extent,
                        old: old.Spec,
                        previousExtent: (m_width, m_height)
                    )
                ) {
                    continue;
                }

                var usage = UsageOf(
                    plan: plan,
                    storage: storage
                );

                if (
                    ((old.Images is { } images) && images.All(predicate: image => ((image is not null) && ((image.Usage & usage) == usage)))) ||
                    (old.Buffers is not null)
                ) {
                    carried.Add(
                        key: storage.Index,
                        value: new CarriedHistory(Old: old)
                    );
                }
            }
        }

        return carried;
    }
    // The usages every instance of an image storage is created with. A depth storage is only a depth attachment. A color
    // storage is sampled and a storage image, which compute writes, zero clears and a publication in General need, and a
    // color attachment too when a graphics pass writes one of its versions.
    private static GpuImageUsage UsageOf(ShaderPipelinePlan plan, ShaderPipelinePlannedStorage storage) =>
        ((storage.Declaration.Kind == ShaderPipelineResourceKind.Depth)
            ? GpuImageUsage.DepthAttachment
            : GpuImageUsage.Sampled | GpuImageUsage.Storage | (plan.Passes.Any(predicate: pass => (
                (pass.Declaration?.IsGraphics == true) &&
                pass.Outputs.Any(predicate: output => storage.Versions.Contains(value: output.Name))
            ))
                ? GpuImageUsage.ColorAttachment
                : GpuImageUsage.None));

    // History an installing graph takes from the installed one: the storage whose instances move into it.
    private readonly record struct CarriedHistory(RuntimeResource Old);

    private ShaderPipelineResourceStatus StatusOf(RuntimeResource resource) {
        var (width, height, bytes) = Footprint(
            count: resource.Count,
            frame: (m_width, m_height),
            storage: resource.Storage
        );

        return new ShaderPipelineResourceStatus(
            resource.Spec.Name,
            resource.Spec.Kind,
            bytes,
            width,
            height,
            resource.Spec.IsExternal,
            resource.History
        );
    }

    /// <summary>Accounts installing a compiled pipeline now: at the requested extent, with the float preview the current
    /// selection needs, from everything the node owns. It is the account a candidate is refused by.</summary>
    /// <param name="pipeline">The compiled pipeline.</param>
    /// <returns>The pipeline's steady-state bytes and the replacement peak, against <see cref="BudgetBytes"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pipeline"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The pipeline publishes no output the selection or its default names, or
    /// declares an unsupported format.</exception>
    public ShaderPipelineMemoryAccount Account(CompiledShaderPipeline pipeline) {
        ArgumentNullException.ThrowIfNull(pipeline);

        var extent = (m_requestedWidth, m_requestedHeight);

        return Account(
            extent: extent,
            plan: pipeline.Plan,
            preview: PreviewFor(
                extent: extent,
                plan: pipeline.Plan
            )
        );
    }
}
