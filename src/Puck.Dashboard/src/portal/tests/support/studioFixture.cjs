// The studio's own test workspace and the stand-ins the machine and shell tests drive it with: an in-memory official
// tree built over tests/fixtures/workspace (every object hash-verified the way loadOfficial verifies a real one), and
// fake engines that count every call a load-independent assertion needs. Nothing here reads the game's worlds.
require('./register.cjs');
const fs = require('node:fs');
const path = require('node:path');
const { Subject } = require('rxjs');
const { resolveOfficial } = require('../../src/official/officialBase.ts');
const { sha256Hex, toHashName } = require('../../src/official/verify.ts');
const { sourceUri } = require('../../src/document/sourcePaths.ts');

const workspaceDir = path.join(__dirname, '..', 'fixtures', 'workspace');

/** Every fixture workspace file's text, keyed by its worlds-relative path. */
function readWorkspaceFixture() {
  const files = {};
  const walk = (directory, prefix) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const relative = prefix ? `${prefix}/${entry.name}` : entry.name;
      if (entry.isDirectory()) walk(path.join(directory, entry.name), relative);
      else files[relative] = fs.readFileSync(path.join(directory, entry.name), 'utf8');
    }
  };
  walk(workspaceDir, '');
  return files;
}

/** The fixture's documents, as a manifest would list them. */
const FIXTURE_DOCUMENTS = [
  { name: 'counter', source: 'counter.puck', role: 'world', documentId: 'counter' },
  { name: 'games/token', source: 'games/token.puck', role: 'fragment', documentId: null },
  { name: 'island', source: 'island.world.json', role: 'world', documentId: 'island' },
];

/** The schema bundle's root sections, enough for the Sections navigator. */
const SCHEMA_BUNDLE = {
  properties: {
    documentId: { title: 'Document id', description: 'The world this document names.' },
    state: { title: 'State', description: 'Rows the rules read and write.' },
    rules: { title: 'Rules', description: 'What happens each tick.' },
    cameras: { title: 'Cameras', description: 'Where the world is seen from.' },
  },
};

/** An in-memory official tree over the fixture workspace, served through a `fetch` stand-in. */
async function buildOfficialFixture({ files = readWorkspaceFixture(), documents = FIXTURE_DOCUMENTS, composed = [{ name: 'island', documentId: 'island' }] } = {}) {
  const official = resolveOfficial({ VITE_PUCK_OFFICIAL_BASE: 'https://official.test/off', VITE_PUCK_OFFICIAL_CHANNEL: 'dev' });
  const byUrl = new Map();
  let objects = 0;
  const put = async (text, contentType) => {
    const bytes = new TextEncoder().encode(text);
    const hash = toHashName(await sha256Hex(bytes));
    const objectPath = `objects/sha256/${String(objects++).padStart(2, '0')}/object`;
    byUrl.set(official.objectUrl(objectPath).href, () => ({ ok: true, status: 200, arrayBuffer: async () => bytes.slice().buffer }));
    return { path: objectPath, hash, size: bytes.length, contentType };
  };
  const sources = [];
  for (const [name, text] of Object.entries(files)) {
    sources.push({ name, ...(await put(text, name.endsWith('.puck') ? 'text/x-puck; charset=utf-8' : 'application/json')) });
  }
  const documentEntries = [];
  for (const document of documents) {
    documentEntries.push({ ...document, imports: [], exports: [], pin: null, ...(await put('{}', 'application/json')) });
  }
  const composedEntries = [];
  for (const entry of composed) {
    composedEntries.push({ ...entry, pin: 'sha256-64/0011223344556677', identity: null, ...(await put('{}', 'application/json')) });
  }
  const manifest = {
    schema: 'puck.official.manifest.v1',
    channel: 'dev',
    build: { commit: 'fixture', dirty: false, generator: 'tests', worldSchema: 'puck.world.definition.v1' },
    worldSchemaBundle: await put(JSON.stringify(SCHEMA_BUNDLE), 'application/schema+json'),
    engine: { entry: 'main.mjs', files: [{ name: 'main.mjs', ...(await put('export {};', 'text/javascript')) }] },
    sources,
    documents: documentEntries,
    composed: composedEntries,
    assets: [],
    signature: null,
  };
  byUrl.set(official.manifestUrl.href, () => ({ ok: true, status: 200, text: async () => JSON.stringify(manifest) }));
  const fetches = [];
  const fetchImpl = (input) => {
    const url = typeof input === 'string' ? input : input.href;
    fetches.push(url);
    const responder = byUrl.get(url);
    return Promise.resolve(responder ? responder() : { ok: false, status: 404, text: async () => '', arrayBuffer: async () => new ArrayBuffer(0) });
  };
  return { official, manifest, fetchImpl, fetches, files };
}

