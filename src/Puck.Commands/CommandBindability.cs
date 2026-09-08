namespace Puck.Commands;

/// <summary>
/// Whether a command may be named as a binding destination — the axis that decides what a binding document can reach.
/// Principal honesty closes identity laundering (a bound verb acts as the seat that pressed it); this closes
/// reach-from-data, because an honest seat principal still makes some verbs dangerous to expose to a page anyone can
/// author.
/// </summary>
/// <remarks>
/// There is no default, deliberately: every registration declares one. A command whose bindability was never stated is
/// <see cref="Unspecified"/>, which the registry refuses by name at construction rather than admitting it under a
/// guessed value — the same loud-completeness shape the addon lane discriminant uses, and for the same reason (the
/// value decides reachability, so landing somewhere by omission is the one outcome worth an enum member to prevent).
/// <para>
/// The discriminator is gesture versus authoring: a chord that produces input is <see cref="Bindable"/> even when it
/// drives authoring, while a verb that names a document target (a world grant, a row mutation) is authority and
/// <see cref="Unbindable"/>. Drawing the line at "content-authoring verbs are authority" instead is the obvious reading
/// and is wrong — the chord-first interface binds gesture verbs that author content, so that line would fail a
/// shipped binding document at boot.
/// </para>
/// </remarks>
public enum CommandBindability : byte {
    /// <summary>Not an answer — the value a registration that declared nothing would carry. Always a composition-root
    /// error; never a bindability.</summary>
    Unspecified = 0,

    /// <summary>The command may be named by a binding page or chord row: the player-facing input surface — movement,
    /// look, action channels, and UI/roster navigation.</summary>
    Bindable = 1,

    /// <summary>The command may not be named by any binding document. Authority verbs live here: the world grant and
    /// mutation surface, screen/replay/feature control, and profile administration (never a content-authoring gesture
    /// verb — see the remarks on <see cref="CommandBindability"/>).</summary>
    Unbindable = 2,
}
