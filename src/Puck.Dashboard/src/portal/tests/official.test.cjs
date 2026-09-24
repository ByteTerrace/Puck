const assert = require('node:assert/strict');
const { test } = require('node:test');

// Exercise the shipped TypeScript through Node's test runner, without a second bundler — same
// pattern as tests/offline-preview.test.cjs.
require('./support/register.cjs');

const { resolveOfficial, OfficialConfigError } = require('../src/official/officialBase.ts');
const { parseManifest, ManifestRefusal, describeTree, NO_COMMIT } = require('../src/official/manifest.ts');
const { sha256Hex, toHashName, verifyBytes, OfficialRefusal } = require('../src/official/verify.ts');
const { createByteStore } = require('../src/official/byteStore.ts');
const { loadOfficial, generatorCommit } = require('../src/official/officialClient.ts');

const OFFICIAL_ENV = { VITE_PUCK_OFFICIAL_BASE: 'https://official.test/off', VITE_PUCK_OFFICIAL_CHANNEL: 'dev' };

function textBytes(text) {
  return new TextEncoder().encode(text);
}

async function buildFixture() {
  const official = resolveOfficial(OFFICIAL_ENV);

  const schemaBundleText = JSON.stringify({ 'x-puck': { schemaVersion: 'puck.world.definition.v1' } });
  const schemaBundleBytes = textBytes(schemaBundleText);
  const schemaBundleHash = toHashName(await sha256Hex(schemaBundleBytes));

  const documentText = JSON.stringify({ schema: 'puck.world.definition.v1', documentId: 'puck' });
  const documentBytes = textBytes(documentText);
  const documentHash = toHashName(await sha256Hex(documentBytes));

  const sourceText = 'schema: "puck.world.definition.v1"\n';
  const sourceBytes = textBytes(sourceText);
  const sourceHash = toHashName(await sha256Hex(sourceBytes));

  const composedText = JSON.stringify({ schema: 'puck.world.definition.v1', composed: true });
  const composedBytes = textBytes(composedText);
  const composedHash = toHashName(await sha256Hex(composedBytes));

  const assetText = JSON.stringify({ schema: 'puck.music.v1' });
  const assetBytes = textBytes(assetText);
  const assetHash = toHashName(await sha256Hex(assetBytes));

  const mainMjsBytes = textBytes('export function createEngine(){return {};}\n');
  const mainMjsHash = toHashName(await sha256Hex(mainMjsBytes));
  const dotnetJsBytes = textBytes('// dotnet.js stand-in\n');
  const dotnetJsHash = toHashName(await sha256Hex(dotnetJsBytes));

  const manifest = {
    schema: 'puck.official.manifest.v1',
    channel: 'dev',
    build: { commit: 'abc123', dirty: false, generator: 'Puck.World.WorldSchema', worldSchema: 'puck.world.definition.v1' },
    worldSchemaBundle: { path: 'objects/sha256/aa/schemabundle', hash: schemaBundleHash, size: schemaBundleBytes.length, contentType: 'application/schema+json' },
    engine: {
      entry: 'main.mjs',
      files: [
        { name: 'main.mjs', path: 'objects/sha256/bb/mainmjs', hash: mainMjsHash, size: mainMjsBytes.length, contentType: 'text/javascript' },
        { name: '_framework/dotnet.js', path: 'objects/sha256/cc/dotnetjs', hash: dotnetJsHash, size: dotnetJsBytes.length, contentType: 'text/javascript' },
      ],
    },
    sources: [
      { name: 'games/tictactoe.puck', path: 'objects/sha256/ab/source', hash: sourceHash, size: sourceBytes.length, contentType: 'text/x-puck; charset=utf-8' },
    ],
    documents: [
      { name: 'games/tictactoe', source: 'games/tictactoe.puck', role: 'fragment', documentId: null, imports: [], exports: ['tictactoe'], path: 'objects/sha256/dd/document', hash: documentHash, size: documentBytes.length, contentType: 'application/json', pin: 'sha256-64/0011223344556677' },
    ],
    composed: [
      { documentId: 'puck', name: 'puck', path: 'objects/sha256/ee/composed', hash: composedHash, size: composedBytes.length, contentType: 'application/json', pin: 'sha256-64/8899aabbccddeeff', identity: null },
    ],
    assets: [
      { family: 'music', name: 'theme', source: 'music/theme.json', path: 'objects/sha256/ff/asset', hash: assetHash, size: assetBytes.length, contentType: 'application/json', pin: null },
    ],
    signature: null,
  };
  const manifestText = JSON.stringify(manifest);

  const byUrl = new Map();
  byUrl.set(official.manifestUrl.href, () => ({ ok: true, status: 200, text: async () => manifestText }));
  const registerObject = (path, bytes) => {
    byUrl.set(official.objectUrl(path).href, () => ({ ok: true, status: 200, arrayBuffer: async () => bytes.buffer }));
  };
  registerObject(manifest.worldSchemaBundle.path, schemaBundleBytes);
  registerObject(manifest.sources[0].path, sourceBytes);
  registerObject(manifest.documents[0].path, documentBytes);
  registerObject(manifest.composed[0].path, composedBytes);
  registerObject(manifest.assets[0].path, assetBytes);
  registerObject(manifest.engine.files[0].path, mainMjsBytes);
  registerObject(manifest.engine.files[1].path, dotnetJsBytes);

  function fakeFetch(input) {
    const url = typeof input === 'string' ? input : input.href;
    const responder = byUrl.get(url);
    if (!responder) {
      return Promise.resolve({ ok: false, status: 404, text: async () => '', arrayBuffer: async () => new ArrayBuffer(0) });
    }
    return Promise.resolve(responder());
  }

  return {
    official, manifest, manifestText, fakeFetch, byUrl,
    documentText, documentHash, sourceText, composedText, assetText,
    documentPath: manifest.documents[0].path,
  };
}

