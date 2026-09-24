import {
  type Data,
  type Date_,
  DateUnit,
  type Decimal,
  type FixedSizeList,
  type Float,
  type Interval,
  IntervalUnit,
  makeVector,
  Precision,
  type RecordBatch,
  type Schema,
  type Struct,
  type Time,
  type Timestamp,
  TimeUnit,
  Type,
  type DataType,
  type Vector,
} from "apache-arrow";

// DuckDB answers a query with Arrow record batches. This module turns them into the text the results table shows,
// written the way DuckDB itself writes each value (`CAST(value AS VARCHAR)`), and reads every value by its column's
// position and type. Arrow's convenience getters are not enough for that: they return timestamps and dates as
// millisecond numbers (losing micro- and nanoseconds), decimals unscaled, and intervals as raw words, so the
// temporal types are read from the column's own storage instead.

/** The results table's contents: every cell as DuckDB would print it, or `null` for SQL NULL. */
export interface QueryOutput {
  /** Column names in select order; two columns may share a name. */
  columns: string[];
  rows: (string | null)[][];
  /** The query produced more rows than `rowLimit`; only the first `rowLimit` are here. */
  truncated: boolean;
}

/** How many rows a query shows before the rest is left unread. */
export const RESULT_ROW_LIMIT = 1000;

const MICROS_PER_SECOND = 1_000_000n;
const SECONDS_PER_DAY = 86_400n;
const UNITS_PER_SECOND = [1n, 1_000n, 1_000_000n, 1_000_000_000n] as const;
const FRACTION_DIGITS = [0, 3, 6, 9] as const;
const INT64_MAX = 9_223_372_036_854_775_807n;

const pad = (value: bigint | number, width: number) => String(value).padStart(width, "0");

/** Floor division, so a moment before 1970 falls on the day before, not the day after. */
const floorDivide = (dividend: bigint, divisor: bigint) => {
  const quotient = dividend / divisor;

  return dividend % divisor < 0n ? quotient - 1n : quotient;
};

/** The storage slot holding a vector's `index`th value, across the vector's chunks. */
function locate(vector: Vector, index: number): { data: Data; position: number } {
  let remaining = index;

  for (const data of vector.data) {
    if (remaining < data.length) {
      return { data: data, position: data.offset + remaining };
    }

    remaining -= data.length;
  }

  throw new RangeError(`Row ${index} is outside a column of ${vector.length}.`);
}

/** A civil date in the proleptic Gregorian calendar, from days since 1970-01-01, as DuckDB prints it. */
function formatDate(daysSinceEpoch: bigint): string {
  // Howard Hinnant's days_from_civil inverse: exact for every date DuckDB can hold, unlike `Date`, which stops at
  // ±275,760 years.
  const z = daysSinceEpoch + 719_468n;
  const era = floorDivide(z, 146_097n);
  const dayOfEra = z - era * 146_097n;
  const yearOfEra = (dayOfEra - dayOfEra / 1_460n + dayOfEra / 36_524n - dayOfEra / 146_096n) / 365n;
  const dayOfYear = dayOfEra - (365n * yearOfEra + yearOfEra / 4n - yearOfEra / 100n);
  const monthPart = (5n * dayOfYear + 2n) / 153n;
  const day = dayOfYear - (153n * monthPart + 2n) / 5n + 1n;
  const month = monthPart < 10n ? monthPart + 3n : monthPart - 9n;
  const year = yearOfEra + era * 400n + (month <= 2n ? 1n : 0n);

  // DuckDB has no year zero: 1 BC is year 0 in the proleptic calendar.
  return year > 0n ? `${pad(year, 4)}-${pad(month, 2)}-${pad(day, 2)}` : `${pad(1n - year, 4)}-${pad(month, 2)}-${pad(day, 2)} (BC)`;
}

/** A fraction of a second, `units` of `1 / unitsPerSecond`, with trailing zeros dropped: `.5`, `.123456`. */
function formatFraction(units: bigint, digits: number): string {
  if (0n === units || 0 === digits) {
    return "";
  }

  return `.${pad(units, digits).replace(/0+$/, "")}`;
}

