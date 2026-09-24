import {
  catchError,
  concat,
  defer,
  EMPTY,
  finalize,
  fromEvent,
  map,
  merge,
  Observable,
  of,
  scan,
  shareReplay,
  startWith,
  switchMap,
} from "rxjs";
import type { QueryOutput } from "./queryResult";
import { type BlobEntry, errorMessage, type PublicFileEntry } from "./storage";

// The Cloud Storage page's asynchronous flows as streams. Each takes its sources as arguments, so the timing rules
// (a newer load supersedes an older one; every reload settles) are testable without Azure or a browser.

export interface StorageAccountState {
  blobs: BlobEntry[] | undefined;
  listError: string | undefined;
  publicFiles: PublicFileEntry[];
  storageEndpoint: string | undefined;
}

export const initialStorageAccountState: StorageAccountState = {
  blobs: undefined,
  listError: undefined,
  publicFiles: [],
  storageEndpoint: undefined,
};

/** A request to refresh the listings. `settled` runs once that refresh ends, or once a newer one supersedes it. */
export interface StorageReload {
  includePublic: boolean;
  settled: () => void;
}

export interface StorageAccountSources {
  listPrivate(endpoint: string): Promise<BlobEntry[]>;
  listPublic(): Promise<PublicFileEntry[]>;
  resolveEndpoint(): Promise<string>;
}

/**
 * The signed-in account's storage: its endpoint, then its private and published listings, loaded once the endpoint
 * resolves and again on each reload. A reload supersedes one still in flight, so an older listing never lands
 * over a newer one. The endpoint resolves once and is kept; a failed resolution is not, so the next reload asks
 * again rather than leaving the page stuck on the error.
 */
export function storageAccountState(
  sources: StorageAccountSources,
  reloads$: Observable<StorageReload>,
): Observable<StorageAccountState> {
  const listing = <T>(load: () => Promise<T>, patch: (value: T) => Partial<StorageAccountState>) =>
    defer(load).pipe(
      map(patch),
      catchError((error: unknown) => of<Partial<StorageAccountState>>({ listError: errorMessage(error) })),
    );

  // `shareReplay` resets on error, so only a resolved endpoint is remembered.
  const endpoint$ = defer(() => sources.resolveEndpoint()).pipe(shareReplay({ bufferSize: 1, refCount: false }));
  const patches$ = reloads$.pipe(
    startWith<StorageReload>({ includePublic: true, settled: () => {} }),
    switchMap((reload) =>
      endpoint$.pipe(
        switchMap((endpoint) =>
          merge(
            of<Partial<StorageAccountState>>({ storageEndpoint: endpoint }),
            listing(() => sources.listPrivate(endpoint), (blobs) => ({ blobs, listError: undefined })),
            reload.includePublic ? listing(() => sources.listPublic(), (publicFiles) => ({ publicFiles })) : EMPTY,
          ),
        ),
        catchError((error: unknown) => of<Partial<StorageAccountState>>({ listError: errorMessage(error) })),
        startWith<Partial<StorageAccountState>>({ listError: undefined }),
        finalize(reload.settled),
      ),
    ),
  );

  return patches$.pipe(
    scan((state, patch) => ({ ...state, ...patch }), initialStorageAccountState),
    startWith(initialStorageAccountState),
  );
}

export interface StorageQueryState {
  isRunning: boolean;
  output: QueryOutput | undefined;
  queryError: string | undefined;
}

export const initialStorageQueryState: StorageQueryState = { isRunning: false, output: undefined, queryError: undefined };

/** A cancellable call as a stream: its one answer, and unsubscribing (a superseding `switchMap`) aborts the call. */
function abortable<T>(call: (signal: AbortSignal) => Promise<T>): Observable<T> {
  return new Observable<T>((subscriber) => {
    const controller = new AbortController();

    call(controller.signal).then(
      (value) => {
        subscriber.next(value);
        subscriber.complete();
      },
      (error: unknown) => subscriber.error(error),
    );

    return () => controller.abort();
  });
}

/**
 * Each run of the SQL editor, latest wins: starting a run abandons the one before it, so a slow earlier query can
 * never replace a newer result, and the abandoned query is cancelled in the engine rather than left running.
 */
export function storageQueryRuns(
  runs$: Observable<string>,
  execute: (sqlText: string, signal: AbortSignal) => Promise<QueryOutput>,
): Observable<StorageQueryState> {
  return runs$.pipe(
    switchMap((sqlText) =>
      concat(
        of<StorageQueryState>({ isRunning: true, output: undefined, queryError: undefined }),
        abortable((signal) => execute(sqlText, signal)).pipe(
          map((output): StorageQueryState => ({ isRunning: false, output, queryError: undefined })),
          catchError((error: unknown) =>
            of<StorageQueryState>({ isRunning: false, output: undefined, queryError: errorMessage(error) }),
          ),
        ),
      ),
    ),
    startWith(initialStorageQueryState),
  );
}

/** Share links clicked while the app is open: the host captures each and announces it with `byteterrace-share`. */
export function shareAnnouncements(readPendingShare: () => string | undefined): Observable<string | undefined> {
  return fromEvent(window, "byteterrace-share").pipe(map(readPendingShare));
}
