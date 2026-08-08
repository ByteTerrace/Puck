namespace Puck.World;

/// <summary>Whether a cataloged refusal is a PROTOCOL FAULT (the received bytes/shape are not legible input at all —
/// malformed JSON, a foreign magic, an unrecognized wire discriminant) or a VERDICT (the input parsed fine; a rule
/// examined its content or the caller's authority and refused what it means). <c>world.refusals</c> reports this per
/// row so a reader can separate "your input is not even legible" from "your input is legible and rejected".</summary>
public enum RefusalKind : byte {
    /// <summary>The bytes/shape do not parse as this door's input at all.</summary>
    ProtocolFault,

    /// <summary>The input parsed; a rule examined it (or the caller) and refused what it means.</summary>
    Verdict,
}

/// <summary>Declares one refusal a door can produce — attached directly to the enum member the door's refusal path
/// requires a caller to name (see e.g. <c>Client.Sdf.SdfDocumentException</c>'s constructor, which takes an
/// <c>SdfRefusal</c> rather than a bare string). <c>Puck.World.RefusalCatalog</c> discovers every so-tagged member by
/// reflection across every <c>Puck.World*</c> assembly, so the enumeration <c>world.refusals</c> prints is read off
/// the SAME finite set a door's refusal constructor requires a caller to pick from — never a hand-kept second list a
/// door's real throw sites can drift out of step with. A door that grows a new refusal adds an enum member (or a new
/// enum) and tags it here; the next <c>world.refusals</c> invocation lists it with no further step, because the
/// catalog is DISCOVERED by scanning each assembly's enum types on every call, never registered by hand into a list
/// someone has to remember to update. Lives in Puck.World.Data (not beside the discovery scanner) because a
/// refusal-tagged enum can live in any of the three World assemblies (Data, Server, or the composition root), and
/// the attribute type itself has to be reachable from all three — the lowest one in the reference graph.
/// <para>What this does NOT guarantee: that a listed member is still reachable. Tagging is one-directional — it
/// proves a door cannot refuse with an UNLISTED reason (the constructor has no other way to be called), not that
/// every listed reason still has a live call site producing it.</para></summary>
/// <param name="door">The door's stable name (dotted, e.g. <c>sdf.decode</c>).</param>
/// <param name="condition">The one-line condition that triggers this refusal.</param>
/// <param name="kind">Whether this is a protocol fault or a verdict.</param>
[AttributeUsage(validOn: AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class RefusalAttribute(string door, string condition, RefusalKind kind) : Attribute {
    /// <summary>Gets the door's stable name.</summary>
    public string Door { get; } = door;

    /// <summary>Gets the one-line condition that triggers this refusal.</summary>
    public string Condition { get; } = condition;

    /// <summary>Gets a value indicating whether this is a protocol fault or a verdict.</summary>
    public RefusalKind Kind { get; } = kind;
}

/// <summary>One cataloged refusal row: a door, the stable id (the enum member's own name — never a second string kept
/// in sync with it by hand), the kind, and the one-line triggering condition.</summary>
/// <param name="Door">The door's stable name.</param>
/// <param name="Id">The refusal's stable id — the tagged enum member's own name.</param>
/// <param name="Kind">Whether this is a protocol fault or a verdict.</param>
/// <param name="Condition">The one-line triggering condition.</param>
public readonly record struct RefusalCatalogEntry(string Door, string Id, RefusalKind Kind, string Condition);
