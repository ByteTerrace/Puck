using System.Text;
using Puck.Abstractions.Machines;
using Puck.Assets;

namespace Puck.HumbleGamingDeck.Forge;

/// <summary>Prepares authored selections of the Deck's original diagnostic programs.</summary>
public sealed class DeckContentProvider : IMachineContentProvider {
    private static readonly Lazy<DerivedBytes> Cache = new(valueFactory: static () => new DerivedBytes(name: "deck", roots: [("Puck.HumbleGamingDeck.Forge", "Puck.HumbleGamingDeck.Forge.DeckTestCartridges", "get_All")]));
    /// <inheritdoc/>
    public string EngineId => HumbleGamingDeckEngine.EngineId;
    /// <inheritdoc/>
    public bool Recognizes(string contentPath) => contentPath.EndsWith(value: ".deck", comparisonType: StringComparison.OrdinalIgnoreCase);
    /// <inheritdoc/>
    public PreparedMachineContent Prepare(ReadOnlyMemory<byte> content, string contentPath) {
        var name = Encoding.UTF8.GetString(bytes: content.Span).Trim();
        foreach (var cartridge in DeckTestCartridges.All) {
            if (Path.GetFileNameWithoutExtension(path: cartridge.FileName) == name) {
                return new PreparedMachineContent(Image: Cache.Value.Get(inputs: name, derive: cartridge.Build),
                    SourceHash: AssetContentHash.Compute(content: content.Span).ToString(), Symbols: new Dictionary<string, MachineContentSymbol>());
            }
        }
        throw new MachineContentException(message: $"Unknown Deck program '{name}' in '{contentPath}'.");
    }
}
