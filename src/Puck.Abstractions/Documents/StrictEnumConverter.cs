using System.Text.Json.Serialization;

namespace Puck.Abstractions.Documents;

/// <summary>
/// The one strict enum-converter this tree's source-gen JSON contexts apply per enum type: writes an enum by its
/// exact declared member name (<c>namingPolicy: null</c> — no naming policy touches an enum value) and refuses a
/// numeric token — or any name that is not a defined member — on read, rather than tolerating either.
/// <c>UseStringEnumConverter = true</c> writes by name too, but has no <c>allowIntegerValues</c> knob, so it still
/// accepts a numeric wire value on read, silently.
/// </summary>
/// <remarks>
/// This is the generic (<c>TEnum</c>-bound) form the BCL ships from .NET 8 —
/// <see cref="JsonStringEnumConverter{TEnum}"/> — never the non-generic <see cref="JsonStringEnumConverter"/>: a
/// source-gen <c>[JsonSourceGenerationOptions(Converters = ...)]</c> array fails the build outright with SYSLIB1034,
/// unconditionally (not merely an AOT-publish nag — it fires with native AOT/trimming off too), if the non-generic
/// form (or a subclass of it) appears there, because it resolves its per-value behavior at runtime rather than at
/// compile time for one closed type. Being generic and a concrete (non-factory) type, this converter needs no
/// <see cref="Type.MakeGenericType(Type[])"/> reflection to reach the enum it converts, so it carries no AOT/trim
/// analysis gap the way the factory it replaces did — no project need opt out of the repo-wide AOT/trim default to
/// use it.
/// <para>
/// Applied per enum at the declaration — <c>[JsonConverter(typeof(StrictEnumConverter&lt;ThatEnum&gt;))]</c> — so the
/// enum itself declares how it crosses a wire once, rather than a central list every source-gen context must repeat
/// and keep in sync. Every enum <c>Puck.World.WorldJsonContext</c> (<c>puck.world.def.v1</c>) and
/// <c>Puck.Recording.Document.RecordingJsonContext</c> (<c>puck.recording.v1</c>) reach through their document
/// graphs carries this attribute.
/// </para>
/// </remarks>
public sealed class StrictEnumConverter<TEnum> : JsonStringEnumConverter<TEnum> where TEnum : struct, Enum {
    /// <summary>Initializes a new instance of the <see cref="StrictEnumConverter{TEnum}"/> class: no naming policy
    /// (an enum value is never touched, only property names are), and integer tokens refused on read.</summary>
    public StrictEnumConverter() : base(namingPolicy: null, allowIntegerValues: false) {
    }
}
