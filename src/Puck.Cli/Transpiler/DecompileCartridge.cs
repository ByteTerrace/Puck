using System.Text.Json.Nodes;
using Puck.GamingBricks.Transpiler;

namespace Puck.Cli.Transpiler;

/// <summary>Writes a <c>puck.cartridge.v1</c> document back out as Puck DSL source.</summary>
internal static class DecompileCartridge {
    /// <summary>Determines whether a parsed JSON document is a cartridge.</summary>
    /// <param name="document">The parsed document.</param>
    /// <returns><see langword="true"/> when its schema names a cartridge.</returns>
    internal static bool Handles(JsonObject document) => string.Equals(
        a: document["schema"]?.GetValue<string>(),
        b: CartridgeVocabulary.Schema,
        comparisonType: StringComparison.Ordinal
    );

    /// <summary>Writes the cartridge out as source.</summary>
    /// <param name="document">The cartridge JSON.</param>
    /// <returns>The source text.</returns>
    internal static string Run(JsonObject document) => CartridgeDecompiler.Decompile(document: document);
}
