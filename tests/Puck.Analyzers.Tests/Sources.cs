namespace Puck.Analyzers.Tests;

/// <summary>The source shapes cases brand, kept in one place so a case reads as the one thing it varies.</summary>
internal static class Sources {
    /// <summary>The brand id every default shape carries.</summary>
    public const string TargetId = "target";

    /// <summary>The documentation-comment id of <see cref="BrandedMethod"/>'s declaration.</summary>
    public const string TargetSymbol = "M:Subject.Assembly.Subject.Target";

    /// <summary>Wraps <paramref name="members"/> in a type in the namespace that matches the harness assembly name.</summary>
    public static string InType(string members, string typeName = "Subject", string containingNamespace = Harness.DefaultNamespace, string modifiers = "internal static", string usings = "") =>
        $$"""
        {{usings}}namespace {{containingNamespace}};

        {{modifiers}} class {{typeName}} {
        {{members}}
        }

        """;

    /// <summary>An ordinary branded static method: the baseline every placement case is compared against.</summary>
    public static string BrandedMethod(string attribute = "[VerifiedCode(\"target\")]", string body = "        return 1;") =>
        InType(members: $$"""
            {{attribute}}
            public static int Target() {
        {{body}}
            }
        """);

    /// <summary>An unbranded compilation, used where the case is about the manifest rather than about a brand.</summary>
    public static string Unbranded() =>
        InType(members: """
            public static int Target() {
                return 1;
            }
        """);

    /// <summary>The repository's own branded bit-mix permutation, reproduced token for token.</summary>
    /// <param name="firstMultiplier">The multiplier constant the branded body reads, declared outside the branded declaration.</param>
    /// <param name="typeName">The containing type, so a case can move the identical declaration somewhere the manifest does not name.</param>
    /// <param name="extraMembers">Members appended after the branded one, so a case can vary the file around it.</param>
    /// <remarks>
    /// Its recorded fingerprint is <see cref="BitMixRecordedHash"/>. Everything the body's correctness rests on —
    /// both multipliers, both multiplier inverses, and both shift counts — is declared beside it rather than inside
    /// it, which is what <see cref="BitMixDependencies"/> names so that the seal reaches them.
    /// </remarks>
    public static string BitMix(string firstMultiplier = "0x7FEB352DU", string typeName = "InvertibleBitMix", string extraMembers = "") =>
        $$"""
        namespace Puck.Maths;

        public static class {{typeName}} {
            public const uint FirstMultiplier = {{firstMultiplier}};
            public const uint FirstMultiplierInverse = 0x1D69E2A5U;
            public const int FirstShift = 16;
            public const int MiddleShift = 15;
            public const uint SecondMultiplier = 0x846CA68BU;
            public const uint SecondMultiplierInverse = 0x43021123U;

            [VerifiedCode("invertible-bit-mix.mix", Laws = "sampling.bit-mix-is-a-permutation, sampling.bit-mix-constants-invert")]
            public static uint Mix(uint value) {
                unchecked {
                    value ^= (value >>> FirstShift);
                    value *= FirstMultiplier;
                    value ^= (value >>> MiddleShift);
                    value *= SecondMultiplier;
                    value ^= (value >>> FirstShift);

                    return value;
                }
            }
        {{extraMembers}}
        }

        """;

    /// <summary>The brand id <see cref="BitMix"/> carries.</summary>
    public const string BitMixId = "invertible-bit-mix.mix";

    /// <summary>The assembly <see cref="BitMix"/> is compiled as, so the manifest sweep attributes its entry here.</summary>
    public const string BitMixAssemblyName = "Puck.Maths";

    /// <summary>The documentation-comment id <c>VerifiedCode.json</c> records for <see cref="BitMix"/>.</summary>
    public const string BitMixSymbol = "M:Puck.Maths.InvertibleBitMix.Mix(System.UInt32)";

    /// <summary>The declarations <c>VerifiedCode.json</c> records <see cref="BitMix"/>'s proof as resting on, copied from the committed ledger.</summary>
    public static readonly string[] BitMixDependencies = [
        "F:Puck.Maths.InvertibleBitMix.FirstMultiplier",
        "F:Puck.Maths.InvertibleBitMix.FirstMultiplierInverse",
        "F:Puck.Maths.InvertibleBitMix.FirstShift",
        "F:Puck.Maths.InvertibleBitMix.MiddleShift",
        "F:Puck.Maths.InvertibleBitMix.SecondMultiplier",
        "F:Puck.Maths.InvertibleBitMix.SecondMultiplierInverse",
    ];

    /// <summary>The fingerprint <c>VerifiedCode.json</c> records for <see cref="BitMix"/>, copied from the committed ledger.</summary>
    public const string BitMixRecordedHash = "68721f164713f6e87d9c0f438756438b6ed35aaece24c83e6260c22be4ede2c9";
}
