using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.Assets.Documents;

namespace Puck.GamingBricks.Forge;

/// <summary>Adapts cartridge validation and native compilation to the neutral machine-content contract.</summary>
internal static class CartridgeContentProvider {
    internal static PreparedMachineContent Prepare(ICartridgeCompiler compiler, ReadOnlyMemory<byte> content) {
        try {
            var compilation = compiler.Compile(document: CartridgeDocuments.Parse(utf8: content.Span));
            var symbols = new Dictionary<string, MachineContentSymbol>(comparer: StringComparer.Ordinal);

            foreach (var (name, address) in compilation.Variables) {
                symbols.Add(key: name, value: new MachineContentSymbol(Space: "bus", Address: address));
            }
            foreach (var (name, address) in compilation.Arrays) {
                symbols.Add(key: name, value: new MachineContentSymbol(Space: "bus", Address: address));
            }

            return new PreparedMachineContent(Image: compilation.Rom, SourceHash: compilation.SourceHash, Symbols: symbols);
        } catch (Exception exception) when (exception is JsonException or DocumentValidationException or ArgumentException or InvalidOperationException or CartridgeCapacityException) {
            throw new MachineContentException(message: exception.Message.ReplaceLineEndings(replacementText: " "), innerException: exception);
        }
    }
}
