// Boots the published Puck.World.Browser AppBundle under Node and drives its .puck authoring surface through the real
// [JSExport]s: a workspace mounted into the runtime's in-memory file system, compiled, composed and served by the
// language server, all by the engine's own toolchain. Run it after `dotnet publish src/Puck.World.Browser -c Release`:
//
//   node --test tests/Puck.World.Browser.Tests/wasm/sources.test.mjs
//
// The fixtures are this suite's own (Fixtures/sources); nothing here reads the shipped worlds.
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "..", "..");
const mainMjs = join(root, "src", "Puck.World.Browser", "bin", "Release", "net10.0", "browser-wasm", "AppBundle", "main.mjs");
const fixtures = join(here, "..", "Fixtures", "sources");
const broken = 'schema: "puck.world.definition.v1"\nbasis: "../base"\n\nhost: {\n  width: 320\n}\n';
// A fragment that compiles alone and whose two rules share one name, which only the composed world refuses.
const duplicated = 'schema: "puck.world.definition.v1"\n\nstate {\n  world {\n    table ticks {\n      count = 0\n    }\n  }\n}\n\nrule "tick" {\n  ticks[count] += 1\n}\n\nrule "tick" {\n  ticks[count] += 2\n}\n';
const host = { presentation: "Windowed", backend: "auto", width: 800, height: 600, surfaceFormat: "r8g8b8a8", fullscreen: false, presentMode: "Immediate", targetHertz: 60, exitAfterSeconds: 0 };
// JSON documents beside sources: a JSON root importing a source, a source over a JSON basis, and a JSON root importing
// the broken fragment.
const mixed = {
    "games/duplicated.puck": duplicated,
    "games/bonus.puck": 'schema: "puck.world.definition.v1"\n\nstate {\n  world {\n    table bonus {\n      stars = 3\n    }\n  }\n}\n',
    "hub.world.json": JSON.stringify({ schema: "puck.world.definition.v1", basis: "base", imports: [{ document: "games/bonus" }] }),
    "plain.world.json": JSON.stringify({ schema: "puck.world.definition.v1", host }),
    "games/over-json.puck": 'schema: "puck.world.definition.v1"\nbasis: "../plain"\n',
    "duplicated-host.world.json": JSON.stringify({ schema: "puck.world.definition.v1", basis: "base", imports: [{ document: "games/duplicated" }] }),
};

function sources(directory) {
    const files = {};

    for (const name of readdirSync(directory)) {
        const path = join(directory, name);

        if (statSync(path).isDirectory()) {
            Object.assign(files, sources(path));
        } else if (name.endsWith(".puck")) {
            files[relative(fixtures, path).replaceAll("\\", "/")] = readFileSync(path, "utf8");
        }
    }

    return files;
}

