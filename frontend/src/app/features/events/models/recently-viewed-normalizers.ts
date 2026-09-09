import {
  asRecord,
  readBoolean,
  readNullableString,
  readNumber,
} from '../../../core/models/payload-casing';
import { EventItemPayload, normalizeEventItem } from './event-normalizers';
import {
  RecentlyViewedEntry,
  RecentlyViewedMergeResult,
  RecentlyViewedSettings,
  RecordEventViewResult,
} from './recently-viewed.types';

/**
 * The API serialises camelCase, but some endpoints have historically returned PascalCase, so both
 * spellings are tolerated here exactly as they are for events themselves.
 */
export function normalizeRecentlyViewedEntry(payload: unknown): RecentlyViewedEntry {
  const record = asRecord(payload) ?? {};
  const event = normalizeEventItem((record['Event'] ?? record['event']) as EventItemPayload);

  return {
    // Falls back to the nested event, so an entry stays addressable if the flat id is absent.
    eventId: readNumber(record, 'EventId', 'eventId') ?? event.id,
    viewedAtUtc: readNullableString(record, 'ViewedAtUtc', 'viewedAtUtc') ?? '',
    event,
  };
}

export function normalizeRecentlyViewedEntries(payload: unknown): RecentlyViewedEntry[] {
  return Array.isArray(payload) ? payload.map(normalizeRecentlyViewedEntry) : [];
}

export function normalizeRecentlyViewedSettings(payload: unknown): RecentlyViewedSettings {
  const record = asRecord(payload) ?? {};

  return {
    // Enabled is the default: an absent preference means the user has never turned tracking off,
    // and a malformed payload must not read as an opt-out they never made.
    enabled: readBoolean(record, 'Enabled', 'enabled') ?? true,
    updatedAtUtc: readNullableString(record, 'UpdatedAtUtc', 'updatedAtUtc') ?? null,
  };
}

export function normalizeMergeResult(payload: unknown): RecentlyViewedMergeResult {
  const record = asRecord(payload) ?? {};

  return {
    merged: readNumber(record, 'Merged', 'merged') ?? 0,
    skipped: readNumber(record, 'Skipped', 'skipped') ?? 0,
    total: readNumber(record, 'Total', 'total') ?? 0,
  };
}

export function normalizeRecordViewResult(
  eventId: number,
  payload: unknown,
): RecordEventViewResult {
  const record = asRecord(payload) ?? {};

  return {
    eventId: readNumber(record, 'EventId', 'eventId') ?? eventId,
    recorded: readBoolean(record, 'Recorded', 'recorded') ?? false,
    viewedAtUtc: readNullableString(record, 'ViewedAtUtc', 'viewedAtUtc') ?? null,
  };
}
