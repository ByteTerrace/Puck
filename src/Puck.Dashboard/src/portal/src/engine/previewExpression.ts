/** Bounded expression grammar for the offline preview. Never executes JavaScript. */
type Node = {
  op: string;
  args: Node[];
  value?: string;
};
const precedence: Record<string, number> = {
  "||": 1, "&&": 2, "|": 3, "^": 4, "&": 5,
  "==": 6, "!=": 6, "<": 7, "<=": 7, ">": 7, ">=": 7,
  "<<": 8, ">>": 8, "+": 9, "-": 9, "*": 10, "/": 10, "%": 10,
};
const cache = new Map<string, Node>();
function parse(source: string): Node {
  if(source.length > 8192)
    throw new Error("Preview expressions are limited to 8,192 characters.");
  const tokens: string[] = [];
  const lex = /\s*(\$board:mask:[A-Za-z_][A-Za-z0-9_]*:-?\d+:-?\d+|\d+n?|[A-Za-z_][A-Za-z0-9_]*|<<|>>|<=|>=|==|!=|&&|\|\||[()+\-*/%&|^~!<>?:,])/gy;
  let at = 0;
  while(at < source.length) {
    lex.lastIndex = at;
    const match = lex.exec(source);
    if(!match) {
      if(source.slice(at).trim() === "")
        break;
      throw new Error(`Unsupported preview syntax at character ${at + 1}.`);
    }
    tokens.push(match[1]);
    at = lex.lastIndex;
    if(tokens.length > 2048)
      throw new Error("Preview expression is too complex.");
  }
  let index = 0;
  const take = (expected: string) => {
    if(tokens[index++] !== expected)
      throw new Error(`Expected '${expected}' in preview expression.`);
  };
  function expression(min = 0, depth = 0): Node {
    if(depth > 64)
      throw new Error("Preview expression nesting exceeds 64 levels.");
    const token = tokens[index++];
    let left: Node;
    if(token === "(") {
      left = expression(0, depth + 1);
      take(")");
    }
    else if(["-", "+", "!", "~"].includes(token)) {
      left = { op: `unary${token}`, args: [expression(11, depth + 1)] };
    }
    else if(token === "boardShift" && tokens[index] === "(") {
      take("(");
      const mask = expression(0, depth + 1);
      take(",");
      const topology = tokens[index++];
      take(",");
      const direction = tokens[index++];
      take(")");
      if(![topology, direction].every(s => /^[A-Za-z_][A-Za-z0-9_]*$/.test(s)))
        throw new Error("Expected topology and direction names.");
      left = { op: "shift", args: [mask], value: `${topology}:${direction}` };
    }
    else if(token && /^(\d+n?|[A-Za-z_][A-Za-z0-9_]*|\$board:mask:[A-Za-z_][A-Za-z0-9_]*:-?\d+:-?\d+)$/.test(token)) {
      left = { op: "value", args: [], value: token };
    }
    else
      throw new Error(`Unexpected preview token '${token ?? "end of expression"}'.`);
    while(index < tokens.length) {
      const op = tokens[index];
      const rank = precedence[op];
      if(rank === undefined || rank < min)
        break;
      index++;
      left = { op, args: [left, expression(rank + 1, depth + 1)] };
    }
    if(min === 0 && tokens[index] === "?") {
      index++;
      const yes = expression(0, depth + 1);
      take(":");
      left = { op: "?:", args: [left, yes, expression(0, depth + 1)] };
    }
    return left;
  }
  const node = expression();
  if(index !== tokens.length)
    throw new Error(`Unsupported preview token '${tokens[index]}'.`);
  return node;
}
export function validatePreviewExpression(source: string): void { compiled(source); }
function compiled(source: string): Node {
  const found = cache.get(source);
  if(found)
    return found;
  const node = parse(source);
  if(cache.size >= 256)
    cache.delete(cache.keys().next().value!);
  cache.set(source, node);
  return node;
}
export function previewExpression(source: string, read: (name: string) => number | bigint | boolean, shift: (mask: bigint, topology: string, direction: string) => bigint): number | bigint {
  let remaining = 4096;
  const bounded = (value: bigint): bigint => {
    if(value < -(1n << 63n) || value >= (1n << 64n))
      throw new Error("Preview integer exceeds 64 bits.");
    return value;
  };
  function run(node: Node): bigint {
    if(--remaining < 0)
      throw new Error("Preview expression budget exceeded.");
    if(node.op === "value") {
      const key = node.value!;
      if(/^\d+n?$/.test(key))
        return bounded(BigInt(key.replace(/n$/, "")));
      if(key === "true")
        return 1n;
      if(key === "false")
        return 0n;
      const value = read(key);
      if(typeof value === "number" && !Number.isSafeInteger(value))
        throw new Error(`'${key}' is not an exact preview integer.`);
      return bounded(BigInt(value));
    }
    const a = run(node.args[0]);
    if(node.op === "?:")
      return run(node.args[a !== 0n ? 1 : 2]);
    if(node.op === "shift") {
      const [topology, direction] = node.value!.split(":");
      return bounded(shift(a, topology, direction));
    }
    if(node.op === "unary-")
      return bounded(-a);
    if(node.op === "unary+")
      return a;
    if(node.op === "unary!")
      return a === 0n ? 1n : 0n;
    if(node.op === "unary~")
      return ~a;
    if(node.op === "&&" && a === 0n)
      return 0n;
    if(node.op === "||" && a !== 0n)
      return 1n;
    const b = run(node.args[1]);
    switch(node.op) {
      case "+": return bounded(a + b);
      case "-": return bounded(a - b);
      case "*": return bounded(a * b);
      case "/":
        if(b === 0n)
          throw new Error("Division by zero.");
        return a / b;
      case "%":
        if(b === 0n)
          throw new Error("Division by zero.");
        return a % b;
      case "&": return a & b;
      case "|": return a | b;
      case "^": return a ^ b;
      case "<<":
      case ">>":
        if(b < 0n || b > 63n)
          throw new Error("Preview shift must be between 0 and 63.");
        return bounded(node.op === "<<" ? a << b : a >> b);
      case "==": return a === b ? 1n : 0n;
      case "!=": return a !== b ? 1n : 0n;
      case "<": return a < b ? 1n : 0n;
      case "<=": return a <= b ? 1n : 0n;
      case ">": return a > b ? 1n : 0n;
      case ">=": return a >= b ? 1n : 0n;
      case "&&":
      case "||": return b !== 0n ? 1n : 0n;
      default: throw new Error("Unsupported preview operator.");
    }
  }
  const result = run(compiled(source));
  return result >= BigInt(Number.MIN_SAFE_INTEGER) && result <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(result) : result;
}