test('officialBase.resolveOfficial joins root/channel/objects without import.meta', () => {
  const official = resolveOfficial(OFFICIAL_ENV);
  assert.equal(official.root.href, 'https://official.test/off/');
  assert.equal(official.channel, 'dev');
  assert.equal(official.manifestUrl.href, 'https://official.test/off/dev/manifest.json');
  assert.equal(official.objectUrl('objects/sha256/ab/abcd1234').href, 'https://official.test/off/objects/sha256/ab/abcd1234');

  // A relative base resolves against the supplied document href, matching the studio's own
  // "/official" dev-proxy configuration.
  const relative = resolveOfficial({ VITE_PUCK_OFFICIAL_BASE: '/official', VITE_PUCK_OFFICIAL_CHANNEL: 'dev' }, 'http://localhost:61101/');
  assert.equal(relative.root.href, 'http://localhost:61101/official/');
  assert.equal(relative.manifestUrl.href, 'http://localhost:61101/official/dev/manifest.json');

  assert.throws(() => resolveOfficial({ VITE_PUCK_OFFICIAL_CHANNEL: 'dev' }), OfficialConfigError);
  assert.throws(() => resolveOfficial({ VITE_PUCK_OFFICIAL_BASE: '/official' }), OfficialConfigError);
});

test('manifest.parseManifest refuses an unknown schema, a foreign build.worldSchema, and a manifest with no sources[] by name', () => {
  assert.throws(() => parseManifest('not json'), ManifestRefusal);
  assert.throws(
    () => parseManifest(JSON.stringify({ schema: 'not.a.real.schema', build: { worldSchema: 'puck.world.definition.v1' } })),
    /not\.a\.real\.schema/,
  );
  assert.throws(
    () => parseManifest(JSON.stringify({ schema: 'puck.official.manifest.v1', build: { worldSchema: 'not.a.real.schema' } })),
    /not\.a\.real\.schema/,
  );
  assert.throws(
    () => parseManifest(JSON.stringify({ schema: 'puck.official.manifest.v1', build: { worldSchema: 'puck.world.definition.v1' } })),
    /sources\[\]/,
  );
});

