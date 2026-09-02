namespace Puck.Commands;

/// <summary>
/// The live vocabularies <see cref="BindingVocabularyCheck.Validate"/> resolves a binding document's references
/// through — the host's command registry, its physical control catalog, and the declaring world's channel table.
/// </summary>
/// <remarks>Every lookup is INDEPENDENTLY optional, and a caller missing one still gets the checks the others
/// support: a caller with no registry leaves <paramref name="Command"/> null and keeps the source and channel
/// checks; a caller with no channel table leaves <paramref name="Channel"/> null and keeps the command checks.
/// Nothing here couples one half's absence to the other's, and <see cref="None"/> — every lookup absent — still
/// runs the shape checks that need no vocabulary at all.</remarks>
/// <param name="Command">Resolves a command name (or alias) to its declared facts, answering <see langword="null"/>
/// when no such command is registered — typically <see cref="CommandRegistry.TryGetMetadata"/>. Leave
/// <see langword="null"/> to skip the command half entirely (a caller with no registry — an offline rehydrator, a
/// pre-container boot parse).</param>
/// <param name="SourceKind">Resolves a physical source id to its declared value kind, or <see langword="null"/>
/// when the source is unknown to the caller's catalog — which is itself a refusal ("names unknown control"), so
/// this lookup doubles as the physical vocabulary's existence check. Leave <see langword="null"/> to skip source
/// resolution entirely (a caller with no control catalog).</param>
/// <param name="Channel">Resolves a declared channel name (a second, world-owned vocabulary a binding destination
/// may name instead of a command — see <see cref="BindingPageEntryDefinition.Channel"/>), or
/// <see langword="null"/> to skip channel-name resolution entirely (a caller with no channel table). A name this
/// resolves <see langword="false"/> for gets the channel twin of the "names no registered command"
/// refusal.</param>
/// <param name="ChannelBinary">Resolves a declared channel name to whether its shape is binary, or
/// <see langword="null"/> to skip the shape check entirely (a caller with no channel table). A binary channel's
/// scale is always the default (<c>+1</c>, or an omitted <see cref="BindingPageEntryDefinition.Scale"/>) —
/// <see cref="BindingPageEntryDefinition.Scale"/>'s own doc names this rule; the check is where it is enforced.
/// Only consulted for a channel <paramref name="Channel"/> has already confirmed exists.</param>
/// <param name="SourceAddressable">Optionally identifies declared sources that cannot be authored as binding
/// controls despite carrying a known value kind, such as the text-payload source.</param>
public sealed record BindingVocabularyLookups(
    Func<string, CommandMetadata?>? Command = null,
    Func<string, CommandValueKind?>? SourceKind = null,
    Func<ChannelRef, bool>? Channel = null,
    Func<ChannelRef, bool>? ChannelBinary = null,
    Func<string, bool>? SourceAddressable = null
) {
    /// <summary>Gets the lookups a caller with no vocabularies at all supplies — every lookup absent, so only the
    /// checks that need no vocabulary run.</summary>
    public static BindingVocabularyLookups None { get; } = new();
}
