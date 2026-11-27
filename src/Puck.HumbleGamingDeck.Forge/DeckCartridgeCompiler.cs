using Puck.Abstractions.Machines;
using Puck.GamingBricks.Forge;
using Puck.GamingBricks.Transpiler;

namespace Puck.HumbleGamingDeck.Forge;

/// <summary>Compiles <c>puck.cartridge.v1</c> documents of target <c>hgd</c> into native 6502 images for the Humble
/// Gaming Deck, on an NROM, MMC1 or MMC3 board.</summary>
/// <remarks>
/// <para>The document names its board, or the compiler picks the smallest that holds the image: nrom when nothing is
/// saved and the image fits its 32 KiB, then mmc1, then mmc3. The one refusal is an image that outgrows its board
/// (<see cref="CartridgeCapacityException"/>).</para>
/// <para><see cref="DeckCartridgeEmitter"/> holds the memory map, the frame and the lowering.</para>
/// </remarks>
public sealed class DeckCartridgeCompiler : ICartridgeCompiler {
    /// <summary>The target token this compiler accepts.</summary>
    public const string TargetName = "hgd";

    /// <inheritdoc/>
    public string EngineId => HumbleGamingDeckEngine.EngineId;
    /// <inheritdoc/>
    public IReadOnlyList<string> Targets { get; } = [TargetName];

    /// <inheritdoc/>
    public bool Recognizes(string contentPath) =>
        CartridgeSource.Recognizes(path: contentPath);
    /// <inheritdoc/>
    public PreparedMachineContent Prepare(ReadOnlyMemory<byte> content, string contentPath) =>
        CartridgeSource.Prepare(compiler: this, content: content, contentPath: contentPath);
    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The document does not target hgd.</exception>
    /// <exception cref="CartridgeCapacityException">The image outgrows its board, or every board when none is named.</exception>
    public CartridgeCompilation Compile(CartridgeDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var source = CartridgeDocuments.Canonicalize(document: document);

        document = source.Document;
        if (document.Target != TargetName) {
            throw new ArgumentException(message: "This compiler requires target hgd.", paramName: nameof(document));
        }
        if (document.Board is { } named) {
            return Build(board: DeckBoards.Parse(name: named), document: document, sourceHash: source.Hash);
        }

        // Smallest first; nrom has no battery memory, so a saving document starts at mmc1.
        var candidates = ((document.Save is null) ? DeckBoards.All : DeckBoards.All[1..]);

        for (var index = 0; ; ++index) {
            try {
                return Build(board: candidates[index], document: document, sourceHash: source.Hash);
            } catch (CartridgeCapacityException) when (index < (candidates.Length - 1)) {
                // The next board up has room for more.
            }
        }
    }

    private static CartridgeCompilation Build(DeckBoard board, CartridgeDocument document, string sourceHash) {
        var emitter = new DeckCartridgeEmitter(board: board, document: document);
        var rom = emitter.Emit();

        return new CartridgeCompilation(
            Arrays: emitter.Arrays,
            Rom: rom,
            SourceHash: sourceHash,
            Target: document.Target,
            Variables: emitter.Variables
        );
    }
}
/// <summary>A cartridge board the Deck compiler builds for.</summary>
public enum DeckBoard {
    /// <summary>NROM: 32 KiB of fixed PRG ROM, no PRG-RAM.</summary>
    Nrom = 0,
    /// <summary>MMC1 (SxROM): up to 256 KiB of PRG ROM in 16 KiB banks, and 8 KiB of PRG-RAM.</summary>
    Mmc1 = 1,
    /// <summary>MMC3 (TxROM): up to 512 KiB of PRG ROM in 8 KiB banks, and 8 KiB of PRG-RAM.</summary>
    Mmc3 = 2,
}
/// <summary>The boards' names, sizes and mapper numbers.</summary>
public static class DeckBoards {
    /// <summary>Gets every board, smallest first.</summary>
    public static DeckBoard[] All { get; } = [DeckBoard.Nrom, DeckBoard.Mmc1, DeckBoard.Mmc3];

    /// <summary>Returns the board a document's name selects.</summary>
    /// <param name="name">nrom, mmc1 or mmc3.</param>
    /// <returns>The board.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> names no board; validation refuses it first.</exception>
    public static DeckBoard Parse(string name) => name switch {
        "nrom" => DeckBoard.Nrom,
        "mmc1" => DeckBoard.Mmc1,
        "mmc3" => DeckBoard.Mmc3,
        _ => throw new ArgumentException(message: $"'{name}' names no Deck board.", paramName: nameof(name)),
    };
    /// <summary>Returns the most 16 KiB data pages a board maps at $8000, beside the 16 KiB of code fixed at $C000.</summary>
    /// <param name="board">The board.</param>
    /// <returns>1 on nrom, 15 on mmc1 (256 KiB), 31 on mmc3 (512 KiB).</returns>
    public static int DataPages(DeckBoard board) => board switch {
        DeckBoard.Nrom => 1,
        DeckBoard.Mmc1 => 15,
        _ => 31,
    };
    /// <summary>Returns whether a board carries 8 KiB of PRG-RAM at $6000.</summary>
    /// <param name="board">The board.</param>
    /// <returns><see langword="true"/> for mmc1 and mmc3.</returns>
    public static bool HasPrgRam(DeckBoard board) => (board != DeckBoard.Nrom);
}