test('manifest.parseManifest refuses an object hash that is not sha256 over 64 lowercase hex digits, naming its field', () => {
  const lower = `sha256/${'ab'.repeat(32)}`;
  const manifestWith = (patch) => JSON.stringify({
    schema: 'puck.official.manifest.v1',
    build: { worldSchema: 'puck.world.definition.v1' },
    worldSchemaBundle: { path: 'objects/sha256/aa/bundle', hash: lower },
    engine: { entry: 'main.mjs', files: [{ name: 'main.mjs', path: 'objects/sha256/bb/main', hash: lower }] },
    sources: [{ name: 'counter.puck', path: 'objects/sha256/cc/source', hash: lower }],
    documents: [], composed: [], assets: [],
    ...patch,
  });

  assert.doesNotThrow(() => parseManifest(manifestWith({})));
  assert.throws(
    () => parseManifest(manifestWith({ sources: [{ name: 'counter.puck', path: 'objects/sha256/cc/source', hash: `sha256/${'AB'.repeat(32)}` }] })),
    /sources\[0\]\.hash .*64 lowercase hex/,
  );
  assert.throws(
    () => parseManifest(manifestWith({ engine: { entry: 'main.mjs', files: [{ name: 'main.mjs', path: 'objects/sha256/bb/main', hash: `sha256/${'ab'.repeat(31)}` }] } })),
    /engine\.files\[0\]\.hash/,
  );
  assert.throws(
    () => parseManifest(manifestWith({ worldSchemaBundle: { path: 'objects/sha256/aa/bundle', hash: `sha512/${'ab'.repeat(32)}` } })),
    /worldSchemaBundle\.hash/,
  );
});

test('manifest.describeTree names the worlds tree a build was read from, and says so when no commit holds it', () => {
  assert.equal(describeTree({ commit: '590e07499670c8e9686bd8a9384d184ace52fa2e', dirty: false }), 'commit 590e07499670');
  assert.equal(describeTree({ commit: '590e07499670c8e9686bd8a9384d184ace52fa2e', dirty: true }), 'commit 590e07499670 + local edits');
  assert.equal(describeTree({ commit: NO_COMMIT, dirty: true }), 'worlds tree outside git');
  assert.equal(NO_COMMIT, 'none');
});

test('officialClient.generatorCommit reads the schema bundle\'s own x-puck.commit, never build.commit', () => {
  assert.equal(generatorCommit({ schemaBundle: { 'x-puck': { schemaVersion: 'puck.world.definition.v1', commit: 'feedface' } } }), 'feedface');
  assert.equal(generatorCommit({ schemaBundle: { 'x-puck': { schemaVersion: 'puck.world.definition.v1' } } }), null);
  assert.equal(generatorCommit({ schemaBundle: null }), null);
  assert.equal(generatorCommit({ schemaBundle: { 'x-puck': { commit: 7 } } }), null);
});

test('verify.verifyBytes hashes with crypto.subtle and refuses a mismatch by name', async () => {
  const bytes = textBytes('hello official world');
  const hash = toHashName(await sha256Hex(bytes));
  await verifyBytes('some/path', bytes, hash);

  await assert.rejects(
    verifyBytes('some/path', bytes, 'sha256/0000000000000000000000000000000000000000000000000000000000000000'),
    (error) => {
      assert.ok(error instanceof OfficialRefusal);
      assert.equal(error.objectPath, 'some/path');
      assert.equal(error.actualHash, hash);
      return true;
    },
  );
});

test('loadOfficial happy path: schemaBundle, sources, documents, composed, assets, and engine files all verify', async () => {
  const fixture = await buildFixture();
  const store = createByteStore();
  const result = await loadOfficial(fixture.official, fixture.fakeFetch, store);

  assert.equal(result.source, 'network');
  assert.equal(result.build.commit, 'abc123');
  assert.deepEqual(result.schemaBundle, { 'x-puck': { schemaVersion: 'puck.world.definition.v1' } });

  assert.deepEqual(result.sources.names(), ['games/tictactoe.puck']);
  assert.equal(await result.sources.get('games/tictactoe.puck'), fixture.sourceText);

  assert.deepEqual(result.documents.names(), ['games/tictactoe']);
  assert.equal(await result.documents.get('games/tictactoe'), fixture.documentText);

  assert.deepEqual(result.composed.names(), ['puck']);
  assert.equal(await result.composed.get('puck'), fixture.composedText);

  assert.deepEqual(result.assets.names(), ['music/theme']);
  assert.equal(await result.assets.get('music/theme'), fixture.assetText);

  assert.equal(result.engineEntryUrl, fixture.official.objectUrl('objects/sha256/bb/mainmjs').href);
  assert.equal(result.engineFiles['main.mjs'].url, result.engineEntryUrl);
  assert.equal(result.engineFiles['_framework/dotnet.js'].url, fixture.official.objectUrl('objects/sha256/cc/dotnetjs').href);
});

