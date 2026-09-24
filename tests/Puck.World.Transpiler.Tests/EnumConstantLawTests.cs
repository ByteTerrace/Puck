using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>An enum member is a constant: bare, it reads as its ordinal. A member spelled like a <c>let</c> in the same
/// scope, or like a module's parameter, is refused with PUCK115 on the member's line rather than silently replacing
/// the constant; the control, the same declarations with the member renamed, compiles and reads each value as
/// written. Two enums may share a member, and each reads it qualified; a bare read of the shared member names no one
/// ordinal and is refused with PUCK115 where it is read, rather than the later enum's ordinal silently
/// winning.</summary>
public sealed class EnumConstantLawTests {
    private const string State = "state {\n    world {\n        slot picked = 0\n    }\n}\n\n";

    private static readonly Dictionary<string, Refusal> Refusals = new(comparer: StringComparer.Ordinal) {
        ["a member spelled like a let"] = new(
            Body: (("let Rook = 7\n\nenum Piece {\n    Pawn,\n    Rook\n}\n\n" + State) + "rule \"pick\" {\n    picked = Rook\n}\n"),
            Code: PuckDiagnosticCodes.EnumMemberShadowsConstant,
            Needle: "    Rook\n}"
        ) { Mentions = "'Piece.Rook'" },
        ["a member spelled like a module parameter"] = new(
            Body: "module board(Rook) {\n    enum Piece {\n        Pawn,\n        Rook\n    }\n\n    state {\n        world {\n            slot picked = Rook\n        }\n    }\n}\n\nuse board as b(Rook: 7)\n",
            Code: PuckDiagnosticCodes.EnumMemberShadowsConstant,
            Needle: "        Rook\n    }"
        ),
        ["a bare read of a member two enums declare"] = new(
            Body: (("enum Piece {\n    Pawn,\n    Rook\n}\n\nenum Tower {\n    Keep,\n    Rook\n}\n\n" + State) + "rule \"pick\" {\n    picked = Rook\n}\n"),
            Code: PuckDiagnosticCodes.EnumMemberShadowsConstant,
            Needle: "    picked = Rook"
        ) { Mentions = "'Piece.Rook' or 'Tower.Rook'" },
        ["a bare value of a member two enums declare"] = new(
            Body: $"{TwoEnums}state {{\n    world {{\n        slot picked = Rook\n    }}\n}}\n",
            Code: PuckDiagnosticCodes.EnumMemberShadowsConstant,
            Needle: "        slot picked = Rook"
        ) { Alone = true },
        // The refused read stands in as its own placeholder, which a default of another kind does not refuse again
        // (PUCK053): one mistake, one report.
        ["a bare member as a text field's default"] = new(
            Body: $"{TwoEnums}record Card {{\n    label: Text = Rook\n}}\n\npool cards of Card capacity(1)\n",
            Code: PuckDiagnosticCodes.EnumMemberShadowsConstant,
            Needle: "    label: Text = Rook"
        ) { Alone = true },
        ["a bare member as a flag field's default"] = new(
            Body: $"{TwoEnums}record Card {{\n    up: Bool = Rook\n}}\n\npool cards of Card capacity(1)\n",
            Code: PuckDiagnosticCodes.EnumMemberShadowsConstant,
            Needle: "    up: Bool = Rook"
        ) { Alone = true },
        ["a bare member as a test's given value"] = new(
            Body: $"{TwoEnums}{State}test \"given\" {{\n    given {{\n        picked = Rook\n    }}\n    expect {{\n        picked == 1\n    }}\n}}\n",
            Code: PuckDiagnosticCodes.EnumMemberShadowsConstant,
            Needle: "        picked = Rook"
        ) { Alone = true },
    };

    private const string TwoEnums = "enum Piece {\n    Pawn,\n    Rook\n}\n\nenum Tower {\n    Keep,\n    Rook\n}\n\n";

    public static TheoryData<string> RefusalNames() => new(values: Refusals.Keys);
    [MemberData(nameof(RefusalNames))]
    [Theory]
    public void AnEnumMemberSpelledLikeAConstantIsRefusedOnceAtItsOwnLine(string name) => WorldSources.AssertRefused(
        label: name,
        refusal: Refusals[name]
    );
    [Fact]
    public void RenamedTheLetAndTheMemberEachReadAsWritten() {
        var json = WorldSources.LowerClean(body: (("let Tower = 7\n\nenum Piece {\n    Pawn,\n    Rook\n}\n\n" + State) + "rule \"pick\" {\n    picked = Rook + Tower + Piece.Rook\n}\n"));
        var constants = json["rules"]![0]!["effects"]![0]!["expression"]!["instructions"]!.AsArray()
            .Where(predicate: static instruction => (instruction!["op"]!.GetValue<string>() == "Constant"))
            .Select(selector: static instruction => instruction!["value"]!.ToJsonString())
            .ToArray();

        Assert.Equal(actual: constants, expected: ["1", "7", "1"]);
    }
    // The control for the shared-member row: the same two enums read qualified each read their own ordinal.
    [Fact]
    public void TwoEnumsSharingAMemberEachReadItQualified() {
        var json = WorldSources.LowerClean(body: (("enum Piece {\n    Pawn,\n    Rook\n}\n\nenum Tower {\n    Keep,\n    Rook\n}\n\n" + State) + "rule \"pick\" {\n    picked = Piece.Rook + Tower.Rook * 10\n}\n"));
        var constants = json["rules"]![0]!["effects"]![0]!["expression"]!["instructions"]!.AsArray()
            .Where(predicate: static instruction => (instruction!["op"]!.GetValue<string>() == "Constant"))
            .Select(selector: static instruction => instruction!["value"]!.ToJsonString())
            .ToArray();

        Assert.Equal(actual: constants, expected: ["1", "1", "10"]);
    }
}
