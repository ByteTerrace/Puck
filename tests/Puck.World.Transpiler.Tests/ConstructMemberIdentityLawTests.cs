using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lsp;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A member's identity is (construct, name): a hover inside one construct describes THAT construct's
/// member, even where several constructs describe a member of the same name.</summary>
/// <remarks>Driven through the real language server over stdio, so it reads what an editor is handed rather than
/// the table directly. Only members written as their own word — a property, a modifier, or a cell modifier — can be
/// under a cursor; a header or body member is written as its value.</remarks>
public class ConstructMemberIdentityLawTests {
    private static string? ReadRpcMessage(Stream stream) {
        var header = new List<byte>();
        var length = -1;

        while (true) {
            var next = stream.ReadByte();

            if (next == -1) {
                return null;
            }
            header.Add(item: ((byte)next));
            if (
                (header.Count >= 4) &&
                (header[^4] == '\r') &&
                (header[^3] == '\n') &&
                (header[^2] == '\r') &&
                (header[^1] == '\n')
            ) {
                foreach (var line in Encoding.ASCII.GetString(bytes: [.. header]).Split(
                    options: StringSplitOptions.RemoveEmptyEntries,
                    separator: ["\r\n"]
                )) {
                    if (line.StartsWith(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: "Content-Length:"
                    )) {
                        _ = int.TryParse(
                            result: out length,
                            s: line["Content-Length:".Length..].Trim()
                        );
                    }
                }
                break;
            }
        }
        if (length <= 0) {
            return null;
        }

        var body = new byte[length];
        var read = 0;

        while (read < length) {
            var count = stream.Read(
                buffer: body,
                count: (length - read),
                offset: read
            );

            if (count == 0) {
                return null;
            }
            read += count;
        }

        return Encoding.UTF8.GetString(bytes: body);
    }
    private static void WriteRpcMessage(Stream stream, string json) {
        var bytes = Encoding.UTF8.GetBytes(s: json);

        stream.Write(buffer: Encoding.ASCII.GetBytes(s: $"Content-Length: {bytes.Length}\r\n\r\n"));
        stream.Write(buffer: bytes);
        stream.Flush();
    }
    // The hover card the real language server returns for the cursor at `offset` in `source`.
    private static async Task<string> HoverAtAsync(string source, int offset) {
        var before = source[..offset];
        var line = before.Count(predicate: static character => (character == '\n'));
        var character = ((offset - before.LastIndexOf(value: '\n')) - 1);

        using var clientToServer = new MemoryStream();
        using var serverToClient = new MemoryStream();

        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}",
            stream: clientToServer
        );
        WriteRpcMessage(
            json: new JsonObject {
                ["jsonrpc"] = "2.0",
                ["method"] = "textDocument/didOpen",
                ["params"] = new JsonObject {
                    ["textDocument"] = new JsonObject {
                        ["uri"] = "file:///identity.puck",
                        ["languageId"] = "puck",
                        ["version"] = 1,
                        ["text"] = source,
                    },
                },
            }.ToJsonString(),
            stream: clientToServer
        );
        WriteRpcMessage(
            json: new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "textDocument/hover",
                ["params"] = new JsonObject {
                    ["textDocument"] = new JsonObject { ["uri"] = "file:///identity.puck" },
                    ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
                },
            }.ToJsonString(),
            stream: clientToServer
        );
        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"shutdown\"}",
            stream: clientToServer
        );
        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}",
            stream: clientToServer
        );
        clientToServer.Position = 0;
        await new PuckLanguageServer(
            clientToServer,
            serverToClient
        ).RunAsync(cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        serverToClient.Position = 0;

        while (ReadRpcMessage(stream: serverToClient) is { } message) {
            var parsed = JsonNode.Parse(json: message);

            if (parsed?["id"]?.GetValue<int>() == 2) {
                return (parsed["result"]?["contents"]?["value"]?.ToString() ?? "");
            }
        }

        return "";
    }
    // The offset of `name` written as a member of `keyword`: on the keyword's own line where a header or modifier
    // member is written, else the first place the word appears, which is inside the construct's body for a probe
    // that authors one construct. Returns -1 when the probe never writes it.
    private static int MemberOffset(string source, string keyword, string name) {
        var line = 0;

        foreach (var text in source.Split(separator: '\n')) {
            var trimmed = text.TrimStart();

            if (trimmed.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: $"{keyword} "
            )) {
                var within = WordOffset(
                    name: name,
                    source: text
                );

                if (within > 0) {
                    return (line + within);
                }
            }
            line += (text.Length + 1);
        }

        return WordOffset(
            name: name,
            source: source
        );
    }
    // The offset of `name` written as its own word in `source`, or -1.
    private static int WordOffset(string source, string name) {
        for (var index = source.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: name
        ); (index >= 0); index = source.IndexOf(
            comparisonType: StringComparison.Ordinal,
            startIndex: (index + 1),
            value: name
        )) {
            var beforeOk = ((index == 0) || !(char.IsLetterOrDigit(c: source[index - 1]) || (source[index - 1] is '_' or '"')));
            var after = (index + name.Length);
            var afterOk = ((after >= source.Length) || !(char.IsLetterOrDigit(c: source[after]) || (source[after] is '_' or '"')));

            if (beforeOk && afterOk) {
                return index;
            }
        }

        return -1;
    }

    public static TheoryData<string> Described() => new(values: WorldConstructs.Table.Keywords);

    [Fact]
    public void AMemberNameOwnedByOneConstructResolvesWithoutContext() {
        var table = WorldConstructs.Table;

        Assert.True(condition: table.TryGetMember(
            enclosing: null,
            member: out var member,
            name: "periodSeconds",
            owner: out var owner
        ));
        Assert.Equal(
            actual: owner!.Keyword,
            expected: "decision"
        );
        Assert.Equal(
            actual: member!.Name,
            expected: "periodSeconds"
        );

        // A name several constructs own resolves to none of them without context, and its card names every owner.
        Assert.False(condition: table.TryGetMember(
            enclosing: null,
            member: out _,
            name: "capacity",
            owner: out _
        ));

        var card = (WorldConstructLanguageServices.Hover(
            table: table,
            word: "capacity"
        ) ?? "");

        foreach (var candidate in table.Owners(name: "capacity")) {
            Assert.Contains(
                expectedSubstring: $"A `{candidate.Keyword}` member",
                actualString: card,
                comparisonType: StringComparison.Ordinal
            );
        }
    }
    [MemberData(nameof(Described))]
    [Theory]
    public async Task HoverInsideAConstructDescribesThatConstructsMember(string keyword) {
        var table = WorldConstructs.Table;

        Assert.True(condition: table.TryGet(
            construct: out var construct,
            keyword: keyword
        ));

        if (!ConstructProbes.Sources.TryGetValue(
            key: keyword,
            value: out var probes
        )) {
            return;
        }

        var failures = new List<string>();
        // A cell entry's key and value are positional, so only its optional modifiers are words a cursor can sit
        // on; a header or body member is written as its value.
        var shared = construct!.Members
            .Where(predicate: static member => (
                (member.Position is WorldMemberPosition.Property or WorldMemberPosition.Modifier) ||
                ((member.Position == WorldMemberPosition.Cell) && !member.Required)
            ))
            .Select(selector: static member => member.Name)
            .Distinct(comparer: StringComparer.Ordinal)
            .Where(predicate: name => (table.Owners(name: name).Count > 1))
            .Order(comparer: StringComparer.Ordinal);

        foreach (var name in shared) {
            Assert.True(condition: construct.TryGetMember(
                member: out var member,
                name: name
            ));

            var probe = probes.FirstOrDefault(predicate: candidate => (MemberOffset(
                keyword: keyword,
                name: name,
                source: candidate.Source
            ) >= 0));

            if (probe is null) {
                failures.Add(item: $"{keyword}.{name}: no probe writes the member, so no cursor can be put on it");

                continue;
            }

            var card = await HoverAtAsync(
                offset: MemberOffset(
                    keyword: keyword,
                    name: name,
                    source: probe.Source
                ),
                source: probe.Source
            ).ConfigureAwait(continueOnCapturedContext: true);

            // Either reader may answer, and both name the owner: the construct table says "A `x` member", the
            // schema hover heads its card "**x.member**". Naming another construct's is the failure.
            if (
                !card.Contains(
                comparisonType: StringComparison.Ordinal,
                value: $"A `{keyword}` member"
            ) &&
                !card.Contains(
                comparisonType: StringComparison.Ordinal,
                value: $"{keyword}.{name}"
            )
            ) {
                failures.Add(item: $"{keyword}.{name}: the card does not attribute the member to '{keyword}': {card.ReplaceLineEndings(replacementText: " ")}");

                continue;
            }
            if (
                card.Contains(
                comparisonType: StringComparison.Ordinal,
                value: $"A `{keyword}` member"
            ) &&
                !card.Contains(
                comparisonType: StringComparison.Ordinal,
                value: member!.Summary
            )
            ) {
                failures.Add(item: $"{keyword}.{name}: the card does not carry the member's own text: {card.ReplaceLineEndings(replacementText: " ")}");
            }
        }

        Assert.True(
            condition: (failures.Count == 0),
            userMessage: string.Join(
                separator: Environment.NewLine,
                values: failures
            )
        );
    }
}
