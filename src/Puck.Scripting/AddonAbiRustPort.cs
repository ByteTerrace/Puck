using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Puck.Scripting;

/// <summary>
/// Emits the Rust mirror of the addon ABI's closed wire-value sets and constants —
/// <see cref="AddonOutCellKind"/>, <see cref="AddonInCellKind"/>, <see cref="AddonChannelKind"/>,
/// <see cref="AddonSubjectKind"/>, <see cref="AddonVerdict"/> (plus its <see cref="AddonVerdicts.IsAllowed"/>
/// predicate), <see cref="AddonCapabilityMask"/>, and <see cref="AddonAbi"/> (its own constants plus its nested
/// offset/verb classes) — for <c>wasm/puck-stdlib/src/abi_generated.rs</c>. Every name and value is read from
/// the live types by reflection, never retyped as a literal, so this cannot silently drift from the host even if
/// this file is never touched again: a renamed member, an added member, or a changed constant all flow straight
/// through the next regeneration.
/// </summary>
/// <remarks>
/// This lives in <c>Puck.Scripting</c> because it is the assembly that owns every mirrored type — the same
/// reasoning <see cref="Puck.Maths.FixedQ4816RustPort"/>'s remarks give for living beside <c>FixedQ4816</c>. It is
/// one contributor to <see cref="WasmStdlibSources.All"/>, alongside <see cref="Puck.Maths.FixedQ4816RustPort"/>'s
/// two artifacts, whose public emitters the registry calls. Deterministic and
/// reproducible: reflection order is made irrelevant by sorting every emitted group by declaration name, so
/// an unchanged host produces byte-identical output on every run. Nothing gates this automatically today; drift is
/// caught only by regenerating and reading the diff.
/// </remarks>
internal static class AddonAbiRustPort {
    /// <summary>Emits the complete text of <c>abi_generated.rs</c>.</summary>
    internal static string EmitGenerated() {
        var sb = new StringBuilder();

        sb.Append(value: """
        //! GENERATED — do not hand-edit. Regenerate with:
        //!
        //! ```text
        //! dotnet run --project src/Puck.Cli -c Release -- wasm-stdlib
        //! ```
        //!
        //! Mirrored bit-for-bit from the live C# host and read by name — never retyped — so this module
        //! cannot silently drift from the types it mirrors:
        //!
        //! - [`OutCellKind`], [`InCellKind`], [`ChannelKind`], [`SubjectKind`], and [`Verdict`] mirror
        //!   `Puck.Scripting.AddonOutCellKind`, `AddonInCellKind`, `AddonChannelKind`, `AddonSubjectKind`,
        //!   and `AddonVerdict` respectively — the addon ABI's wire value sets, pinned independently of any
        //!   consumer enum. `Verdict::is_allowed` mirrors `Puck.Scripting.AddonVerdicts.IsAllowed`, generated
        //!   from the live predicate rather than hand-listed.
        //! - `CAP_*` mirror `Puck.Scripting.AddonCapabilityMask` (`src/Puck.Scripting/AddonCapabilityMask.cs`)
        //!   as raw `u64` mask values, not bit positions.
        //! - The remaining constants mirror every public constant on `Puck.Scripting.AddonAbi`
        //!   (`src/Puck.Scripting/AddonAbi.cs`) and its nested `OutCellOffsets`/`InCellOffsets`/
        //!   `ChannelDescriptorOffsets`/`RequestVerbs`/`ObservationVerbs` classes: the frozen byte layout,
        //!   sizes, version, and budgets the addon ABI freezes. `OUT_CELL_OFFSET_*`/`IN_CELL_OFFSET_*`/
        //!   `CHANNEL_DESCRIPTOR_OFFSET_*`/`REQUEST_VERB_*`/`OBSERVATION_VERB_*` carry their nested class's
        //!   name as a prefix; every other constant keeps `AddonAbi`'s own name.
        //!
        //! This mirrors `AddonAbi`'s COMPLETE public constant surface, not just the fields this
        //! crate's typed accessors happen to consume today — cherry-picking which constants to
        //! generate would reintroduce exactly the drift risk this file exists to close, the moment a
        //! future consumer needs one that generation skipped. `#![allow(dead_code)]` accordingly:
        //! this is a private module (see `lib.rs`), so a `pub const`/`pub enum` variant unused
        //! in-crate today is expected, not a mistake.

        #![allow(dead_code)]


        """);

        AppendByteEnum<AddonOutCellKind>(
            csharpTypeName: "AddonOutCellKind",
            rustName: "OutCellKind",
            sb: sb
        );
        AppendByteEnum<AddonInCellKind>(
            csharpTypeName: "AddonInCellKind",
            rustName: "InCellKind",
            sb: sb
        );
        AppendByteEnum<AddonChannelKind>(
            csharpTypeName: "AddonChannelKind",
            rustName: "ChannelKind",
            sb: sb
        );
        AppendByteEnum<AddonSubjectKind>(
            csharpTypeName: "AddonSubjectKind",
            rustName: "SubjectKind",
            sb: sb
        );
        AppendByteEnum<AddonVerdict>(
            csharpTypeName: "AddonVerdict",
            rustName: "Verdict",
            sb: sb
        );
        AppendVerdictIsAllowed(sb: sb);
        AppendCapabilityMask(sb: sb);
        AppendAbiConstants(sb: sb);

        return sb.ToString();
    }

