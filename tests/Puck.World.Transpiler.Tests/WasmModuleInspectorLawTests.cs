using Puck.World.Transpiler.Addons;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="WasmModuleInspector.Inspect"/> reads a well-formed module's imports and
/// exports, and answers bytes that are not one with an invalid result carrying neither. No length, count, or
/// truncation in the bytes reaches an exception: a module is a file an author points at.</summary>
public sealed class WasmModuleInspectorLawTests {
    private static readonly byte[] Header = [0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00];
    // One function import, puck.log, of type 0.
    private static readonly byte[] Imports = [0x01, 0x04, ((byte)'p'), ((byte)'u'), ((byte)'c'), ((byte)'k'), 0x03, ((byte)'l'), ((byte)'o'), ((byte)'g'), 0x00, 0x00];
    // One function export, tick, of index 0.
    private static readonly byte[] Exports = [0x01, 0x04, ((byte)'t'), ((byte)'i'), ((byte)'c'), ((byte)'k'), 0x00, 0x00];

    private static byte[] Module(params byte[][] parts) => [.. Header, .. parts.SelectMany(selector: static part => part)];
    private static byte[] Section(byte id, byte[] payload) => [id, ((byte)payload.Length), .. payload];

    /// <summary>Returns bytes that carry a valid header and a structure that does not hold.</summary>
    /// <returns>The cases, each named by what is wrong with it.</returns>
    public static TheoryData<string, byte[]> Malformed() => new() {
        { "a section length whose fifth byte sets the sign of a 32-bit value", Module([0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F]) },
        { "a section length encoded past 32 bits", Module([0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F]) },
        { "a section length that never ends", Module([0x00, 0x80]) },
        { "a section id with no length", Module([0x07]) },
        { "a section longer than the module", Module([0x07, 0x7F, 0x00]) },
        { "an export count with no entries", Module(Section(id: 0x07, payload: [0x05])) },
        { "an export name longer than its section", Module(Section(id: 0x07, payload: [0x01, 0x7F, ((byte)'a')])) },
        { "an export with no kind", Module(Section(id: 0x07, payload: [0x01, 0x01, ((byte)'a')])) },
        { "an import with no descriptor", Module(Section(id: 0x02, payload: [0x01, 0x01, ((byte)'m'), 0x01, ((byte)'n'), 0x00])) },
        { "a table import cut off inside its limits", Module(Section(id: 0x02, payload: [0x01, 0x01, ((byte)'m'), 0x01, ((byte)'n'), 0x01])) },
        { "a global import cut off before its mutability", Module(Section(id: 0x02, payload: [0x01, 0x01, ((byte)'m'), 0x01, ((byte)'n'), 0x03, 0x7F])) },
        { "an import of a kind the format does not define", Module(Section(id: 0x02, payload: [0x01, 0x01, ((byte)'m'), 0x01, ((byte)'n'), 0x09, 0x00])) },
    };
    [Fact]
    public void AWellFormedModuleReportsItsImportsAndExports() {
        var result = WasmModuleInspector.Inspect(wasmBytes: Module(
            Section(id: 0x02, payload: Imports),
            // A custom section the inspector has no reader for is skipped by its length.
            Section(id: 0x00, payload: [0x01, ((byte)'x'), 0xAA, 0xBB]),
            Section(id: 0x07, payload: Exports)
        ));

        Assert.True(condition: result.IsValid);
        Assert.Equal(
            actual: result.Imports,
            expected: [new WasmImport(
                Kind: 0,
                Module: "puck",
                Name: "log"
            )]
        );
        Assert.Equal(
            actual: result.Exports,
            expected: ["tick"]
        );
    }
    [Fact]
    public void AHeaderAloneIsAValidModuleWithNothingInIt() {
        var result = WasmModuleInspector.Inspect(wasmBytes: Header);

        Assert.True(condition: result.IsValid);
        Assert.Empty(collection: result.Imports);
        Assert.Empty(collection: result.Exports);
    }
    [MemberData(nameof(Malformed))]
    [Theory]
    public void BytesWhoseStructureDoesNotHoldAreInvalidAndReportNothing(string what, byte[] bytes) {
        var result = WasmModuleInspector.Inspect(wasmBytes: bytes);

        Assert.False(
            condition: result.IsValid,
            userMessage: what
        );
        Assert.Empty(collection: result.Imports);
        Assert.Empty(collection: result.Exports);
    }
    [Fact]
    public void EveryTruncationOfAWellFormedModuleIsAnsweredWithoutThrowing() {
        var whole = Module(
            Section(id: 0x02, payload: Imports),
            Section(id: 0x07, payload: Exports)
        );

        for (var length = 0; (length <= whole.Length); length++) {
            var result = WasmModuleInspector.Inspect(wasmBytes: whole.AsSpan(
                length: length,
                start: 0
            ));

            // A cut at a section boundary is a shorter well-formed module; every other cut is invalid.
            if (!result.IsValid) {
                Assert.Empty(collection: result.Imports);
                Assert.Empty(collection: result.Exports);
            }
        }
    }
}
