namespace Puck.World;

/// <summary>
/// Injection seam for the extension-key validation that needs the host's REGISTERED vocabulary —
/// <c>Puck.World.Schema</c> must not reference the concrete extension assemblies (<c>Puck.HumbleGamingBrick</c>,
/// <c>Puck.AdvancedGamingBrick</c>, and whatever registers against a future extension point), while
/// <see cref="WorldDefinitionValidator"/> still must refuse a document naming an unregistered key BY NAME, at load,
/// instead of letting it boot and fault a slot at runtime. The composition root (<c>Puck.World</c>) wires this with a
/// <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> method (see
/// <c>WorldDataHookInstaller</c>) — installed before <c>Main</c>, before the DI container, before any pre-container
/// document parse the validators run during.
/// </summary>
/// <remarks>
/// <para><b>REQUIRED, not optional — deliberately the opposite of
/// <see cref="BindingVocabularyHook.VocabularyCheck"/>.</b> That check is genuinely absent-tolerant because what sits
/// behind it (<c>WorldAffordances.Validate</c>) is itself a no-op until the composition root finishes building its
/// command registry: a null there means "too early", a real state with a defined answer. Nothing behind THIS hook has
/// such a window — the registered extension set is a static list available at module-initializer time — so absence can
/// only mean no composition root installed it. Skipping the check then would validate CLEAN a document naming a key
/// the host cannot run: a check that passes because it never ran. Hence nullable only so a caller before install gets
/// a named failure instead of a bare <see cref="NullReferenceException"/>, read exclusively through
/// <see cref="IsRegisteredScreenMachineEngine"/>, which throws.</para>
/// <para>That throw is an <see cref="InvalidOperationException"/> so it lands where every other validator failure
/// lands: <see cref="WorldDefinitionValidator.TryValidate"/> collapses it into a refusal reason and
/// <see cref="WorldDefinitionFileSource.TryLoad"/> into a refused load — an uninstalled hook REFUSES the document,
/// loudly and by cause, rather than taking a tick down.</para>
/// </remarks>
public static class WorldExtensionVocabularyHook {
    /// <summary>Answers whether a key names a registered screen-machine engine
    /// (<see cref="Abstractions.Machines.IScreenMachineEngine"/>). Installed once by the composition root's module
    /// initializer; read through <see cref="IsRegisteredScreenMachineEngine"/>, never directly.</summary>
    public static Func<string, bool>? ScreenMachineEngineCheck { get; set; }
    /// <summary>Answers whether a key names a shipped post-render extension (a shader set found by its
    /// <c>puck.shader.v1</c> manifest — this project cannot reference <c>Puck.Shaders</c>, so the catalog never
    /// appears here). Installed once by the composition root's module initializer, the same required,
    /// never-absent-tolerant shape as <see cref="ScreenMachineEngineCheck"/>: the shipped set is a directory scan
    /// available at module-initializer time, so absence can only mean no composition root installed it. Read
    /// through <see cref="IsRegisteredPostRenderExtension"/>, never directly.</summary>
    public static Func<string, bool>? PostRenderExtensionCheck { get; set; }

    /// <summary>Determines whether <paramref name="engineId"/> names a registered screen-machine engine.</summary>
    /// <param name="engineId">The candidate engine id.</param>
    /// <returns><see langword="true"/> when an engine is registered under that id.</returns>
    /// <exception cref="InvalidOperationException"><see cref="ScreenMachineEngineCheck"/> was never installed. The
    /// check is never skipped: skipping it would pass a document no host can run.</exception>
    public static bool IsRegisteredScreenMachineEngine(string engineId) {
        return ((ScreenMachineEngineCheck is { } check)
            ? check(engineId)
            : throw new InvalidOperationException(message: "WorldExtensionVocabularyHook.ScreenMachineEngineCheck was never installed — Puck.World's module initializer should have wired it before any validator ran; a screen-machine engine key cannot be checked here, and is never assumed valid.")
        );
    }
    /// <summary>Determines whether <paramref name="extensionId"/> names a registered post-render extension.</summary>
    /// <param name="extensionId">The candidate extension id.</param>
    /// <returns><see langword="true"/> when an extension is registered under that id.</returns>
    /// <exception cref="InvalidOperationException"><see cref="PostRenderExtensionCheck"/> was never installed. The
    /// check is never skipped: skipping it would pass a document no host can run.</exception>
    public static bool IsRegisteredPostRenderExtension(string extensionId) {
        return ((PostRenderExtensionCheck is { } check)
            ? check(extensionId)
            : throw new InvalidOperationException(message: "WorldExtensionVocabularyHook.PostRenderExtensionCheck was never installed — Puck.World's module initializer should have wired it before any validator ran; a post-render extension key cannot be checked here, and is never assumed valid.")
        );
    }
}
