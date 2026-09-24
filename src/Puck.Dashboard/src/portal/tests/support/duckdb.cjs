// The portal's own DuckDB-Wasm build, run in Node: the same engine and Arrow the browser uses, through the blocking
// bindings, so tests read real query results rather than a stand-in.
const path = require('node:path');

const dist = path.dirname(require.resolve('@duckdb/duckdb-wasm/dist/duckdb-node-blocking.cjs'));
const duckdb = require(path.join(dist, 'duckdb-node-blocking.cjs'));

/** A fresh in-memory database; `connect()` it for queries and `registerFileText` to give it files. */
async function openDuckDb() {
  const bundles = {
    eh: { mainModule: path.join(dist, 'duckdb-eh.wasm'), mainWorker: '' },
    mvp: { mainModule: path.join(dist, 'duckdb-mvp.wasm'), mainWorker: '' },
  };
  const db = await duckdb.createDuckDB(bundles, new duckdb.VoidLogger(), duckdb.NODE_RUNTIME);

  await db.instantiate();

  return db;
}

/** A query's result stream, shaped like the browser's `AsyncDuckDBConnection.send` answer. */
function streamOf(connection, sql) {
  let reader;

  return {
    get schema() {
      return reader?.schema;
    },
    async *[Symbol.asyncIterator]() {
      reader = await connection.send(sql, true);
      yield* reader;
    },
  };
}

module.exports = { openDuckDb, streamOf };
