namespace Puck.World.Qr;

/// <summary>
/// A QR code's error-correction level (ISO/IEC 18004 §6.5.1) — how large a fraction of a symbol's codewords can be
/// wrong and still decode. The enum's numeric values are deliberately the level's 2-bit FORMAT-INFO indicator (a spec
/// quirk: alphabetically L, M, Q, H, but encoded as L=01, M=00, Q=11, H=10), so <see cref="QrMatrix"/> can use the
/// value directly when it builds the 15-bit format string.
/// </summary>
public enum QrErrorCorrectionLevel {
    /// <summary>~7% of codewords recoverable. Format-info indicator <c>01</c>.</summary>
    Low = 0b01,
    /// <summary>~15% of codewords recoverable — the authored default for <see cref="WorldScreenSource.Qr"/>. Format-info
    /// indicator <c>00</c>.</summary>
    Medium = 0b00,
    /// <summary>~25% of codewords recoverable. Format-info indicator <c>11</c>.</summary>
    Quartile = 0b11,
    /// <summary>~30% of codewords recoverable. Format-info indicator <c>10</c>.</summary>
    High = 0b10,
}
/// <summary>
/// The ONE spelling of the single-letter error-correction token — the document's <see cref="WorldScreenSource.Qr.EcLevel"/>
/// string, the <c>screen.source &lt;index&gt; qr</c> verb's optional argument, and every refusal that names a level all read and write it
/// through here, so an authoring-time refusal and a live refusal cannot drift apart.
/// </summary>
public static class QrErrorCorrection {
    /// <summary>The level a source authors when it names no letter — the spec's common general-purpose choice.</summary>
    public const QrErrorCorrectionLevel Default = QrErrorCorrectionLevel.Medium;
    /// <summary>The recognized letters, in refusal-message order — the exact vocabulary <see cref="TryParse"/> accepts.</summary>
    public const string Vocabulary = "L, M, Q, H";

    /// <summary>Returns the canonical single-letter token for a level — the spelling every echo and refusal prints.</summary>
    /// <param name="level">The level to spell.</param>
    /// <returns>One of <c>L</c>, <c>M</c>, <c>Q</c>, <c>H</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a defined level.</exception>
    public static string Letter(QrErrorCorrectionLevel level) => level switch {
        QrErrorCorrectionLevel.Low => "L",
        QrErrorCorrectionLevel.Medium => "M",
        QrErrorCorrectionLevel.Quartile => "Q",
        QrErrorCorrectionLevel.High => "H",
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(level),
        actualValue: level,
        message: "Unknown QR error-correction level."
    ),
    };
    /// <summary>Parses a case-insensitive single-letter level token (surrounding whitespace tolerated).</summary>
    /// <param name="text">The candidate token, or <see langword="null"/>.</param>
    /// <param name="level">The parsed level on success; <see cref="Default"/> otherwise.</param>
    /// <returns>Whether <paramref name="text"/> named a level.</returns>
    public static bool TryParse(string? text, out QrErrorCorrectionLevel level) {
        level = Default;

        if (string.IsNullOrWhiteSpace(value: text)) {
            return false;
        }

        switch (text.Trim()) {
            case "L" or "l":
                level = QrErrorCorrectionLevel.Low;

                return true;
            case "M" or "m":
                level = QrErrorCorrectionLevel.Medium;

                return true;
            case "Q" or "q":
                level = QrErrorCorrectionLevel.Quartile;

                return true;
            case "H" or "h":
                level = QrErrorCorrectionLevel.High;

                return true;
            default:
                return false;
        }
    }
}
