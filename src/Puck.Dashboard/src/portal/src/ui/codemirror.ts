import { HighlightStyle, syntaxHighlighting } from "@codemirror/language";
import { EditorView } from "@codemirror/view";
import { tags } from "@lezer/highlight";

// Every value is a theme CSS variable, so the editor follows the color scheme with no rebuild when it flips.

const editorTheme = EditorView.theme({
  "&": {
    backgroundColor: "var(--puck-surface-sunken)",
    border: "1px solid var(--puck-line)",
    borderRadius: "var(--mantine-radius-md)",
    color: "var(--mantine-color-text)",
    fontSize: "var(--mantine-font-size-sm)",
  },
  "&.cm-focused": {
    borderColor: "var(--accent-2)",
    outline: "none",
  },
  // The vertical padding sits on the scroller, around the gutter and the content alike. On the content alone, the
  // gutter matches it only after the editor's first measurement, and the line numbers drop a frame after appearing.
  ".cm-scroller": {
    paddingBlock: "var(--mantine-spacing-xs)",
  },
  ".cm-content": {
    caretColor: "var(--mantine-color-anchor)",
    fontFamily: "var(--font-code)",
    padding: 0,
  },
  ".cm-cursor, .cm-dropCursor": {
    borderLeftColor: "var(--mantine-color-anchor)",
  },
  ".cm-gutters": {
    backgroundColor: "transparent",
    border: "none",
    color: "var(--mantine-color-placeholder)",
    fontFamily: "var(--font-code)",
  },
  ".cm-activeLine, .cm-activeLineGutter": {
    backgroundColor: "color-mix(in oklab, var(--mantine-color-text) 5%, transparent)",
  },
  "&.cm-focused .cm-selectionBackground, .cm-selectionBackground, ::selection": {
    backgroundColor: "color-mix(in oklab, var(--mantine-color-coral-4) 28%, transparent)",
  },
  ".cm-tooltip": {
    backgroundColor: "var(--puck-surface-raised)",
    border: "1px solid var(--puck-line)",
    borderRadius: "var(--mantine-radius-sm)",
  },
  ".cm-tooltip-autocomplete > ul > li[aria-selected]": {
    backgroundColor: "color-mix(in oklab, var(--mantine-color-coral-4) 22%, transparent)",
    color: "var(--mantine-color-text)",
  },
});

// Keys and keywords take the coral lead, strings the jade complement, literals the ground's soft ink.
const highlightStyle = HighlightStyle.define([
  { color: "var(--mantine-color-anchor)", tag: [tags.propertyName, tags.keyword, tags.operatorKeyword] },
  { color: "var(--accent-2)", tag: [tags.string, tags.special(tags.string)] },
  { color: "var(--mantine-color-text)", fontWeight: "500", tag: [tags.number, tags.bool, tags.null] },
  { color: "var(--mantine-color-dimmed)", fontStyle: "italic", tag: [tags.comment, tags.lineComment, tags.blockComment] },
  { color: "var(--mantine-color-dimmed)", tag: [tags.punctuation, tags.bracket, tags.separator] },
  { color: "var(--mantine-color-text)", tag: [tags.function(tags.variableName), tags.typeName] },
]);

// The `.puck` editor's colors come from the language server's semantic tokens (`cm-puck-<type>`), in the same roles
// as the syntax colors above: keywords take the coral lead, strings the jade complement, literals the soft ink.
const semanticTokenTheme = EditorView.theme({
  ".cm-puck-keyword, .cm-puck-modifier, .cm-puck-operator, .cm-puck-property": { color: "var(--mantine-color-anchor)" },
  ".cm-puck-string, .cm-puck-regexp": { color: "var(--accent-2)" },
  ".cm-puck-number, .cm-puck-enumMember": { color: "var(--mantine-color-text)", fontWeight: "500" },
  ".cm-puck-comment": { color: "var(--mantine-color-dimmed)", fontStyle: "italic" },
  ".cm-puck-function, .cm-puck-method, .cm-puck-macro, .cm-puck-type, .cm-puck-class, .cm-puck-namespace": {
    color: "color-mix(in oklab, var(--mantine-color-anchor) 55%, var(--mantine-color-text))",
  },
  ".cm-puck-parameter, .cm-puck-variable": { color: "var(--mantine-color-text)" },
});

/** The brand look for a CodeMirror editor: surfaces, gutters, selection, syntax colors, and `.puck` token colors. */
export const puckEditorTheme = [editorTheme, syntaxHighlighting(highlightStyle), semanticTokenTheme];
