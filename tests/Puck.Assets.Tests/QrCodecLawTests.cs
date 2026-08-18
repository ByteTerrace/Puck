using System.IO.Hashing;
using Puck.Assets.Qr;
using Xunit;

namespace Puck.Assets.Tests;

public sealed class QrCodecLawTests {
    // Packs the module grid, one bit per module (row-major, MSB first), prefixed with the header fields a scanner
    // also reads from the symbol itself — two matrices with the same fingerprint carry the identical grid.
    private static string Fingerprint(QrMatrix matrix) {
        var bits = new byte[((matrix.Size * matrix.Size) + 7) / 8];
        var bitIndex = 0;

        for (var row = 0; (row < matrix.Size); row++) {
            for (var col = 0; (col < matrix.Size); col++) {
                if (matrix.IsDark(
                    col: col,
                    row: row
                )) {
                    bits[(bitIndex / 8)] |= ((byte)(0x80 >> (bitIndex % 8)));
                }

                bitIndex++;
            }
        }

        var header = new byte[] { ((byte)matrix.Version), ((byte)matrix.Level), ((byte)matrix.MaskPattern), ((byte)matrix.Size) };
        var payload = new byte[(header.Length + bits.Length)];

        header.CopyTo(
            array: payload,
            index: 0
        );
        bits.CopyTo(
            array: payload,
            index: header.Length
        );

        return Convert.ToHexStringLower(bytes: XxHash64.Hash(source: payload));
    }

    [Fact]
    public void EncodingAKnownPayloadProducesAPinnedMatrixFingerprint() {
        var ok = QrEncoder.TryEncode(
            error: out _,
            level: QrErrorCorrectionLevel.Medium,
            matrix: out var matrix,
            payload: "https://puck.game/w/1"
        );

        Assert.True(condition: ok);
        Assert.Equal(
            expected: "612be0d08aa1580f",
            actual: Fingerprint(matrix: matrix!)
        );
    }
    [Fact]
    public void FlippingOneCodewordByteChangesTheFingerprint() {
        var plan = QrCapacityTable.BlockPlan(
            level: QrErrorCorrectionLevel.Low,
            version: 1
        );
        var codewords = new byte[plan.TotalCodewords];

        for (var i = 0; (i < codewords.Length); i++) {
            codewords[i] = ((byte)((i * 37) + 11));
        }

        var baseline = QrMatrix.Build(
            codewords: codewords,
            level: QrErrorCorrectionLevel.Low,
            version: 1
        );

        codewords[0] ^= 0x01; // flips one bit of one data codeword, nothing else

        var flipped = QrMatrix.Build(
            codewords: codewords,
            level: QrErrorCorrectionLevel.Low,
            version: 1
        );

        Assert.NotEqual(
            expected: Fingerprint(matrix: baseline),
            actual: Fingerprint(matrix: flipped)
        );
    }
}
