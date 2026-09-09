import {
  RecentlyViewedLocalItem,
  RecentlyViewedLocalState,
  RecentlyViewedMaxItems,
  RecentlyViewedRetentionDays,
} from '../models/recently-viewed.types';

export const RecentlyViewedStorageKey = 'recently-viewed-events';
export const RecentlyViewedOptOutStorageKey = 'recently-viewed-opt-out';

const StateVersion = 1;
const RetentionMs = RecentlyViewedRetentionDays * 24 * 60 * 60 * 1000;

/**
 * The signed-out half of the history, held in localStorage.
 *
 * Plain functions rather than a service: nothing here needs Angular, and keeping the branchy
 * parsing separate from the store makes both easier to reason about and to cover.
 *
 * Only ids and timestamps are persisted, never event payloads — the blob stays small, and nothing
 * privileged or stale sits at rest in a browser that may be shared.
 *
 * Every entry point tolerates storage being unavailable. SSR has no localStorage at all, private
 * mode throws on write, and a browser set to block site data throws on read, so a failure here has
 * to degrade to "no history" rather than break the page.
 */
export function readLocalHistory(now: number = Date.now()): RecentlyViewedLocalItem[] {
  const raw = readRaw(RecentlyViewedStorageKey);
  if (raw === null) {
    return [];
  }

  const parsed = parseState(raw);
  if (parsed === null) {
    return [];
  }

  // Pruned and capped on the way out, so the local buffer honours the same contract as the server
  // without needing a sweeper of its own.
  return capAndSort(parsed.items.filter((item) => !isExpired(item, now)));
}

export function writeLocalHistory(
  items: RecentlyViewedLocalItem[],
  now: number = Date.now(),
): void {
  const state: RecentlyViewedLocalState = {
    v: StateVersion,
    items: capAndSort(items.filter((item) => !isExpired(item, now))),
  };

  writeRaw(RecentlyViewedStorageKey, JSON.stringify(state));
}

/** Moves an event to the head, de-duplicating by id rather than stacking repeat views. */
export function addLocalView(eventId: number, now: number = Date.now()): RecentlyViewedLocalItem[] {
  const existing = readLocalHistory(now).filter((item) => item.id !== eventId);
  const next = [{ id: eventId, at: new Date(now).toISOString() }, ...existing];

  writeLocalHistory(next, now);
  return capAndSort(next);
}

export function removeLocalViews(
  eventIds: number[],
  now: number = Date.now(),
): RecentlyViewedLocalItem[] {
  const doomed = new Set(eventIds);
  const next = readLocalHistory(now).filter((item) => !doomed.has(item.id));

  writeLocalHistory(next, now);
  return next;
}

export function clearLocalHistory(): void {
  removeRaw(RecentlyViewedStorageKey);
}

export function readLocalOptOut(): boolean {
  return readRaw(RecentlyViewedOptOutStorageKey) === 'true';
}

export function writeLocalOptOut(optedOut: boolean): void {
  if (optedOut) {
    writeRaw(RecentlyViewedOptOutStorageKey, 'true');
    return;
  }

  removeRaw(RecentlyViewedOptOutStorageKey);
}

/**
 * Validates the stored blob rather than trusting it. A hand-edited value, a half-written entry, or
 * a blob from a future version of this code must read as "no history" instead of throwing on a
 * page the user is simply trying to load.
 */
function parseState(raw: string): RecentlyViewedLocalState | null {
  let parsed: unknown;

  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  if (typeof parsed !== 'object' || parsed === null) {
    return null;
  }

  const candidate = parsed as Partial<RecentlyViewedLocalState>;
  if (candidate.v !== StateVersion || !Array.isArray(candidate.items)) {
    return null;
  }

  return { v: StateVersion, items: candidate.items.filter(isValidItem) };
}

function isValidItem(item: unknown): item is RecentlyViewedLocalItem {
  if (typeof item !== 'object' || item === null) {
    return false;
  }

  const candidate = item as Partial<RecentlyViewedLocalItem>;
  return (
    typeof candidate.id === 'number' &&
    Number.isFinite(candidate.id) &&
    candidate.id > 0 &&
    typeof candidate.at === 'string' &&
    Number.isFinite(Date.parse(candidate.at))
  );
}

function isExpired(item: RecentlyViewedLocalItem, now: number): boolean {
  return now - Date.parse(item.at) > RetentionMs;
}

/** Newest first, then capped — the same order and cap the server presents. */
function capAndSort(items: RecentlyViewedLocalItem[]): RecentlyViewedLocalItem[] {
  return [...items]
    .sort((a, b) => Date.parse(b.at) - Date.parse(a.at))
    .slice(0, RecentlyViewedMaxItems);
}

function readRaw(key: string): string | null {
  if (typeof localStorage === 'undefined') {
    return null;
  }

  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeRaw(key: string, value: string): void {
  if (typeof localStorage === 'undefined') {
    return;
  }

  try {
    localStorage.setItem(key, value);
  } catch {
    // Ignore storage failures (e.g. private mode quotas). A history that cannot be saved is a
    // lost convenience, never a broken page.
  }
}

function removeRaw(key: string): void {
  if (typeof localStorage === 'undefined') {
    return;
  }

  try {
    localStorage.removeItem(key);
  } catch {
    // Ignore storage failures, as above.
  }
}