if (!existsSync(mainMjs)) {
    test(`wasm sources (SKIPPED: no AppBundle at ${mainMjs} — run 'dotnet publish src/Puck.World.Browser -c Release')`, { skip: true }, () => {});
} else {
    const engineReady = (async () => {
        const { createEngine } = await import(pathToFileURL(mainMjs).href);
        const engine = await createEngine();
        const mounted = JSON.parse(engine.MountSources(JSON.stringify({ ...sources(fixtures), ...mixed, "games/broken.puck": broken })));

        assert.deepEqual(mounted, { ok: true, error: null });

        return engine;
    })();
    const uri = (path) => `file:///worlds/${path}`;
    let nextId = 1;
    const request = (method, params) => JSON.stringify({ jsonrpc: "2.0", id: nextId++, method, params });
    const notify = (method, params) => JSON.stringify({ jsonrpc: "2.0", method, params });

    test("CompileSource compiles a game whose basis is a source, with its source map", async () => {
        const engine = await engineReady;
        const result = JSON.parse(engine.CompileSource("games/counter.puck"));

        assert.equal(result.ok, true, JSON.stringify(result.diagnostics));
        assert.equal(result.diagnostics.filter((diagnostic) => diagnostic.severity === "error").length, 0);
        assert.equal(JSON.parse(result.document).basis, "../base");
        assert.deepEqual(result.worlds, []);
        assert.ok(Object.values(result.sourceMap).some((origin) => origin.path === "games/counter.puck" && origin.line > 0));
    });

    test("CompileSource reports a broken source's diagnostic where it was written, 1-based", async () => {
        const engine = await engineReady;
        const result = JSON.parse(engine.CompileSource("games/broken.puck"));
        const diagnostic = result.diagnostics.find((entry) => entry.code === "PUCK040");

        assert.equal(result.ok, false);
        assert.equal(result.document, null);
        assert.ok(diagnostic, JSON.stringify(result.diagnostics));
        assert.equal(diagnostic.severity, "error");
        assert.equal(diagnostic.path, "games/broken.puck");
        assert.equal(diagnostic.line, 4);
        assert.equal(diagnostic.column, 1);
    });

    test("ComposeSource folds the source basis into one standalone document", async () => {
        const engine = await engineReady;
        const result = JSON.parse(engine.ComposeSource("games/counter.puck"));

        assert.equal(result.ok, true, JSON.stringify(result.diagnostics));

        const composed = JSON.parse(result.composed);

        assert.equal(composed.basis, undefined);
        assert.equal(composed.host.width, 640);
        assert.ok(result.composed.includes('"score"'));

        const compiled = JSON.parse(engine.Compile(result.composed));

        assert.equal(compiled.ok, true, JSON.stringify(compiled.errors));
    });

    test("ComposeSource composes a JSON root that imports a source, and a source over a JSON basis", async () => {
        const engine = await engineReady;
        const hub = JSON.parse(engine.ComposeSource("hub.world.json"));

        assert.equal(hub.ok, true, JSON.stringify(hub.diagnostics));
        assert.equal(JSON.parse(hub.composed).host.width, 640);
        assert.ok(hub.composed.includes('"bonus"'));

        const over = JSON.parse(engine.ComposeSource("games/over-json.puck"));

        assert.equal(over.ok, true, JSON.stringify(over.diagnostics));
        assert.equal(JSON.parse(over.composed).host.width, 800);

        const plain = JSON.parse(engine.CompileSource("plain.world.json"));

        assert.equal(plain.ok, true, JSON.stringify(plain.diagnostics));
        assert.equal(JSON.parse(plain.document).host.height, 600);
    });

    test("ComposeSource validates the composed world and still hands it back", async () => {
        const engine = await engineReady;
        const result = JSON.parse(engine.ComposeSource("duplicated-host.world.json"));

        assert.equal(result.ok, false);
        assert.notEqual(result.composed, null);
        assert.ok(result.diagnostics.some((diagnostic) => diagnostic.code === "PUCK030" && diagnostic.severity === "error" && diagnostic.message.includes("tick")), JSON.stringify(result.diagnostics));
    });

    test("WriteSource refuses a path outside the workspace", async () => {
        const engine = await engineReady;

        assert.equal(JSON.parse(engine.WriteSource("../escape.puck", "")).ok, false);
        assert.equal(JSON.parse(engine.WriteSource("/worlds/absolute.puck", "")).ok, false);
    });

    test("the language server answers initialize, diagnoses when idle, completes and highlights", async () => {
        const engine = await engineReady;
        const counter = sources(fixtures)["games/counter.puck"];
        const initialize = JSON.parse(engine.Lsp(request("initialize", { capabilities: {} })));
        const legend = initialize[0].result.capabilities.semanticTokensProvider.legend;

        assert.ok(legend.tokenTypes.includes("property"));

        // An open never diagnoses inline; the idle turn does, once, at the opened version.
        assert.deepEqual(JSON.parse(engine.Lsp(notify("textDocument/didOpen", { textDocument: { uri: uri("games/broken.puck"), languageId: "puck", version: 1, text: broken } }))), []);
        assert.deepEqual(JSON.parse(engine.Lsp(notify("textDocument/didOpen", { textDocument: { uri: uri("games/counter.puck"), languageId: "puck", version: 3, text: counter } }))), []);

        const published = [];

        for (let turn = 0; turn < 10; turn++) {
            const idle = JSON.parse(engine.LspIdle());

            published.push(...idle.messages);
            if (!idle.pending) {
                break;
            }
        }

        assert.equal(JSON.parse(engine.LspIdle()).ran, false);
        // At full depth (the default) a clean source publishes its source tier, then both tiers as a unit of its own;
        // a broken one has no semantic tier.
        assert.deepEqual(published.map((message) => [message.params.uri, message.params.version]), [[uri("games/counter.puck"), 3], [uri("games/counter.puck"), 3], [uri("games/broken.puck"), 1]]);
        assert.deepEqual(published[1].params.diagnostics.filter((diagnostic) => diagnostic.severity === 1), []);
        assert.ok(published[2].params.diagnostics.some((diagnostic) => diagnostic.code === "PUCK040"));

        const completion = JSON.parse(engine.Lsp(request("textDocument/completion", { textDocument: { uri: uri("games/counter.puck") }, position: { line: 0, character: 0 } })));

        assert.ok(completion[0].result.items.some((item) => item.label === "schema"));

        const tokens = JSON.parse(engine.Lsp(request("textDocument/semanticTokens/full", { textDocument: { uri: uri("games/counter.puck") } })));
        const data = tokens[0].result.data;

        assert.ok(data.length > 0);
        assert.equal(data.length % 5, 0);
        // The first token is the opening comment on line 0.
        assert.deepEqual(data.slice(0, 5), [0, 0, counter.indexOf("\n"), legend.tokenTypes.indexOf("comment"), 0]);
    });
}
