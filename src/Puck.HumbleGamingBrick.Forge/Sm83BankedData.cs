using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// Read-only payloads too bulky for the fixed data window, packed into the switchable banks. A table never straddles a
/// bank, so a single select makes all of it readable at 0x4000.
/// </summary>
/// <remarks>
/// Only data read while its bank is selected belongs here. Anything the running game reads at an arbitrary moment —
/// a music stream the sequencer walks every frame, a screen a blit paints — must stay in the fixed window, because
/// nothing re-selects its bank first.
/// </remarks>
internal sealed class Sm83BankedData {
    /// <summary>A payload's home: the bank holding it and the address it appears at while that bank is selected.</summary>
    /// <param name="Bank">The bank number, two or above.</param>
    /// <param name="Address">The bus address within the switchable window.</param>
    internal readonly record struct Placement(int Bank, ushort Address);

    private readonly List<List<byte>> m_banks = [];

    /// <summary>Gets the packed banks, in order from bank two.</summary>
    public IReadOnlyList<byte[]> Banks => [.. m_banks.Select(selector: static bank => bank.ToArray())];

    /// <summary>Places a payload in the first bank with room for it.</summary>
    /// <param name="bytes">The payload; it must fit one bank.</param>
    /// <returns>Where the payload landed.</returns>
    /// <exception cref="ArgumentException">The payload is larger than one bank.</exception>
    public Placement Add(byte[] bytes) {
        ArgumentNullException.ThrowIfNull(argument: bytes);

        if (bytes.Length > FrameworkCartridge.BankSize) {
            throw new ArgumentException(message: $"A {bytes.Length} byte payload does not fit the {FrameworkCartridge.BankSize}-byte bank window.", paramName: nameof(bytes));
        }

        for (var index = 0; index < m_banks.Count; ++index) {
            if (m_banks[index].Count + bytes.Length <= FrameworkCartridge.BankSize) {
                var address = (ushort)(RomDataBuilder.BaseAddress + m_banks[index].Count);
                m_banks[index].AddRange(collection: bytes);

                return new Placement(Bank: index + 2, Address: address);
            }
        }

        m_banks.Add(item: [.. bytes]);

        return new Placement(Bank: m_banks.Count + 1, Address: RomDataBuilder.BaseAddress);
    }

    /// <summary>Emits the write that pages a bank into the switchable window.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="bank">The bank number.</param>
    /// <remarks>The mapper latches the low eight bits from a write anywhere in 0x2000..0x2FFF.</remarks>
    public static void EmitSelect(Sm83Emitter emitter, int bank) {
        ArgumentNullException.ThrowIfNull(argument: emitter);

        emitter.LoadAImmediate(value: (byte)(bank & 0xFF));
        emitter.StoreAToAddress(address: 0x2000);
    }
}
