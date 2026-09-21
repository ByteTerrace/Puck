const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const ts = require('typescript');
require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, file);
const { wrapRawExports } = require('../src/native/inlineHost.ts');

test('cost facade keeps large cycle counts exact and unresolved counts absent', async () => {
  const missing = { kind: 'Unmodeled', cycles: null, reason: 'missing evidence' };
  const engine = wrapRawExports({ Costs: () => JSON.stringify({ ok: true, report: {
    modelId: 'test', evidenceDigest: 'digest', scope: 'authored simulation', simulationRateHz: 30,
    stepAllowanceCycles: '9223372036854775807', recurringBound: missing, searchReservations: { kind: 'Known', cycles: '0', reason: null },
    totalBound: missing, editBurstBound: { kind: 'Overflow', cycles: null, reason: null }, admitted: false,
    heuristicWorkUnitsPerTick: '42', issues: ['missing evidence'],
    contributors: [{ name: 'rule', isInteraction: false, multiplier: '9007199254740993', setup: '0', check: '1', fire: '2', work: '3' }],
    contributorSources: [{ name: 'rule', isInteraction: false, jsonPointer: '/rules/0', sourcePath: null, line: null, column: null, moduleInstancePath: null }],
    resources: {
      rowCount: 1, topologyCount: 0, populationCapacity: 1, cellSlotCount: 1,
      vectorComponentBytes: '4', laneSlotCount: 0, laneRosterCount: 0, drawMaskWordCount: 0,
      layoutBytes: '64', retainedVisibilityBytes: '16', retainedKeyBytes: '8', arenaFootprintBytes: '88',
      arenaAdmissionCeilingBytes: '1048576', journalAllowanceBytes: '4096', measurementIssue: null,
      unmodeledTotalMemoryReason: 'scratch and service allocations are outside this report',
    },
  } }) });
  const report = await engine.costs('h');
  assert.equal(report.stepAllowanceCycles, 9223372036854775807n);
  assert.equal(report.searchReservations.cycles, 0n);
  assert.equal(report.totalBound.cycles, null);
  assert.equal(report.totalBound.reason, 'missing evidence');
  assert.equal(report.contributors[0].multiplier, 9007199254740993n);
  assert.equal(report.resources.arenaFootprintBytes, 88n);
  assert.equal(report.resources.journalAllowanceBytes, 4096n);
  assert.equal(report.contributorSources[0].jsonPointer, '/rules/0');
  assert.equal(report.heuristicWorkUnitsPerTick, '42');
});

test('cost facade propagates a released-handle error', async () => {
  const engine = wrapRawExports({ Costs: () => JSON.stringify({ ok: false, error: 'no such handle' }) });
  await assert.rejects(engine.costs('gone'), /no such handle/);
});
