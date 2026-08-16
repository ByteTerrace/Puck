using System.Text.Json;

namespace Puck.World;

/// <summary>
/// The one round-trip regime a versioned document root's <c>Extensions</c> bag follows: unknown top-level members
/// are captured into a settable <c>IDictionary&lt;string, JsonElement&gt;?</c> via <c>[JsonExtensionData]</c> and
/// validated through <see cref="ValidateKeys"/>.
/// </summary>
/// <remarks>
/// The world document (<c>puck.world.def.v1</c>) — including an owned identity, which is a <c>WorldDefinition</c>
/// instance in its own right — follows this regime; "survives deserialization" and "passes validation" mean the
/// same thing. A reserved-prefix key ('$' schema-like keys, '_' comments) is an intentional escape hatch and always
/// allowed; anything else at the top level is an authoring mistake (most often a mis-cased or mistyped section name)
/// and is reported rather than silently absorbed.
/// <para>Nothing interprets a captured value — no dispatch path reads <c>Extensions</c> to drive behavior, and nothing
/// should. The keys are not inert, though: an unprefixed one now fails validation, so the bag's content decides whether
/// the document loads at all. This regime applies to document roots only; other document families under
/// <c>Puck.World.Forge</c> and <c>Puck.Recording</c> carry their own <c>[JsonExtensionData]</c> bags and validate them
/// through <c>DocumentCanonicalizer.ValidateExtensions</c> instead.</para>
/// </remarks>
public static class DocumentExtensionsPolicy {
    /// <summary>True when <paramref name="key"/> is a reserved escape hatch ('$' or '_' prefixed) rather than an
    /// authoring mistake.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public static bool IsReservedKey(string key) {
        ArgumentNullException.ThrowIfNull(argument: key);

        return (
            (key.Length != 0) &&
            ((key[0] == '$') || (key[0] == '_'))
        );
    }
    /// <summary>Runs the shared regime over a document's captured <c>Extensions</c> keys, invoking
    /// <paramref name="report"/> once per key that is not a reserved escape hatch.</summary>
    /// <param name="extensions">The document's captured extension-data bag; <see langword="null"/> (the document
    /// carried no unknown top-level members) reports nothing.</param>
    /// <param name="report">Invoked once per offending key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    public static void ValidateKeys(IDictionary<string, JsonElement>? extensions, Action<string> report) {
        ArgumentNullException.ThrowIfNull(argument: report);

        if (extensions is null) {
            return;
        }

        foreach (var key in extensions.Keys) {
            if (!IsReservedKey(key: key)) {
                report.Invoke(obj: key);
            }
        }
    }
}
