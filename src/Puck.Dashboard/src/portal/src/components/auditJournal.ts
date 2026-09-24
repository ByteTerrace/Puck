import { catchError, defer, from, map, mergeMap, type Observable, of, reduce, startWith, switchMap } from "rxjs";

// The audit trail's journal: the storage account's change events, which the platform writes as batches of
// CloudEvents under the user's container. `auditJournal` reads the newest batches as one stream.

export const JOURNAL_PREFIX = "system/events/blob/";
/** How many of the newest journal batches the trail reads. */
export const JOURNAL_FETCH_LIMIT = 60;
/** How many journal batches download at once: enough to overlap round trips without flooding the connection. */
export const JOURNAL_FETCH_CONCURRENCY = 6;

export type AuditKind = "created" | "deleted" | "other" | "renamed";

export interface AuditEvent {
  blobPath: string;
  journalUrl: string;
  kind: AuditKind;
  /** The storage operation that raised the event (`PutBlob`, `DeleteBlob`), when the journal records one. */
  operation: string | undefined;
  time: Date;
}

export const kindOf = (eventType: string): AuditKind => {
  const type = eventType.toLowerCase();

  if (type.includes("created")) {
    return "created";
  }

  if (type.includes("deleted")) {
    return "deleted";
  }

  if (type.includes("renamed")) {
    return "renamed";
  }

  return "other";
};
// Journal payload shapes are parsed defensively: the writer serializes CloudEvent batches whose casing depends on
// serializer configuration.
export const parseJournal = (raw: unknown, journalUrl: string): AuditEvent[] => {
  const record = raw as Record<string, unknown>;
  const items =
    (Array.isArray(raw) ? raw : ((record?.value ?? record?.Value ?? record?.items ?? record?.Items) as unknown[])) ?? [];

  return items.flatMap((item) => {
    const event = item as Record<string, any>;
    const type = String(event?.type ?? event?.Type ?? "");
    const time = event?.time ?? event?.Time;
    const data = event?.data ?? event?.Data;
    const url = String(data?.url ?? data?.Url ?? "");
    const operation = data?.api ?? data?.Api;
    let blobPath = "";

    try {
      blobPath = decodeURIComponent(new URL(url).pathname.split("/").slice(2).join("/"));
    } catch {
      blobPath = url;
    }

    if (!type || !time) {
      return [];
    }

    return [
      {
        blobPath: blobPath,
        journalUrl: journalUrl,
        kind: kindOf(type),
        operation: operation ? String(operation) : undefined,
        time: new Date(time),
      },
    ];
  });
};
export interface AuditJournalSources {
  /** The auth headers every journal download carries. */
  headers(): Promise<Record<string, string>>;
  /** Every journal batch's blob name under the account. */
  listJournalNames(endpoint: string): Promise<string[]>;
  resolveEndpoint(): Promise<string>;
  userObjectId: string;
}

export interface AuditJournalState {
  error: string | undefined;
  /** Newest first; `undefined` until the first load completes. */
  events: AuditEvent[] | undefined;
  /** The batches read, newest first, for handing to the SQL editor. */
  journalUrls: string[];
}

export const initialAuditJournalState: AuditJournalState = { error: undefined, events: undefined, journalUrls: [] };

/**
 * The newest journal batches, downloaded a few at a time and merged into one newest-first timeline. A batch that
 * fails to download or parse drops out rather than failing the trail; failing to reach the account at all is the
 * trail's error.
 */
export function auditJournal(
  sources: AuditJournalSources,
  download: (url: string, headers: Record<string, string>) => Promise<unknown>,
): Observable<AuditJournalState> {
  return defer(() => sources.resolveEndpoint()).pipe(
    switchMap((endpoint) =>
      defer(() => sources.listJournalNames(endpoint)).pipe(
        // Journal names begin with a UTC timestamp, so name order is time order.
        map((names) => [...names].sort().reverse().slice(0, JOURNAL_FETCH_LIMIT).map((name) => `${endpoint}/${sources.userObjectId}/${name}`)),
        switchMap((journalUrls) =>
          defer(() => sources.headers()).pipe(
            switchMap((headers) =>
              from(journalUrls).pipe(
                mergeMap(
                  (journalUrl) =>
                    defer(() => download(journalUrl, headers)).pipe(
                      map((raw) => parseJournal(raw, journalUrl)),
                      catchError(() => of<AuditEvent[]>([])),
                    ),
                  JOURNAL_FETCH_CONCURRENCY,
                ),
                reduce((all, events) => all.concat(events), [] as AuditEvent[]),
              ),
            ),
            map((events): AuditJournalState => ({
              error: undefined,
              events: events.sort((a, b) => b.time.getTime() - a.time.getTime()),
              journalUrls: journalUrls,
            })),
          ),
        ),
      ),
    ),
    catchError((error: unknown) =>
      of<AuditJournalState>({ ...initialAuditJournalState, error: error instanceof Error ? error.message : String(error) }),
    ),
    startWith(initialAuditJournalState),
  );
}