/** A time of day or a duration's clock part: `HH:MM:SS[.fraction]`; hours may exceed 23 for a duration. */
function formatClock(totalUnits: bigint, unitsPerSecond: bigint, digits: number): string {
  const seconds = totalUnits / unitsPerSecond;

  return `${pad(seconds / 3_600n, 2)}:${pad((seconds / 60n) % 60n, 2)}:${pad(seconds % 60n, 2)}${formatFraction(totalUnits % unitsPerSecond, digits)}`;
}

function formatTimestamp(raw: bigint, unit: TimeUnit, timezone: string | null): string {
  if (INT64_MAX === raw) {
    return "infinity";
  }

  if (-INT64_MAX === raw) {
    return "-infinity";
  }

  const unitsPerSecond = UNITS_PER_SECOND[unit];
  const seconds = floorDivide(raw, unitsPerSecond);
  const fraction = raw - seconds * unitsPerSecond;
  const days = floorDivide(seconds, SECONDS_PER_DAY);
  const clock = formatClock((seconds - days * SECONDS_PER_DAY) * unitsPerSecond + fraction, unitsPerSecond, FRACTION_DIGITS[unit]);
  // DuckDB hands Arrow every TIMESTAMPTZ in UTC and prints UTC as a `+00` offset.
  const zone = null === timezone ? "" : "UTC" === timezone || "+00:00" === timezone ? "+00" : ` ${timezone}`;

  // A BC date keeps its `(BC)` beside the date: `0044-03-15 (BC) 12:00:00`.
  return `${formatDate(days)} ${clock}${zone}`;
}

/** A MONTH_DAY_NANO interval as DuckDB prints it: `1 year 2 months 3 days 04:05:06.5`, or `00:00:00` when empty. */
function formatInterval(months: number, days: number, nanoseconds: bigint): string {
  const parts: string[] = [];
  const years = Math.trunc(months / 12);
  const remainingMonths = months % 12;
  const plural = (count: number, unit: string) => `${count} ${unit}${1 === Math.abs(count) ? "" : "s"}`;

  if (0 !== years) parts.push(plural(years, "year"));
  if (0 !== remainingMonths) parts.push(plural(remainingMonths, "month"));
  if (0 !== days) parts.push(plural(days, "day"));

  // DuckDB keeps microseconds; the clock part prints them, not nanoseconds.
  const micros = nanoseconds / 1_000n;

  if (0n !== micros || 0 === parts.length) {
    const magnitude = micros < 0n ? -micros : micros;

    parts.push(`${micros < 0n ? "-" : ""}${formatClock(magnitude, MICROS_PER_SECOND, 6)}`);
  }

  return parts.join(" ");
}

/** An unscaled decimal's digits with the point set `scale` places from the right: `-12345`, 3 → `-12.345`. */
function formatDecimal(unscaled: string, scale: number): string {
  if (0 === scale) {
    return unscaled;
  }

  const negative = unscaled.startsWith("-");
  const digits = (negative ? unscaled.slice(1) : unscaled).padStart(scale + 1, "0");

  return `${negative ? "-" : ""}${digits.slice(0, -scale)}.${digits.slice(-scale)}`;
}

/**
 * A float the way DuckDB prints it: the shortest digits that read back as the same value, in fixed notation for
 * decimal exponents from -4 to 15 (always with a fractional part, `2.0`) and scientific notation outside them, with a
 * signed exponent of at least two digits (`1e+20`, `1e-07`).
 */
