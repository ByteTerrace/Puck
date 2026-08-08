namespace Puck.Scripting;

/// <summary>One decoded entry of a guest's declared channel-name table, resolved once against the host's channel
/// resolver at handshake and cached for the instance's lifetime. Resolution failure is never a mount fault (see
/// <see cref="IAddonChannelResolver"/>): a name the host table lacks decodes as <see cref="Resolved"/> = <see
/// langword="false"/> and <see cref="Ordinal"/> = <c>-1</c>, a sentinel — a well-formed but currently-inert
/// declaration, reported once at mount rather than refused.</summary>
/// <param name="Name">The declared channel name text, decoded from the guest's name table.</param>
/// <param name="Resolved">Whether <paramref name="Name"/> resolved against the host's channel table.</param>
/// <param name="Ordinal">The host-owned channel ordinal <paramref name="Name"/> resolved to, or <c>-1</c> when
/// <paramref name="Resolved"/> is <see langword="false"/>.</param>
/// <param name="Shape">The value shape the host table declares for <paramref name="Ordinal"/>; <c>default</c>
/// when <paramref name="Resolved"/> is <see langword="false"/> — meaningless, never consulted.</param>
public readonly record struct AddonChannelBinding(string Name, bool Resolved, int Ordinal, AddonChannelValueShape Shape);