test('a hash mismatch refuses by name and caches nothing in the byte store', async () => {
  const fixture = await buildFixture();
  // Corrupt the served bytes for the one document without touching its manifest hash.
  fixture.byUrl.set(fixture.official.objectUrl(fixture.documentPath).href, () => ({
    ok: true, status: 200, arrayBuffer: async () => textBytes('tampered bytes').buffer,
  }));

  const store = createByteStore();
  const result = await loadOfficial(fixture.official, fixture.fakeFetch, store);

  await assert.rejects(result.documents.get('games/tictactoe'), (error) => {
    assert.ok(error instanceof OfficialRefusal);
    assert.equal(error.objectPath, fixture.documentPath);
    assert.equal(error.expectedHash, fixture.documentHash);
    assert.notEqual(error.actualHash, fixture.documentHash);
    return true;
  });

  assert.equal(await store.get(fixture.documentHash), undefined);
});

test('a tampered stored copy is fetched again and replaced, never served', async () => {
  const fixture = await buildFixture();
  const store = createByteStore();
  await store.put(fixture.documentHash, textBytes('tampered in the store'), 'application/json');
  let documentFetches = 0;
  const countingFetch = (input) => {
    if ((typeof input === 'string' ? input : input.href) === fixture.official.objectUrl(fixture.documentPath).href) documentFetches += 1;
    return fixture.fakeFetch(input);
  };

  const result = await loadOfficial(fixture.official, countingFetch, store);
  assert.equal(await result.documents.get('games/tictactoe'), fixture.documentText);
  assert.equal(documentFetches, 1, 'the tampered copy sent the read to the network');
  assert.equal(new TextDecoder().decode(await store.get(fixture.documentHash)), fixture.documentText, 'the good bytes replace it');
});

test('a tampered stored copy with no network to replace it is refused by name', async () => {
  const fixture = await buildFixture();
  const store = createByteStore();
  const loaded = await loadOfficial(fixture.official, fixture.fakeFetch, store);
  await store.put(fixture.documentHash, textBytes('tampered in the store'), 'application/json');
  const objectsOffline = (input) => ((typeof input === 'string' ? input : input.href).includes('/objects/')
    ? Promise.reject(new Error('offline'))
    : fixture.fakeFetch(input));

  const offline = await loadOfficial(fixture.official, objectsOffline, store);
  assert.equal(loaded.manifest.documents.length, offline.manifest.documents.length);
  await assert.rejects(offline.documents.get('games/tictactoe'), (error) => {
    assert.ok(error instanceof OfficialRefusal);
    assert.equal(error.objectPath, fixture.documentPath);
    assert.equal(error.expectedHash, fixture.documentHash);
    assert.match(error.message, /failed verification/);
    assert.match(error.message, /offline/);
    return true;
  });
});

test('a manifest fetch failure falls back to a previously verified offline copy', async () => {
  const fixture = await buildFixture();
  const store = createByteStore();

  const first = await loadOfficial(fixture.official, fixture.fakeFetch, store);
  assert.equal(first.source, 'network');
  // Prove the schema bundle's own bytes really did get cached, not merely the manifest.
  assert.notEqual(await store.get(fixture.manifest.worldSchemaBundle.hash), undefined);

  const offlineFetch = () => Promise.reject(new Error('simulated network outage'));
  const second = await loadOfficial(fixture.official, offlineFetch, store);
  assert.equal(second.source, 'offline-cache');
  assert.deepEqual(second.manifest, first.manifest);
});

test('an offline manifest fetch with nothing cached throws an OfficialRefusal', async () => {
  const fixture = await buildFixture();
  const store = createByteStore();
  const offlineFetch = () => Promise.reject(new Error('simulated network outage'));

  await assert.rejects(loadOfficial(fixture.official, offlineFetch, store), OfficialRefusal);
});

test('object URLs resolve every manifest path against the official root exactly once', async () => {
  const fixture = await buildFixture();
  let assetFetches = 0;
  const countingFetch = (input) => {
    const url = typeof input === 'string' ? input : input.href;
    if (url.endsWith('/ff/asset')) assetFetches += 1;
    return fixture.fakeFetch(input);
  };

  const store = createByteStore();
  const result = await loadOfficial(fixture.official, countingFetch, store);
  await result.assets.get('music/theme');
  await result.assets.get('music/theme');

  assert.equal(assetFetches, 1, 'a second get() for the same name must not re-fetch');
  assert.equal(
    fixture.official.objectUrl(fixture.manifest.assets[0].path).href,
    'https://official.test/off/objects/sha256/ff/asset',
  );
});
