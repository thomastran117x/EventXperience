import { EventItem } from './event.types';

/** How many entries a history holds, mirroring the server cap. */
export const RecentlyViewedMaxItems = 50;

/** How long an entry survives, mirroring the server retention window. */
export const RecentlyViewedRetentionDays = 90;

/** One entry in the history, with the event hydrated so surfaces can render it directly. */
export interface RecentlyViewedEntry {
  eventId: number;
  viewedAtUtc: string;
  event: EventItem;
}

/**
 * What the browser keeps for a signed-out visitor: ids and timestamps only, never event payloads.
 * Keeps the stored blob small, and leaves nothing at rest that could go stale or disclose an event
 * the visitor has since lost access to.
 */
export interface RecentlyViewedLocalItem {
  id: number;
  at: string;
}

/** The persisted envelope. Versioned so a future shape change can discard the old one cleanly. */
export interface RecentlyViewedLocalState {
  v: 1;
  items: RecentlyViewedLocalItem[];
}

export interface RecentlyViewedSettings {
  enabled: boolean;
  updatedAtUtc: string | null;
}

export interface RecentlyViewedMergeResult {
  merged: number;
  skipped: number;
  total: number;
}

export interface RecordEventViewResult {
  eventId: number;
  recorded: boolean;
  viewedAtUtc: string | null;
}
