using Puck.Commands;

namespace Puck.World;

/// <summary>
/// Injection seam for the binding-validation work that needs the engine's PHYSICAL device vocabulary
/// (<c>Puck.Input</c>) — <c>Puck.World.Schema</c> must not reference <c>Puck.Input</c> at all (the architecture gate's
/// structural denial), while <see cref="WorldDefinitionValidator"/>
/// still needs to compose a candidate binding overlay against the real engine-default first layer and lint it
/// against the live command/channel vocabulary. The composition root (<c>Puck.World</c>) wires both hooks with a
/// <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> method, so they are installed the
/// instant the process's entry assembly loads — before <c>Main</c>, before the DI container, before any offline
/// replay rehydration or pre-container boot parse the validators run during. The one production consumer is
/// Puck.World.exe; <c>tests/Puck.World.Tests</c> is the sole other caller that loads Puck.World.Schema without
/// Puck.World, and wires a minimal stand-in of its own (<c>TestHookInstaller</c>) for exactly this reason.
/// </summary>
public static class BindingVocabularyHook {
    /// <summary>Gets the hook that builds the engine-default binding document — the mandatory first compose layer. Not
    /// nullable in practice (the composition root's module initializer installs it unconditionally), but declared
    /// nullable so a caller before install fails with a clear message rather than a bare
    /// <see cref="NullReferenceException"/>.</summary>
    public static Func<BindingProfileDocument>? DefaultDocument { get; set; }
    /// <summary>Gets the hook that lints a composed binding document against the command vocabulary and against a
    /// channel table the CALLER supplies. Genuinely optional — mirrors <c>WorldAffordances.Validate</c>'s own
    /// absent-tolerant contract (a <see langword="null"/> hook, or a hook that itself no-ops before the composition
    /// root finishes building its registry, skips the command half only; structural validation never depends on
    /// this).</summary>
    /// <remarks>The channel table is a parameter rather than something the hook resolves for itself, and that is
    /// load-bearing: channels are declared per world document, so the only table that can honestly judge a document's
    /// binding overlays is the one compiled from that same document. A hook resolving its own table would answer for
    /// whichever world happened to install one — refusing a self-consistent document under one boot world and
    /// admitting a self-inconsistent one under another.</remarks>
    public static Action<BindingProfileDocument, WorldChannelTable, List<string>>? VocabularyCheck { get; set; }

    /// <summary>Builds the engine-default binding document through <see cref="DefaultDocument"/>.</summary>
    /// <returns>The engine-default document.</returns>
    /// <exception cref="InvalidOperationException"><see cref="DefaultDocument"/> was never installed.</exception>
    public static BindingProfileDocument BuildDefaultDocument() {
        return ((DefaultDocument is { } factory)
            ? factory()
            : throw new InvalidOperationException(message: "BindingVocabularyHook.DefaultDocument was never installed — Puck.World's module initializer should have wired it before any validator ran.")
        );
    }
}
