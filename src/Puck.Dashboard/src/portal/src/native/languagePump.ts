/**
 * The language server pump: how client messages and the server's pending diagnostic work share one engine thread.
 *
 * The engine's language server never diagnoses while it takes a message; it queues the work and runs one unit per
 * `lspIdle` call. The pump therefore keeps two kinds of work in one order. A client message always goes first:
 * the queue is drained before any idle unit runs. Idle units run only while the queue is empty, one per scheduled
 * step, so each yields to the event loop and a message that arrives meanwhile is taken before the next unit. A unit
 * already running finishes, because the engine's thread cannot be interrupted. The pump idles while the server says
 * work is pending, stops when it says none is, and starts again after the next message.
 *
 * Consecutive queued full-text `didChange` notifications for one URI coalesce to the newest, so a burst of edits
 * costs one server call and one diagnostic unit. The server writes an opened or changed document's text through into
 * its mounted workspace itself, so every other file's resolution sees the edit without a separate write.
 *
 * The module is plain and both hosts use it the same way: the pump runs in the page's thread over the engine's own
 * `lsp`/`lspIdle`, which for a worker engine are ordinary calls. A unit costs one message each way, and while a unit
 * runs in the worker the page stays free to queue what the editor sends next. Tests drive it with a virtual-time
 * scheduler.
 */
import { asyncScheduler, Observable, Subject, type SchedulerLike, type Subscription } from "rxjs";
import type { EngineCore, LanguageServerChannel, LspIdleResult, LspMessage, WorldEngine } from "./engineTypes";

/** The engine calls the pump drives; each may answer at once or through a promise. */
export interface LanguageServerCore {
  lsp(message: string): LspMessage[] | Promise<LspMessage[]>;
  lspIdle(): LspIdleResult | Promise<LspIdleResult>;
}

interface ClientMessage {
  readonly method?: string;
  readonly params?: {
    readonly textDocument?: { readonly uri?: string };
    readonly contentChanges?: readonly { readonly text: string; readonly range?: unknown }[];
  };
}

interface Queued {
  readonly text: string;
  readonly message: ClientMessage;
}

/** Whether a notification replaces its document's whole text, so an older one queued before it can be dropped. */
function isFullChange(message: ClientMessage): boolean {
  if (message.method !== "textDocument/didChange") return false;
  const changes = message.params?.contentChanges ?? [];
  const last = changes[changes.length - 1];
  return !!last && !("range" in last && last.range !== undefined);
}

function settle<T>(value: T | Promise<T>, next: (value: T) => void, fail: (error: unknown) => void): void {
  if (value instanceof Promise) {
    value.then(next, fail);
    return;
  }
  next(value);
}

/**
 * Runs `core`'s language server over `inbound$`, one client message per string, and emits every message the server
 * writes. Completes when `inbound$` completes and the queue has drained; errors when an engine call fails.
 */
export function pumpLanguageServer(
  inbound$: Observable<string>,
  core: LanguageServerCore,
  scheduler: SchedulerLike = asyncScheduler,
): Observable<LspMessage> {
  return new Observable<LspMessage>((subscriber) => {
    const queue: Queued[] = [];
    let running = false;
    let idleWork = false;
    let inboundDone = false;
    let scheduled: Subscription | null = null;

    const finishIfDone = () => {
      if (inboundDone && queue.length === 0 && !running) subscriber.complete();
    };
    const kick = () => {
      if (running || scheduled || subscriber.closed) return;
      if (queue.length === 0 && !idleWork) {
        finishIfDone();
        return;
      }
      scheduled = scheduler.schedule(step);
    };
    const emit = (messages: readonly LspMessage[]) => {
      for (const message of messages) subscriber.next(message);
    };
    const fail = (error: unknown) => subscriber.error(error);
    const done = () => {
      running = false;
      kick();
    };

    const take = (item: Queued) => settle(core.lsp(item.text), (messages) => {
      emit(messages);
      done();
    }, fail);

    const idle = () => settle(core.lspIdle(), (result) => {
      if (!result.pending) idleWork = false;
      emit(result.messages);
      done();
    }, fail);

    function step() {
      scheduled = null;
      if (subscriber.closed) return;
      running = true;
      const next = queue.shift();
      if (next) {
        take(next);
      } else {
        idle();
      }
    }

    const inbound = inbound$.subscribe({
      next: (text) => {
        const message = JSON.parse(text) as ClientMessage;
        const tail = queue[queue.length - 1];
        const uri = message.params?.textDocument?.uri;
        if (tail && isFullChange(message) && isFullChange(tail.message) && tail.message.params?.textDocument?.uri === uri) {
          queue[queue.length - 1] = { text, message };
        } else {
          queue.push({ text, message });
        }
        idleWork = true;
        kick();
      },
      error: fail,
      complete: () => {
        inboundDone = true;
        idleWork = false;
        kick();
      },
    });

    return () => {
      inbound.unsubscribe();
      scheduled?.unsubscribe();
    };
  });
}

/** A decoded engine with its language server: `languageServer()` runs this pump over the engine's own calls. */
export function withLanguageServer(core: EngineCore): WorldEngine {
  return { ...core, languageServer: languageServerOf(core) };
}

/** An engine's `languageServer()`: opens the one channel over `core`, and refuses a second while it is open. */
export function languageServerOf(core: LanguageServerCore): () => LanguageServerChannel {
  let open = false;
  return () => {
    if (open) throw new Error("this engine's language server channel is already open.");
    open = true;
    const channel = openLanguageChannel(core);
    return {
      ...channel,
      close: () => {
        channel.close();
        open = false;
      },
    };
  };
}

/** Opens a channel over `core`: the pump runs from now until `close`, and its messages are shared by every observer. */
export function openLanguageChannel(core: LanguageServerCore, scheduler: SchedulerLike = asyncScheduler): LanguageServerChannel {
  const inbound = new Subject<string>();
  const outbound = new Subject<LspMessage>();
  const subscription = pumpLanguageServer(inbound, core, scheduler).subscribe(outbound);
  const shared = outbound.asObservable();

  return {
    send: (message) => inbound.next(message),
    messages: () => shared,
    close: () => {
      subscription.unsubscribe();
      inbound.complete();
      outbound.complete();
    },
  };
}
