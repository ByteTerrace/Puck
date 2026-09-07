const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const ts = require('typescript');

// Exercise the shipped TypeScript through Node's test runner, without a second bundler — same
// pattern as tests/offline-preview.test.cjs.
require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, file);

const { resolveOfficial, OfficialConfigError } = require('../src/official/officialBase.ts');
const { parseManifest, ManifestRefusal } = require('../src/official/manifest.ts');
const { sha256Hex, toHashName, verifyBytes, OfficialRefusal } = require('../src/official/verify.ts');
const { createByteStore } = require('../src/official/byteStore.ts');
const { loadOfficial } = require('../src/official/officialClient.ts');

const OFFICIAL_ENV = { VITE_PUCK_OFFICIAL_BASE: 'https://official.test/off', VITE_PUCK_OFFICIAL_CHANNEL: 'dev' };

function textBytes(text) {
  return new TextEncoder().encode(text);
}

async function buildFixture() {
  const official = resolveOfficial(OFFICIAL_ENV);

  const schemaBundleText = JSON.stringify({ 'x-puck': { schemaVersion: 'puck.world.def.v1' } });
  const schemaBundleBytes = textBytes(schemaBundleText);
  const schemaBundleHash = toHashName(await sha256Hex(schemaBundleBytes));

  const documentText = JSON.stringify({ schema: 'puck.world.def.v1', documentId: 'puck' });
  const documentBytes = textBytes(documentText);
  const documentHash = toHashName(await sha256Hex(documentBytes));

  const composedText = JSON.stringify({ schema: 'puck.world.def.v1', composed: true });
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
    schema: 'puck.official.v1',
    channel: 'dev',
    build: { commit: 'abc123', dirty: false, generator: 'Puck.World.WorldSchema', worldSchema: 'puck.world.def.v1' },
    worldSchemaBundle: { path: 'objects/sha256/aa/schemabundle', hash: schemaBundleHash, size: schemaBundleBytes.length, contentType: 'application/schema+json' },
    engine: {
      entry: 'main.mjs',
      files: [
        { name: 'main.mjs', path: 'objects/sha256/bb/mainmjs', hash: mainMjsHash, size: mainMjsBytes.length, contentType: 'text/javascript' },
        { name: '_framework/dotnet.js', path: 'objects/sha256/cc/dotnetjs', hash: dotnetJsHash, size: dotnetJsBytes.length, contentType: 'text/javascript' },
      ],
    },
    documents: [
      { name: 'games/tictactoe.world.json', role: 'fragment', documentId: null, imports: [], exports: ['tictactoe'], path: 'objects/sha256/dd/document', hash: documentHash, size: documentBytes.length, contentType: 'application/json', pin: 'sha256-64/0011223344556677' },
    ],
    composed: [
      { documentId: 'puck', name: 'puck.world.json', path: 'objects/sha256/ee/composed', hash: composedHash, size: composedBytes.length, contentType: 'application/json', pin: 'sha256-64/8899aabbccddeeff', identity: null },
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
    documentText, documentHash, composedText, assetText,
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

test('manifest.parseManifest refuses an unknown schema and a foreign build.worldSchema by name', () => {
  assert.throws(() => parseManifest('not json'), ManifestRefusal);
  assert.throws(
    () => parseManifest(JSON.stringify({ schema: 'not.a.real.schema', build: { worldSchema: 'puck.world.def.v1' } })),
    /not\.a\.real\.schema/,
  );
  assert.throws(
    () => parseManifest(JSON.stringify({ schema: 'puck.official.v1', build: { worldSchema: 'not.a.real.schema' } })),
    /not\.a\.real\.schema/,
  );
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

test('loadOfficial happy path: schemaBundle, documents, composed, assets, and engine files all verify', async () => {
  const fixture = await buildFixture();
  const store = createByteStore();
  const result = await loadOfficial(fixture.official, fixture.fakeFetch, store);

  assert.equal(result.source, 'network');
  assert.equal(result.build.commit, 'abc123');
  assert.deepEqual(result.schemaBundle, { 'x-puck': { schemaVersion: 'puck.world.def.v1' } });

  assert.deepEqual(result.documents.names(), ['games/tictactoe.world.json']);
  assert.equal(await result.documents.get('games/tictactoe.world.json'), fixture.documentText);

  assert.deepEqual(result.composed.names(), ['puck.world.json']);
  assert.equal(await result.composed.get('puck.world.json'), fixture.composedText);

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

  await assert.rejects(result.documents.get('games/tictactoe.world.json'), (error) => {
    assert.ok(error instanceof OfficialRefusal);
    assert.equal(error.objectPath, fixture.documentPath);
    assert.equal(error.expectedHash, fixture.documentHash);
    assert.notEqual(error.actualHash, fixture.documentHash);
    return true;
  });

  assert.equal(await store.get(fixture.documentHash), undefined);
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
