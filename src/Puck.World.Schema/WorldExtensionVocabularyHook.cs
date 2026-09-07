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
/// such a window — <see langword="null"/> installed is a distinct, always-available answer of its own (see below) —
/// so an UNINSTALLED hook (the property itself never set) can only mean no composition root ran at all. Skipping the
/// check then would validate CLEAN a document naming a key the host cannot run: a check that passes because it never
/// ran. Hence nullable only so a caller before install gets a named failure instead of a bare
/// <see cref="NullReferenceException"/>, read exclusively through <see cref="IsRegisteredScreenMachineEngine"/>,
/// which throws.</para>
/// <para>That throw is an <see cref="InvalidOperationException"/> so it lands where every other validator failure
/// lands: <see cref="WorldDefinitionValidator.TryValidate"/> collapses it into a refusal reason and
/// <see cref="WorldDefinitionFileSource.TryLoad"/> into a refused load — an uninstalled hook REFUSES the document,
/// loudly and by cause, rather than taking a tick down.</para>
/// <para><b>Three answers, not two.</b> Each predicate answers <see langword="bool"/>? — <see langword="true"/> (the
/// key is registered), <see langword="false"/> (the key is not registered — REFUSE the document by name), or
/// <see langword="null"/> (this host installed the hook but carries no catalog for that vocabulary at all — the
/// answer is DEFERRED to the deployment target that does, never treated as a refusal or a silent pass). The desktop
/// client and the silo always answer <see langword="true"/>/<see langword="false"/> — they carry the real catalogs.
/// <c>Puck.World.Browser</c> answers <see langword="null"/> for every one of these — a browser-wasm build carries no
/// emulator core of its own (Architecture.props' exact-closure profile denies <c>Puck.World.Protocol</c> and every
/// extension-owning assembly), so "no catalog to check against" is the honest answer, distinct from "checked and
/// refused". <see cref="WorldDefinitionValidator"/> routes a <see langword="null"/> answer into the deferred
/// collection, exactly as <c>WorldDefinitionValidator.Admission.cs</c>'s ECDsa key-material deferral does, and
/// continues validating rather than refusing.</para>
/// </remarks>
public static class WorldExtensionVocabularyHook {
    /// <summary>Answers whether a key names a shipped post-render extension (a shader set found by its
    /// <c>puck.shader.v1</c> manifest — this project cannot reference <c>Puck.Shaders</c>, so the catalog never
    /// appears here), or <see langword="null"/> when the installing host carries no post-render extension catalog at
    /// all. Installed once by the composition root's module initializer, the same required, never-absent-tolerant
    /// shape as <see cref="ScreenMachineEngineCheck"/>: the shipped set is a directory scan available at
    /// module-initializer time on a host that has one, so an UNSET property can only mean no composition root
    /// installed it. Read through <see cref="IsRegisteredPostRenderExtension"/>, never directly.</summary>
    public static Func<string, bool?>? PostRenderExtensionCheck { get; set; }
    /// <summary>Answers whether a key names a registered screen-machine engine
    /// (<see cref="Abstractions.Machines.IScreenMachineEngine"/>), or <see langword="null"/> when the installing host
    /// carries no screen-machine engine catalog at all. Installed once by the composition root's module initializer;
    /// read through <see cref="IsRegisteredScreenMachineEngine"/>, never directly.</summary>
    public static Func<string, bool?>? ScreenMachineEngineCheck { get; set; }
    /// <summary>Answers whether a registered screen-machine engine compiles an authored <c>puck.cartridge.v1</c>
    /// document at bind (<c>Puck.World.WorldScreenMachineEngines.CompilesCartridges</c> in a real root), or
    /// <see langword="null"/> when the installing host carries no screen-machine engine catalog at all. Installed
    /// beside <see cref="ScreenMachineEngineCheck"/>, the same required shape; read through
    /// <see cref="IsCartridgeCompilingScreenMachineEngine"/>, never directly.</summary>
    public static Func<string, bool?>? ScreenMachineCartridgeCheck { get; set; }

    /// <summary>Determines whether <paramref name="extensionId"/> names a registered post-render extension.</summary>
    /// <param name="extensionId">The candidate extension id.</param>
    /// <returns><see langword="true"/> when an extension is registered under that id; <see langword="false"/> when
    /// the installing host has a catalog and the id is not in it; <see langword="null"/> when the installing host
    /// carries no post-render extension catalog at all (the answer is deferred).</returns>
    /// <exception cref="InvalidOperationException"><see cref="PostRenderExtensionCheck"/> was never installed. The
    /// check is never skipped: skipping it would pass a document no host can run.</exception>
    public static bool? IsRegisteredPostRenderExtension(string extensionId) {
        return ((PostRenderExtensionCheck is { } check)
            ? check(extensionId)
            : throw new InvalidOperationException(message: "WorldExtensionVocabularyHook.PostRenderExtensionCheck was never installed — Puck.World's module initializer should have wired it before any validator ran; a post-render extension key cannot be checked here, and is never assumed valid.")
        );
    }
    /// <summary>Determines whether <paramref name="engineId"/> names a registered screen-machine engine that
    /// compiles cartridge documents (<see cref="WorldScreenSource.Machine.CartridgeDocumentSuffix"/>).</summary>
    /// <param name="engineId">The candidate engine id.</param>
    /// <returns><see langword="true"/> when the engine compiles a cartridge document at bind; <see langword="false"/>
    /// when the installing host has a catalog and the engine does not; <see langword="null"/> when the installing
    /// host carries no screen-machine engine catalog at all (the answer is deferred).</returns>
    /// <exception cref="InvalidOperationException"><see cref="ScreenMachineCartridgeCheck"/> was never installed.
    /// The check is never skipped: skipping it would pass a document no host can boot.</exception>
    public static bool? IsCartridgeCompilingScreenMachineEngine(string engineId) {
        return ((ScreenMachineCartridgeCheck is { } check)
            ? check(engineId)
            : throw new InvalidOperationException(message: "WorldExtensionVocabularyHook.ScreenMachineCartridgeCheck was never installed — Puck.World's module initializer should have wired it before any validator ran; a cartridge document path cannot be checked here, and is never assumed valid.")
        );
    }
    /// <summary>Determines whether <paramref name="engineId"/> names a registered screen-machine engine.</summary>
    /// <param name="engineId">The candidate engine id.</param>
    /// <returns><see langword="true"/> when an engine is registered under that id; <see langword="false"/> when the
    /// installing host has a catalog and the id is not in it; <see langword="null"/> when the installing host
    /// carries no screen-machine engine catalog at all (the answer is deferred).</returns>
    /// <exception cref="InvalidOperationException"><see cref="ScreenMachineEngineCheck"/> was never installed. The
    /// check is never skipped: skipping it would pass a document no host can run.</exception>
    public static bool? IsRegisteredScreenMachineEngine(string engineId) {
        return ((ScreenMachineEngineCheck is { } check)
            ? check(engineId)
            : throw new InvalidOperationException(message: "WorldExtensionVocabularyHook.ScreenMachineEngineCheck was never installed — Puck.World's module initializer should have wired it before any validator ran; a screen-machine engine key cannot be checked here, and is never assumed valid.")
        );
    }
}