function formatFloat(value: number, shortest: (value: number) => string): string {
  if (Number.isNaN(value)) {
    return "nan";
  }

  if (!Number.isFinite(value)) {
    return 0 < value ? "inf" : "-inf";
  }

  if (0 === value) {
    return Object.is(value, -0) ? "-0.0" : "0.0";
  }

  // `shortest` answers in exponential form: "-1.2345e+2".
  const [mantissa, exponentText] = shortest(Math.abs(value)).split("e");
  const exponent = Number(exponentText);
  const digits = mantissa.replace(".", "");
  const sign = value < 0 ? "-" : "";

  if (exponent < -4 || 16 <= exponent) {
    const fraction = 1 < digits.length ? `.${digits.slice(1)}` : "";

    return `${sign}${digits[0]}${fraction}e${exponent < 0 ? "-" : "+"}${String(Math.abs(exponent)).padStart(2, "0")}`;
  }

  if (exponent < 0) {
    return `${sign}0.${"0".repeat(-exponent - 1)}${digits}`;
  }

  const whole = digits.slice(0, exponent + 1).padEnd(exponent + 1, "0");
  const fraction = digits.slice(exponent + 1);

  return `${sign}${whole}.${fraction || "0"}`;
}

// JavaScript's own shortest round-trip digits for a double.
const shortestFloat64 = (value: number) => value.toExponential();

/** The shortest digits that read back as the same 32-bit float (a REAL). */
function shortestFloat32(value: number): string {
  for (let precision = 1; precision < 9; precision++) {
    const candidate = value.toExponential(precision - 1);

    if (Math.fround(Number(candidate)) === value) {
      return candidate;
    }
  }

  return value.toExponential(8);
}

/** Bytes as DuckDB prints a BLOB: printable ASCII as itself, everything else (and `\`) as `\xHH`. */
function formatBytes(bytes: Uint8Array): string {
  let text = "";

  for (const byte of bytes) {
    text += 32 <= byte && byte <= 126 && 92 !== byte ? String.fromCharCode(byte) : `\\x${byte.toString(16).toUpperCase().padStart(2, "0")}`;
  }

  return text;
}

