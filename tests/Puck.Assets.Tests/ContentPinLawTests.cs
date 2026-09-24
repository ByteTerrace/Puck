using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Puck.Assets.Tests;

// The pin grammar has one parser per form. Each door that admits a pin (the release manifests, the launcher and
// official canonicalizers, the asset lock, the authority roots, the store's refs) parses through ContentPin or
// AssetContentHash, so what one door admits every door admits. These laws fix what that parser admits.
public sealed class ContentPinLawTests {
    private const string AbcHex = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void ComputePrintsTheStandardDigestInCanonicalText() {
        var pin = ContentPin.Compute(content: "abc"u8);

        Assert.Equal(
            expected: AbcHex,
            actual: pin.Hex
        );
        Assert.Equal(
            expected: $"sha256/{AbcHex}",
            actual: pin.ToString()
        );
    }
    [Fact]
    public void EveryComputeRouteAgrees() {
        var bytes = Encoding.UTF8.GetBytes(s: "the one pin");
        var expected = ContentPin.Compute(content: bytes);

        using (var stream = new MemoryStream(buffer: bytes)) {
            Assert.Equal(
                expected: expected,
                actual: ContentPin.Compute(content: stream)
            );
        }

        Assert.Equal(
            expected: expected,
            actual: ContentPin.FromDigest(digest: SHA256.HashData(source: bytes))
        );

        var path = Path.GetTempFileName();

        try {
            File.WriteAllBytes(
                bytes: bytes,
                path: path
            );
            Assert.Equal(
                expected: expected,
                actual: ContentPin.OfFile(path: path)
            );
        } finally {
            File.Delete(path: path);
        }
    }
    [Fact]
    public void CanonicalTextRoundTrips() {
        var pin = ContentPin.Compute(content: "round trip"u8);

        Assert.True(condition: ContentPin.TryParse(
            pin: out var parsed,
            text: pin.ToString()
        ));
        Assert.Equal(
            actual: parsed,
            expected: pin
        );
        Assert.True(condition: ContentPin.TryParseHex(
            hex: pin.Hex,
            pin: out var bare
        ));
        Assert.Equal(
            actual: bare,
            expected: pin
        );
        Assert.Equal(
            expected: pin,
            actual: ContentPin.Parse(text: pin.ToString())
        );
    }
    [Fact]
    public void UppercasePinIsRefusedAtParse() {
        Assert.False(condition: ContentPin.TryParse(
            pin: out _,
            text: $"sha256/{AbcHex.ToUpperInvariant()}"
        ));
        Assert.False(condition: ContentPin.TryParse(
            pin: out _,
            text: $"sha256/{AbcHex[..63]}A"
        ));
        Assert.False(condition: ContentPin.TryParseHex(
            hex: AbcHex.ToUpperInvariant(),
            pin: out _
        ));
        Assert.False(condition: ContentPin.TryParse(
            pin: out _,
            text: $"SHA256/{AbcHex}"
        ));
        _ = Assert.Throws<FormatException>(testCode: () => ContentPin.Parse(text: $"sha256/{AbcHex.ToUpperInvariant()}"));
    }
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256/")]
    [InlineData(AbcHex)]
    [InlineData(("sha256:" + AbcHex))]
    [InlineData((("sha256/" + AbcHex) + "0"))]
    [InlineData((("sha256/" + AbcHex) + " "))]
    [InlineData((" sha256/" + AbcHex))]
    [InlineData("sha256/ga7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [Theory]
    public void MalformedPinIsRefused(string? text) {
        Assert.False(condition: ContentPin.TryParse(
            pin: out _,
            text: text
        ));
    }
    [Fact]
    public void DefaultPinCarriesNoDigest() {
        _ = Assert.Throws<InvalidOperationException>(testCode: () => default(ContentPin).Hex);
        _ = Assert.Throws<ArgumentException>(testCode: () => ContentPin.FromDigest(digest: new byte[31]));
    }
    [Fact]
    public void ShortPinRoundTripsAndRefusesUppercase() {
        var hash = AssetContentHash.Compute(content: "abc"u8);

        Assert.Equal(
            expected: "sha256-64/eacf018fbf1678ba",
            actual: hash.ToString()
        );
        Assert.Equal(
            expected: hash,
            actual: AssetContentHash.FromDigest(digest: SHA256.HashData(source: "abc"u8))
        );
        Assert.True(condition: AssetContentHash.TryParse(
            hash: out var parsed,
            text: hash.ToString()
        ));
        Assert.Equal(
            actual: parsed,
            expected: hash
        );
        Assert.False(condition: AssetContentHash.TryParse(
            hash: out _,
            text: hash.ToString().ToUpperInvariant()
        ));
        Assert.False(condition: AssetContentHash.TryParse(
            hash: out _,
            text: "sha256-64/EACF018FBF1678BA"
        ));
        Assert.False(condition: AssetContentHash.TryParse(
            hash: out _,
            text: "sha256-64/eacf018fbf1678b"
        ));
        Assert.False(condition: AssetContentHash.TryParse(
            hash: out _,
            text: null
        ));
    }
    [Fact]
    public void StoreAddressesObjectsAtTheOneLayout() {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-content-pin-");

        try {
            var store = new ContentAddressedStore(root: root.FullName);
            var pin = store.Put(content: "stored"u8);

            Assert.Equal(
                expected: ContentPin.Compute(content: "stored"u8),
                actual: pin
            );
            Assert.Equal(
                expected: $"objects/sha256/{pin.Hex[..2]}/{pin.Hex}",
                actual: ContentAddressedStore.ObjectRelativePath(pin: pin)
            );
            Assert.True(condition: File.Exists(path: ContentAddressedStore.ObjectPath(
                pin: pin,
                root: root.FullName
            )));
            Assert.True(condition: store.Contains(pin: pin));
            Assert.True(condition: store.TryGet(
                content: out var content,
                pin: pin
            ));
            Assert.Equal(
                expected: "stored"u8.ToArray(),
                actual: content
            );
            store.SetRef(
                category: "tests",
                hash: pin,
                name: "stored"
            );
            Assert.True(condition: store.TryResolveRef(
                category: "tests",
                hash: out var resolved,
                name: "stored"
            ));
            Assert.Equal(
                actual: resolved,
                expected: pin
            );
            File.WriteAllText(
                contents: pin.ToString().ToUpperInvariant().Replace(
                    newValue: ContentPin.Prefix,
                    oldValue: ContentPin.Prefix.ToUpperInvariant()
                ),
                path: Path.Combine(
                    path1: root.FullName,
                    path2: "refs",
                    path3: "tests",
                    path4: "upper"
                )
            );
            Assert.False(condition: store.TryResolveRef(
                category: "tests",
                hash: out _,
                name: "upper"
            ));
        } finally {
            root.Delete(recursive: true);
        }
    }
}