    private static void AppendAbiConstants(StringBuilder sb) {
        AppendConstGroup(
            comment: "`Puck.Scripting.AddonAbi` constants (`src/Puck.Scripting/AddonAbi.cs`).",
            csharpTypeName: "AddonAbi",
            rustPrefix: null,
            sb: sb,
            type: typeof(AddonAbi)
        );
        AppendConstGroup(
            comment: "`Puck.Scripting.AddonAbi.OutCellOffsets` — the guest→host output cell field offsets.",
            csharpTypeName: "AddonAbi.OutCellOffsets",
            rustPrefix: "OUT_CELL_OFFSET",
            sb: sb,
            type: typeof(AddonAbi.OutCellOffsets)
        );
        AppendConstGroup(
            comment: "`Puck.Scripting.AddonAbi.InCellOffsets` — the host→guest input cell field offsets.",
            csharpTypeName: "AddonAbi.InCellOffsets",
            rustPrefix: "IN_CELL_OFFSET",
            sb: sb,
            type: typeof(AddonAbi.InCellOffsets)
        );
        AppendConstGroup(
            comment: "`Puck.Scripting.AddonAbi.ChannelDescriptorOffsets` — the channel descriptor table field offsets.",
            csharpTypeName: "AddonAbi.ChannelDescriptorOffsets",
            rustPrefix: "CHANNEL_DESCRIPTOR_OFFSET",
            sb: sb,
            type: typeof(AddonAbi.ChannelDescriptorOffsets)
        );
        AppendConstGroup(
            comment: "`Puck.Scripting.AddonAbi.RequestVerbs` — the Request channel's closed numeric vocabulary.",
            csharpTypeName: "AddonAbi.RequestVerbs",
            rustPrefix: "REQUEST_VERB",
            sb: sb,
            type: typeof(AddonAbi.RequestVerbs)
        );
        AppendConstGroup(
            comment: "`Puck.Scripting.AddonAbi.ObservationVerbs` — the host-written disclosure verb vocabulary.",
            csharpTypeName: "AddonAbi.ObservationVerbs",
            rustPrefix: "OBSERVATION_VERB",
            sb: sb,
            type: typeof(AddonAbi.ObservationVerbs)
        );
    }
    // Generic mirror for the addon ABI's plain byte-discriminant enums: every member emits as-is, in
    // Enum.GetValues declaration order (already the ascending wire-value order for each of these types, so
    // this is deterministic run-to-run without an extra sort).
    private static void AppendByteEnum<TEnum>(StringBuilder sb, string csharpTypeName, string rustName) where TEnum : struct, Enum {
        sb.Append(value: "/// Mirrors `Puck.Scripting.").Append(value: csharpTypeName).Append(value: "` (`src/Puck.Scripting/").Append(value: csharpTypeName).Append(value: ".cs`).\n");
        sb.Append(value: "#[repr(u8)]\n");
        sb.Append(value: "#[derive(Clone, Copy, Debug, Eq, PartialEq)]\n");
        sb.Append(value: "pub enum ").Append(value: rustName).Append(value: " {\n");

        foreach (var value in Enum.GetValues<TEnum>()) {
            var name = value.ToString();
            var raw = Convert.ToByte(
                value: value,
                provider: CultureInfo.InvariantCulture
            );

            sb.Append(value: "    /// `Puck.Scripting.").Append(value: csharpTypeName).Append(value: '.').Append(value: name).Append(value: "` (`").Append(value: raw).Append(value: "`).\n");
            sb.Append(value: "    ").Append(value: name).Append(value: " = ").Append(value: raw).Append(value: ",\n");
        }

        sb.Append(value: "}\n\n");
    }
    private static void AppendCapabilityMask(StringBuilder sb) {
        AppendConstGroup(
            comment: "`Puck.Scripting.AddonCapabilityMask` (`src/Puck.Scripting/AddonCapabilityMask.cs`) — the addon ABI's Ask capability-mask bit values.",
            csharpTypeName: "AddonCapabilityMask",
            rustPrefix: "CAP",
            sb: sb,
            type: typeof(AddonCapabilityMask)
        );
    }
    // Reflects every public const field declared directly on `type` (never a base member, never a nested
    // type's own fields) and emits one Rust `pub const` per field, sorted by name for a reflection-order-
    // independent, byte-identical-on-every-run result. `rustPrefix` (e.g. "CAP") is prepended to every emitted
    // name; the five AddonAbi nested classes need it because their bare field names would otherwise collide
    // across groups (or, for RequestVerbs/ObservationVerbs/CAP, read ambiguously without it).
    private static void AppendConstGroup(StringBuilder sb, string comment, string csharpTypeName, string? rustPrefix, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type type) {
        var fields = type
            .GetFields(bindingAttr: BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(predicate: static field => field.IsLiteral)
            .OrderBy(
            keySelector: static field => field.Name,
            comparer: StringComparer.Ordinal
        );

        sb.Append(value: "// ").Append(value: comment).Append(value: '\n');

        foreach (var field in fields) {
            var rustName = $"{((rustPrefix is null)
                ? string.Empty
                : $"{rustPrefix}_")}{ToScreamingSnakeCase(pascal: field.Name)}";
            var rustType = RustTypeFor(field: field);
            var rawValue = field.GetRawConstantValue();
            var valueText = Convert.ToString(
                value: rawValue,
                provider: CultureInfo.InvariantCulture
            );

            sb.Append(value: "/// `").Append(value: csharpTypeName).Append(value: '.').Append(value: field.Name).Append(value: "` (`").Append(value: valueText).Append(value: "`).\n");
            sb.Append(value: "pub const ").Append(value: rustName).Append(value: ": ").Append(value: rustType).Append(value: " = ").Append(value: valueText).Append(value: ";\n");
        }

