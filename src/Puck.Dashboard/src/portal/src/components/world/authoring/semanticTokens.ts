/**
 * `.puck` highlighting from the engine's own lexer. After each sync the editor asks the language server for
 * `textDocument/semanticTokens/full` and marks each token with a `cm-puck-<type>` class, the type named by the legend
 * the server declared when it initialized. `ui/codemirror.ts` styles those classes. The studio has no grammar or
 * tokenizer of its own for `.puck`: a token the server does not report stays plain text.
 */
import { LSPPlugin, type LSPClient } from "@codemirror/lsp-client";
import { type Extension, RangeSetBuilder, StateEffect, StateField, type Text } from "@codemirror/state";
import { Decoration, type DecorationSet, EditorView, ViewPlugin } from "@codemirror/view";
import type { StudioWorkspace } from "./languageClient";

const setTokens = StateEffect.define<DecorationSet>();

const tokenField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(tokens, transaction) {
    let next = tokens.map(transaction.changes);
    for (const effect of transaction.effects) {
      if (effect.is(setTokens)) next = effect.value;
    }
    return next;
  },
  provide: (field) => EditorView.decorations.from(field),
});

const marks = new Map<string, Decoration>();
function markFor(type: string): Decoration {
  let mark = marks.get(type);
  if (!mark) {
    mark = Decoration.mark({ class: `cm-puck-${type}` });
    marks.set(type, mark);
  }
  return mark;
}

/** Decodes the relative five-number encoding of `data` against `doc`, mapping each token through `map`. */
export function decodeSemanticTokens(
  data: readonly number[],
  legend: readonly string[],
  doc: Text,
  map: (position: number) => number,
): DecorationSet {
  const builder = new RangeSetBuilder<Decoration>();
  let line = 0;
  let character = 0;
  for (let index = 0; index + 4 < data.length; index += 5) {
    const [deltaLine, deltaStart, length, type] = [data[index], data[index + 1], data[index + 2], data[index + 3]];
    line += deltaLine;
    character = (deltaLine === 0) ? character + deltaStart : deltaStart;
    const name = legend[type];
    if (!name || line + 1 > doc.lines) continue;
    const start = doc.line(line + 1).from + character;
    const from = map(start);
    const to = map(start + length);
    if (to > from) builder.add(from, to, markFor(name));
  }
  return builder.finish();
}

async function refresh(view: EditorView, client: LSPClient): Promise<void> {
  const plugin = LSPPlugin.get(view);
  if (!plugin) return;
  const legend = (client.serverCapabilities?.semanticTokensProvider as { legend?: { tokenTypes: string[] } } | undefined)?.legend?.tokenTypes;
  if (!legend) return;
  const doc = plugin.syncedDoc;
  const uri = plugin.uri;
  await client.withMapping(async (mapping) => {
    const result = await client.request<{ textDocument: { uri: string } }, { data: number[] } | null>(
      "textDocument/semanticTokens/full", { textDocument: { uri } });
    const current = LSPPlugin.get(view);
    if (!result || !current || current.uri !== uri) return;
    const tokens = decodeSemanticTokens(result.data, legend, doc, (position) =>
      current.unsyncedChanges.mapPos(mapping.mapPos(uri, position)));
    view.dispatch({ effects: setTokens.of(tokens) });
  });
}

/** Keeps the editor's `.puck` tokens current after each sync of its file. */
export function semanticTokens(client: LSPClient, workspace: StudioWorkspace): Extension {
  return [
    tokenField,
    ViewPlugin.define((view) => {
      const subscription = workspace.synced$.subscribe((uri) => {
        if (LSPPlugin.get(view)?.uri === uri) void refresh(view, client).catch(() => undefined);
      });
      return { destroy: () => subscription.unsubscribe() };
    }),
  ];
}
