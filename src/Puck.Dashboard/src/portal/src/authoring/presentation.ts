export interface ValueAppearance { label: string; color: string; hidden?: boolean; shape?: "cube" | "sphere" | "diamond" }
export interface PresentationBindings { [stateName: string]: { [value: string]: ValueAppearance } }
const palette = ["#74c9ba", "#efaf91", "#a8b8f8", "#dfaad5", "#d5c67d", "#92bed4"];
export function appearanceFor(value: number, bindings?: Record<string, ValueAppearance>): ValueAppearance {
  return bindings?.[String(value)] ?? { label: String(value), color: value === 0 ? "#718497" : palette[Math.abs(value) % palette.length] };
}
/** Studio metadata uses the native custom string extension; no renderer objects are serialized. */
export function readPresentation(world: any): PresentationBindings {
  const text = world.metadata?.custom?.puckStudioPresentation;
  if (text === undefined) return {};
  if (typeof text !== "string") throw new Error("Studio presentation metadata must be a JSON string.");
  const data = JSON.parse(text);
  if (!data || typeof data !== "object" || Array.isArray(data)) throw new Error("Invalid studio presentation bindings.");
  for (const entries of Object.values(data)) {
    if (!entries || typeof entries !== "object" || Array.isArray(entries)) throw new Error("Invalid state presentation binding.");
    for (const [value, entry] of Object.entries(entries)) {
      const binding = entry as ValueAppearance;
      if (!/^-?\d+$/.test(value) || !Number.isSafeInteger(Number(value)) || !binding || typeof binding.label !== "string" || binding.label.length > 80 ||
          !/^#[\da-f]{6}$/i.test(binding.color) || (binding.hidden !== undefined && typeof binding.hidden !== "boolean") || (binding.shape !== undefined && !["cube", "sphere", "diamond"].includes(binding.shape))) throw new Error("Presentation entries require an integer value, a short label, a hex color, and an optional shape or hidden flag.");
    }
  }
  return data;
}
export function bindAppearance(world: any, stateName: string, value: number, appearance: ValueAppearance): any {
  if (!world.state.world.some((row: any) => row.name === stateName && row.domain)) throw new Error("Choose a cell state to bind.");
  const bindings = readPresentation(world);
  const result = { ...world, metadata: { ...world.metadata, custom: { ...world.metadata?.custom,
    puckStudioPresentation: JSON.stringify({ ...bindings, [stateName]: { ...bindings[stateName], [value]: appearance } }),
  } } };
  readPresentation(result);
  return result;
}
