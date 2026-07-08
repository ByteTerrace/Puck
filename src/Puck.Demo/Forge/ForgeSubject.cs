using Puck.Abstractions.Gpu;
using Puck.Demo.Overworld;
using Puck.Hosting;

namespace Puck.Demo.Forge;

/// <summary>
/// The SUBJECT-NEUTRAL author→forge→hot-swap seam: one abstraction the AVATAR, the TUNE, and the SDF-ART SCENE all
/// register on, so authoring any of them in-session forges a cart and hot-swaps it into a cabinet through ONE
/// mechanism, not three copies. A subject binds its cabinet cart <see cref="CartType"/>, whether its bake
/// <see cref="NeedsGpu"/> (the avatar/scene rasterize on the live device; the tune compiler is pure-CPU — its forge
/// must NEVER be gated behind device resolution), a human <see cref="Kind"/> label, and a <see cref="Forge"/> that
/// pulls its OWN live document (the creator scene, or the tracker's working tune) and produces the 32 KiB cart bytes.
/// <para>
/// The registry (<see cref="ForgeRegistry"/>) is what the render node's commit / lazy-forge / reload paths ITERATE
/// generically — replacing the avatar-hardcoded <c>MarkAvatarCabinetsForReload</c> and the
/// <c>wantType == AvatarCartType</c> lazy-forge branch with a registry-driven "any forged type" notion. Every subject
/// forges a cart LAZILY the first time a cabinet wants it (like the avatar always has), so a forged type is
/// Cycle-reachable but is NEVER a cabinet's boot default (which would need a forge before one exists).
/// </para>
/// </summary>
/// <param name="CartType">The <see cref="OverworldWorld"/> cart type this subject forges into (its slot in the render
/// node's ROM table).</param>
/// <param name="Kind">A short human label for the subject (narration + the <c>forge</c> verb's subject word).</param>
/// <param name="NeedsGpu">Whether the bake needs the live GPU device (avatar/scene true; tune false).</param>
/// <param name="Forge">Produces the cart bytes from the subject's own live document, given the resolved forge context
/// (the GPU device/compute services are non-null only when <see cref="NeedsGpu"/> demanded — and succeeded —
/// resolution). Returns null on a narrated failure (a bad bake must never take the demo down).</param>
internal sealed record ForgeSubject(int CartType, string Kind, bool NeedsGpu, Func<ForgeContext, byte[]?> Forge);

/// <summary>
/// The resolved context a <see cref="ForgeSubject.Forge"/> runs in: the current frame context (its host resolves the
/// live GPU device), the application services, and the live overworld frame source (the authoring composition point
/// every subject reads its document from). <see cref="Device"/>/<see cref="Gpu"/> are populated ONLY for a
/// GPU-needing subject when resolution SUCCEEDED — a pure-CPU subject (the tune) is invoked without touching device
/// resolution at all, so its forge is never gated behind a device the room may not have.
/// </summary>
internal readonly ref struct ForgeContext {
    /// <summary>Initializes the forge context.</summary>
    /// <param name="frame">The current frame context.</param>
    /// <param name="services">The application services.</param>
    /// <param name="frameSource">The live overworld frame source (the document composition point).</param>
    /// <param name="device">The resolved GPU device (null for a pure-CPU subject).</param>
    /// <param name="gpu">The resolved compute services (null for a pure-CPU subject).</param>
    public ForgeContext(in FrameContext frame, IServiceProvider services, OverworldFrameSource frameSource, IGpuDeviceContext? device, IGpuComputeServices? gpu) {
        Frame = frame;
        Services = services;
        FrameSource = frameSource;
        Device = device;
        Gpu = gpu;
    }

    /// <summary>The current frame context.</summary>
    public FrameContext Frame { get; }

    /// <summary>The application services.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The live overworld frame source (the document composition point).</summary>
    public OverworldFrameSource FrameSource { get; }

    /// <summary>The resolved GPU device, or null for a pure-CPU subject.</summary>
    public IGpuDeviceContext? Device { get; }

    /// <summary>The resolved compute services, or null for a pure-CPU subject.</summary>
    public IGpuComputeServices? Gpu { get; }
}

/// <summary>
/// The registry of <see cref="ForgeSubject"/>s the in-session forge iterates. Static + immutable: the three subjects
/// are declared once (in <see cref="ForgeCommands"/>) and looked up by cart type. The render node reaches this ONLY
/// through <see cref="ForgeCommands"/>'s primitive-typed forwarders — never as a typed field on
/// <c>OverworldRenderNode</c>, which is at its analyzer coupling ceiling.
/// </summary>
internal sealed class ForgeRegistry {
    private readonly IReadOnlyDictionary<int, ForgeSubject> m_byType;

    /// <summary>Initializes the registry from the subject list, keyed by cart type.</summary>
    /// <param name="subjects">The registered subjects (one per forgeable cart type).</param>
    public ForgeRegistry(IReadOnlyList<ForgeSubject> subjects) {
        ArgumentNullException.ThrowIfNull(subjects);

        var byType = new Dictionary<int, ForgeSubject>(capacity: subjects.Count);

        foreach (var subject in subjects) {
            byType[subject.CartType] = subject;
        }

        m_byType = byType;
    }

    /// <summary>Whether <paramref name="cartType"/> is a FORGED (lazily-baked) cart type — the registry-driven
    /// replacement for the old <c>wantType == AvatarCartType</c> test. A forged type must be lazy-forged / Cycle-reached,
    /// never a cabinet boot default.</summary>
    /// <param name="cartType">The cart type to test.</param>
    /// <returns>Whether a subject forges this type.</returns>
    public bool IsForgedType(int cartType) => m_byType.ContainsKey(key: cartType);

    /// <summary>Looks up the subject for a cart type, or null when the type is not forged.</summary>
    /// <param name="cartType">The cart type.</param>
    /// <returns>The subject, or null.</returns>
    public ForgeSubject? Find(int cartType) => (m_byType.TryGetValue(key: cartType, value: out var subject) ? subject : null);
}
