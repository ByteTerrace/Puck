/**
 * The JSON pointer of the value at a position in JSON text, read from lang-json's syntax tree: each enclosing
 * property contributes its key and each enclosing array the index of the element that holds the position.
 */
import { ensureSyntaxTree } from "@codemirror/language";
import type { EditorState } from "@codemirror/state";
import { pathToPointer, type JsonPath } from "../../../document/jsonPath";

type SyntaxNode = ReturnType<NonNullable<ReturnType<typeof ensureSyntaxTree>>["resolveInner"]>;

const VALUES = new Set(["Object", "Array", "String", "Number", "True", "False", "Null"]);

/** The pointer, relative to the text's own root, of the innermost value at `position`. A position on a property's
 * key names that property's value. */
export function jsonPointerAt(state: EditorState, position: number): string {
  const tree = ensureSyntaxTree(state, state.doc.length, 1000);
  if (!tree) return "";
  const path: (string | number)[] = [];
  let node: SyntaxNode | null = tree.resolveInner(position, 1);
  while (node && !VALUES.has(node.name) && node.name !== "Property") node = node.parent;
  if (node?.name === "Property") {
    let value: SyntaxNode | null = node.lastChild;
    while (value && !VALUES.has(value.name)) value = value.prevSibling;
    node = value;
  }
  for (let child: SyntaxNode | null = node; child && child.parent; child = child.parent) {
    const parent: SyntaxNode = child.parent;
    if (parent.name === "Property" && VALUES.has(child.name)) {
      const key = parent.getChild("PropertyName");
      if (key) path.unshift(JSON.parse(state.doc.sliceString(key.from, key.to)) as string);
    } else if (parent.name === "Array" && VALUES.has(child.name)) {
      let index = 0;
      for (let sibling = child.prevSibling; sibling; sibling = sibling.prevSibling) {
        if (VALUES.has(sibling.name)) index += 1;
      }
      path.unshift(index);
    }
  }
  return pathToPointer(path as JsonPath);
}
