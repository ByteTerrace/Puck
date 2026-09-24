/**
 * The editor's side of the language server channel: `@codemirror/lsp-client`'s `Transport` over a
 * `LanguageServerChannel`. Every message the client sends goes to the channel as it is; every message the server
 * writes reaches each subscribed handler as JSON text, in order. Timing belongs to the pump behind the channel
 * (`native/languagePump.ts`) and to lsp-client's own sync, which waits for 500 ms of quiet and flushes before every
 * request, so the transport adds no delay of its own.
 */
import type { Subscription } from "rxjs";
import type { LanguageServerChannel } from "./engineTypes";

/** The shape `@codemirror/lsp-client` calls a `Transport`. */
export interface LspTransport {
  send(message: string): void;
  subscribe(handler: (value: string) => void): void;
  unsubscribe(handler: (value: string) => void): void;
}

export function channelTransport(channel: LanguageServerChannel): LspTransport {
  const handlers = new Map<(value: string) => void, Subscription>();
  return {
    send: (message) => channel.send(message),
    subscribe: (handler) => {
      if (handlers.has(handler)) return;
      handlers.set(handler, channel.messages().subscribe((message) => handler(JSON.stringify(message))));
    },
    unsubscribe: (handler) => {
      handlers.get(handler)?.unsubscribe();
      handlers.delete(handler);
    },
  };
}