        sb.Append(value: '\n');
    }
    // Generated FROM AddonVerdicts.IsAllowed by iterating the live enum, never hand-listed — the predicate
    // moving in either language flows straight through the next regeneration.
    private static void AppendVerdictIsAllowed(StringBuilder sb) {
        var allowedNames = Enum.GetValues<AddonVerdict>()
            .Where(predicate: static value => AddonVerdicts.IsAllowed(verdict: value))
            .Select(selector: static value => value.ToString());

        sb.Append(value: "impl Verdict {\n");
        sb.Append(value: "    /// Mirrors `Puck.Scripting.AddonVerdicts.IsAllowed`, generated from the live predicate.\n");
        sb.Append(value: "    pub fn is_allowed(self) -> bool {\n");
        sb.Append(value: "        matches!(self, ").Append(value: string.Join(
            separator: " | ",
            values: allowedNames.Select(selector: static name => $"Verdict::{name}")
        )).Append(value: ")\n");
        sb.Append(value: "    }\n");
        sb.Append(value: "}\n\n");
    }
    // `AddonAbi.AbiVersion` crosses the ABI as the `i32` every `puck_abi_version` export returns; `long`
    // constants (`DefaultFuelPerTick`/`One`) cross as `i64`; `ulong` constants (`AddonCapabilityMask`'s bits)
    // cross as `u64`; every other integral constant here is a byte count, offset, count, or cap, which this
    // crate always carries as `usize`.
    private static string RustTypeFor(FieldInfo field) {
        if (string.Equals(
            a: field.Name,
            b: "AbiVersion",
            comparisonType: StringComparison.Ordinal
        )) {
            return "i32";
        }

        if (field.FieldType == typeof(long)) {
            return "i64";
        }

        if (field.FieldType == typeof(ulong)) {
            return "u64";
        }

        return "usize";
    }
    // PascalCase -> SCREAMING_SNAKE_CASE: an underscore before every uppercase letter that isn't the first
    // character, then uppercase the whole thing. "VerbTablePtr" -> "VERB_TABLE_PTR", "A" -> "A".
    private static string ToScreamingSnakeCase(string pascal) {
        var sb = new StringBuilder(capacity: (pascal.Length * 2));

        for (var index = 0; (index < pascal.Length); ++index) {
            var current = pascal[index];

            if (
                (index > 0) &&
                char.IsUpper(c: current)
            ) {
                sb.Append(value: '_');
            }

            sb.Append(value: char.ToUpperInvariant(c: current));
        }

        return sb.ToString();
    }
}
