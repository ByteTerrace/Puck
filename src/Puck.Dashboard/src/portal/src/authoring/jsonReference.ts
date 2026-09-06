/** Locate an applied document address without depending on whitespace or property order. */
export function findJsonRange(text: string, path: (string | number)[]): [number, number] | null {
  try { JSON.parse(text); } catch { return null; }
  const tokens = [...text.matchAll(/"(?:\\.|[^"\\])*"|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null|[{}\[\]:,]/g)];
  let cursor = 0;
  let found: [number, number] | null = null;
  function visit(location: (string | number)[]) {
    if (location.length > 128) throw new Error("JSON reveal depth exceeded.");
    const start = cursor, token = tokens[cursor++][0];
    if (token === "{") {
      while (tokens[cursor][0] !== "}") {
        const key = JSON.parse(tokens[cursor++][0]); cursor++;
        visit([...location, key]);
        if (tokens[cursor][0] === ",") cursor++;
      }
      cursor++;
    } else if (token === "[") {
      let index = 0;
      while (tokens[cursor][0] !== "]") {
        visit([...location, index++]);
        if (tokens[cursor][0] === ",") cursor++;
      }
      cursor++;
    }
    if (location.length === path.length && location.every((part, i) => part === path[i])) {
      const last = tokens[cursor - 1]; found = [tokens[start].index!, last.index! + last[0].length];
    }
  }
  try { visit([]); return found; } catch { return null; }
}