/** A compiled world with one topology and one register, the shape the Spatial and State views read. */
const COMPOSED_WORLD = {
  schema: 'puck.world.definition.v1',
  documentId: 'counter',
  state: {
    lattices: [{ $type: 'grid', name: 'board', width: 2, height: 2 }],
    world: [
      { name: 'score', kind: 'Int', value: 0 },
      { name: 'marks', kind: 'Int', value: 0, domain: { $type: 'cellsOf', topology: 'board' }, cells: [{ key: '1', value: 2 }] },
    ],
  },
  rules: [{ name: 'score-up', mode: 'Edge' }],
};

/**
 * A fake `WorldEngine`. Every call is counted in `calls`; `composeSource` and `compileSource` answer from overridable
 * functions; the language server channel is a Subject the test publishes into.
 */
function fakeEngine(overrides = {}) {
  const calls = { mountSources: 0, writeSource: 0, compileSource: 0, composeSource: 0, compile: 0, release: 0, cells: 0, dispose: 0 };
  const mounted = new Map();
  const channel = { sent: [], messages$: new Subject(), closed: false };
  const engine = {
    publish(pathName, version, diagnostics = []) {
      channel.messages$.next({ jsonrpc: '2.0', method: 'textDocument/publishDiagnostics', params: { uri: sourceUri(pathName), version, diagnostics } });
    },
    async version() { return { schemaVersion: 'puck.world.definition.v1', engine: 'fake', commit: 'fixture' }; },
    async mountSources(files) { calls.mountSources++; mounted.clear(); for (const [name, text] of Object.entries(files)) mounted.set(name, text); },
    async writeSource(name, text) { calls.writeSource++; mounted.set(name, text); },
    async compileSource(name) {
      calls.compileSource++;
      return overrides.compileSource ? overrides.compileSource(name, mounted)
        : { ok: true, document: JSON.stringify(COMPOSED_WORLD), worlds: [], diagnostics: [], sourceMap: { '/rules/0': { path: name, line: 11, column: 1, length: 4, module: null } } };
    },
    async composeSource(name) {
      calls.composeSource++;
      return overrides.composeSource ? overrides.composeSource(name, mounted) : { ok: true, composed: JSON.stringify(COMPOSED_WORLD), diagnostics: [] };
    },
    async compile() { calls.compile++; return { ok: true, handle: `handle-${calls.compile}` }; },
    async rows() { return [{ name: 'score', kind: 'Int', keyed: false, cells: [{ key: '$value', value: { kind: 'Int', value: 0n } }] }]; },
    async stateHash() { return 'hash-0'; },
    async judge(handle, tick) { return { ok: true, trace: { rules: [{ name: 'score-up', mode: 'Edge', evaluations: [] }], writes: [], refusals: [], hostFacts: [] } }; },
    async release() { calls.release++; },
    async writeRow() { return { ok: true }; },
    async evaluate() { return { ok: true, value: 1n }; },
    async cells() {
      calls.cells++;
      return { ok: true, cells: [0, 1, 2, 3].map((ordinal) => ({ ordinal, key: String(ordinal), x: ordinal % 2, y: Math.floor(ordinal / 2), z: 0 })) };
    },
    async lsp() { return []; },
    async lspIdle() { return { ran: false, pending: false, messages: [] }; },
    languageServer() {
      return {
        send: (message) => channel.sent.push(JSON.parse(message)),
        messages: () => channel.messages$.asObservable(),
        close: () => { channel.closed = true; },
      };
    },
    async dispose() { calls.dispose++; },
  };
  // The test's handles stay out of the machine's persisted context, as a real engine's internals do.
  for (const [name, value] of Object.entries({ calls, mounted, channel })) Object.defineProperty(engine, name, { value, enumerable: false });
  return engine;
}

/** A boot seam that hands out `engines` in order: the language engine first, then the world engine. */
function bootSequence(...engines) {
  const booted = [];
  const options = [];
  const bootEngine = async (_official, bootOptions) => {
    const next = engines[booted.length];
    if (!next) throw new Error('no more fake engines to boot');
    booted.push(next);
    options.push(bootOptions);
    return next;
  };
  return { bootEngine, booted, options };
}

function memoryStorage() {
  const entries = new Map();
  return { getItem: (key) => entries.get(key) ?? null, setItem: (key, value) => entries.set(key, value) };
}

module.exports = { readWorkspaceFixture, buildOfficialFixture, fakeEngine, bootSequence, memoryStorage, COMPOSED_WORLD, FIXTURE_DOCUMENTS };
