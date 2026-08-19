using Puck.Commands;

namespace Puck.World;

/// <summary>
/// Injection seam for the binding-validation work that needs the engine's PHYSICAL device vocabulary
/// (<c>Puck.Input</c>) — <c>Puck.World.Schema</c> must not reference <c>Puck.Input</c> at all (the architecture gate's
/// structural denial), while <see cref="WorldDefinitionValidator"/>
/// still needs to lint a composed binding overlay against the live command/channel vocabulary. The composition
/// root (<c>Puck.World</c>) wires the hook with a
/// <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> method, so they are installed the
/// instant the process's entry assembly loads — before <c>Main</c>, before the DI container, before any offline
/// replay rehydration or pre-container boot parse the validators run during. The one production consumer is
/// Puck.World.exe; <c>tests/Puck.World.Tests</c> is the sole other caller that loads Puck.World.Schema without
/// Puck.World, and wires a minimal stand-in of its own (<c>TestHookInstaller</c>) for exactly this reason.
/// </summary>
public static class BindingVocabularyHook {
    /// <summary>Gets the hook that lints a composed binding document against the command vocabulary and against a
    /// channel table the CALLER supplies. Genuinely optional — mirrors <c>WorldAffordances.Validate</c>'s own
    /// absent-tolerant contract (a <see langword="null"/> hook, or a hook that itself no-ops before the composition
    /// root finishes building its registry, skips the command half only; structural validation never depends on
    /// this).</summary>
    /// <remarks><para>The channel table is a parameter rather than something the hook resolves for itself, and that is
    /// load-bearing: channels are declared per world document, so the only table that can honestly judge a document's
    /// binding overlays is the one compiled from that same document. A hook resolving its own table would answer for
    /// whichever world happened to install one — refusing a self-consistent document under one boot world and
    /// admitting a self-inconsistent one under another.</para>
    /// <para>The seat-mode family list is the identical parameter for the identical reason, extended to the
    /// document's AUTHORED per-seat mode families (<see cref="WorldSeatModeFamily"/>): a <c>contexts</c> row naming
    /// one is only checkable against that same document's own declared families, never a process-global or another
    /// world's.</para></remarks>
    public static Action<BindingProfileDocument, WorldChannelTable, IReadOnlyList<WorldSeatModeFamily>, List<string>>? VocabularyCheck { get; set; }
}