const NESTED_TYPES = new Set<Type>([Type.List, Type.FixedSizeList, Type.Struct, Type.Map]);
// The characters that make DuckDB quote a scalar inside a nested value (`NestedToVarcharCast::LOOKUP_TABLE`).
const NEEDS_QUOTES = /["'(),:=[\]{}]/;
const SPACE = /^[ \t\n\v\f\r]$/;

/** A scalar's text written inside a nested value, quoted and escaped exactly when DuckDB would (`WriteEscapedString`). */
function quoteNested(text: string, always: boolean): string {
  const needsQuotes =
    always ||
    "" === text ||
    SPACE.test(text[0]) ||
    (2 <= text.length && SPACE.test(text.at(-1)!)) ||
    "null" === text.toLowerCase() ||
    NEEDS_QUOTES.test(text);

  return needsQuotes ? `'${text.replace(/['\\]/g, "\\$&")}'` : text;
}

/** A value nested in a LIST, STRUCT, or MAP: NULL spelled out, since the cell as a whole is not null. */
function nested(vector: Vector, index: number): string {
  const text = formatCell(vector, index);

  if (null === text) {
    return "NULL";
  }

  return NESTED_TYPES.has(vector.type.typeId) ? text : quoteNested(text, false);
}

/** Entries `[start, end)` of a variable-length child, for LIST and MAP. */
function childRange(data: Data, position: number): [number, number] {
  const offsets = data.valueOffsets as Int32Array;

  return [offsets[position], offsets[position + 1]];
}

/** One cell as DuckDB would print it, or `null` for SQL NULL. */
export function formatCell(vector: Vector, index: number): string | null {
  if (!vector.isValid(index)) {
    return null;
  }

  const type: DataType = vector.type;

  switch (type.typeId) {
    case Type.Timestamp: {
      const { data, position } = locate(vector, index);

      return formatTimestamp(BigInt((data.values as BigInt64Array)[position]), (type as Timestamp).unit, (type as Timestamp).timezone ?? null);
    }
    case Type.Date: {
      const { data, position } = locate(vector, index);
      const raw = (data.values as Int32Array | BigInt64Array)[position];

      return DateUnit.DAY === (type as Date_).unit ? formatDate(BigInt(raw)) : formatDate(floorDivide(BigInt(raw), 86_400_000n));
    }
    case Type.Time: {
      const { data, position } = locate(vector, index);
      const unit = (type as Time).unit;

      return formatClock(BigInt((data.values as Int32Array | BigInt64Array)[position]), UNITS_PER_SECOND[unit], FRACTION_DIGITS[unit]);
    }
    case Type.Interval: {
      const { data, position } = locate(vector, index);

      if (IntervalUnit.MONTH_DAY_NANO !== (type as Interval).unit) {
        return String(vector.get(index));
      }

      // Four 32-bit words per value: months, days, then nanoseconds as a little-endian 64-bit integer.
      const words = (data.values as Int32Array).subarray(position * 4, position * 4 + 4);

      return formatInterval(words[0], words[1], (BigInt(words[3]) << 32n) | BigInt(words[2] >>> 0));
    }
    case Type.Decimal:
      return formatDecimal(String(vector.get(index)), (type as Decimal).scale);
    case Type.Float:
      return formatFloat(vector.get(index), Precision.SINGLE === (type as Float).precision ? shortestFloat32 : shortestFloat64);
    case Type.Binary:
    case Type.LargeBinary:
    case Type.FixedSizeBinary:
      return formatBytes(vector.get(index) as Uint8Array);
    case Type.List: {
      const { data, position } = locate(vector, index);
      const [start, end] = childRange(data, position);
      const child = makeVector(data.children[0]);

      return `[${Array.from({ length: end - start }, (_, offset) => nested(child, start + offset)).join(", ")}]`;
    }
    case Type.FixedSizeList: {
      const { data, position } = locate(vector, index);
      const size = (type as FixedSizeList).listSize;
      const child = makeVector(data.children[0]);

      return `[${Array.from({ length: size }, (_, offset) => nested(child, position * size + offset)).join(", ")}]`;
    }
    case Type.Struct: {
      const fields = (type as Struct).children;

      return `{${fields.map((field, child) => `${quoteNested(field.name, true)}: ${nested(vector.getChildAt(child)!, index)}`).join(", ")}}`;
    }
    case Type.Map: {
      const { data, position } = locate(vector, index);
      const [start, end] = childRange(data, position);
      const entries = makeVector(data.children[0]);
      const keys = entries.getChildAt(0)!;
      const values = entries.getChildAt(1)!;

      return `{${Array.from({ length: end - start }, (_, offset) => `${nested(keys, start + offset)}=${nested(values, start + offset)}`).join(", ")}}`;
    }
    default: {
      const value: unknown = vector.get(index);

      return "bigint" === typeof value || "boolean" === typeof value || "number" === typeof value ? String(value) : String(value ?? "");
    }
  }
}

/** Every row of a batch, as text. */
export function formatBatch(batch: RecordBatch): (string | null)[][] {
  const columns = batch.schema.fields.map((_, column) => batch.getChildAt(column)!);

  return Array.from({ length: batch.numRows }, (_, row) => columns.map((vector) => formatCell(vector, row)));
}

/**
 * Reads a query's batches until `rowLimit` rows are in hand, then stops: the rest of a large result is never
 * transferred from the engine or formatted. Leaving the loop early closes the stream.
 */
export async function collectRows(
  batches: AsyncIterable<RecordBatch> & { schema?: Schema | null },
  rowLimit: number = RESULT_ROW_LIMIT,
): Promise<QueryOutput> {
  const rows: (string | null)[][] = [];
  let schema: Schema | null = null;
  let truncated = false;

  for await (const batch of batches) {
    schema = batch.schema;

    if (rows.length === rowLimit) {
      truncated = 0 < batch.numRows;

      if (truncated) break;

      continue;
    }

    const formatted = formatBatch(batch.numRows > rowLimit - rows.length ? batch.slice(0, rowLimit - rows.length) : batch);

    rows.push(...formatted);

    if (batch.numRows > formatted.length) {
      truncated = true;
      break;
    }
  }

  schema ??= batches.schema ?? null;

  return { columns: schema?.fields.map((field) => field.name) ?? [], rows: rows, truncated: truncated };
}
