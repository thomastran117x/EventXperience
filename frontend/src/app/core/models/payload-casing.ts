/**
 * Readers that accept a field under either casing.
 *
 * The API serialises camelCase (`usernameDisplay`), while much of this codebase types its response
 * models in PascalCase. Where the two disagree the field silently reads as `undefined`, which is
 * how a stored value can look missing on screen while the row is perfectly correct — so anything
 * consuming a raw payload should go through these rather than casting.
 *
 * Pass the PascalCase key first and the camelCase one second, so a payload carrying both resolves
 * the same way everywhere.
 */

export type UnknownRecord = Record<string, unknown>;

export function asRecord(value: unknown): UnknownRecord | null {
  return typeof value === 'object' && value !== null ? (value as UnknownRecord) : null;
}

export function readString(source: UnknownRecord, ...keys: string[]): string | undefined {
  for (const key of keys) {
    const value = source[key];
    if (typeof value === 'string') {
      return value;
    }
  }

  return undefined;
}

export function readNullableString(
  source: UnknownRecord,
  ...keys: string[]
): string | null | undefined {
  for (const key of keys) {
    const value = source[key];
    if (typeof value === 'string' || value === null) {
      return value;
    }
  }

  return undefined;
}

export function readNumber(source: UnknownRecord, ...keys: string[]): number | undefined {
  for (const key of keys) {
    const value = source[key];
    if (typeof value === 'number' && Number.isFinite(value)) {
      return value;
    }

    if (typeof value === 'string') {
      const parsed = Number(value);
      if (Number.isFinite(parsed)) {
        return parsed;
      }
    }
  }

  return undefined;
}

export function readBoolean(source: UnknownRecord, ...keys: string[]): boolean | undefined {
  for (const key of keys) {
    const value = source[key];
    if (typeof value === 'boolean') {
      return value;
    }
  }

  return undefined;
}
