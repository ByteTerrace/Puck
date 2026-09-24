import type { TokenCredential } from "@azure/identity";
import { useEffect, useEffectEvent, useRef, useState } from "react";
import { Subject } from "rxjs";
import { runStorageQuery } from "./duckDb";
import {
  buildQuerySql,
  describeShareUri,
  PENDING_SHARE_STORAGE_KEY,
  PENDING_SQL_STORAGE_KEY,
  readFunctionFor,
} from "./storage";
import { initialStorageQueryState, shareAnnouncements, storageQueryRuns } from "./storageStreams";

const readSession = (key: string): string | undefined => {
  try {
    return sessionStorage.getItem(key) ?? undefined;
  } catch {
    return undefined;
  }
};

/**
 * The SQL editor's text and its latest run (`storageQueryRuns`). SQL another page left behind (the audit trail's
 * "Analyze with SQL") is picked up once, on mount.
 */
export function useStorageQuery(tokenCredential: TokenCredential) {
  const [runs] = useState(() => new Subject<string>());
  const [state, setState] = useState(initialStorageQueryState);
  const [sql, setSql] = useState("");

  useEffect(() => {
    const subscription = storageQueryRuns(runs, (sqlText, signal) => runStorageQuery(sqlText, tokenCredential, signal)).subscribe(setState);

    return () => subscription.unsubscribe();
  }, [runs, tokenCredential]);

  useEffect(() => {
    const pendingSql = readSession(PENDING_SQL_STORAGE_KEY);

    if (pendingSql) {
      try {
        sessionStorage.removeItem(PENDING_SQL_STORAGE_KEY);
      } catch {
        // Storage unavailable; the text still loads.
      }

      setSql(pendingSql);
    }
  }, []);

  /** Runs `sqlText`, abandoning any run still in flight. */
  const run = (sqlText: string) => runs.next(sqlText);

  return {
    ...state,
    /** Replaces the editor's text and runs it. */
    replaceAndRun: (sqlText: string) => {
      setSql(sqlText);
      run(sqlText);
    },
    run: run,
    setSql: setSql,
    sql: sql,
  };
}

/**
 * A file someone shared with this user through a portal link. The host stashes the link in session storage and
 * announces links clicked mid-session with the `byteterrace-share` window event. A queryable file greets the
 * recipient with its data, not a button: `queryShare` runs once per announced link.
 */
export function usePendingShare(queryShare: (sqlText: string) => void) {
  const [pendingShare, setPendingShare] = useState<string | undefined>(() => readSession(PENDING_SHARE_STORAGE_KEY));
  const autoRanShare = useRef(false);

  // Share links clicked while the app is already open only change the hash; the host captures and announces them.
  useEffect(() => {
    const subscription = shareAnnouncements(() => readSession(PENDING_SHARE_STORAGE_KEY)).subscribe((share) => {
      autoRanShare.current = false;
      setPendingShare(share);
    });

    return () => subscription.unsubscribe();
  }, []);

  const autoQuery = useEffectEvent((share: string) => {
    const described = describeShareUri(share);
    const readFunction = readFunctionFor(described.name);

    // The link came from a URL fragment anyone can write; only a ByteTerrace storage link ever runs on its own.
    if (described.isByteTerrace && readFunction) {
      autoRanShare.current = true;
      queryShare(buildQuerySql(readFunction, share));
    }
  });

  useEffect(() => {
    if (pendingShare && !autoRanShare.current) {
      autoQuery(pendingShare);
    }
  }, [pendingShare]);

  return {
    dismiss: () => {
      setPendingShare(undefined);

      try {
        sessionStorage.removeItem(PENDING_SHARE_STORAGE_KEY);
      } catch {
        // Nothing to clean up if storage is unavailable.
      }
    },
    pendingShare: pendingShare,
  };
}
