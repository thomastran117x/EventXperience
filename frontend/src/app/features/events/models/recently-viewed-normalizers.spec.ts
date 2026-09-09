import {
  normalizeMergeResult,
  normalizeRecentlyViewedEntries,
  normalizeRecentlyViewedEntry,
  normalizeRecentlyViewedSettings,
  normalizeRecordViewResult,
} from './recently-viewed-normalizers';

describe('recently-viewed normalizers', () => {
  describe('normalizeRecentlyViewedEntry', () => {
    it('reads a camelCase payload', () => {
      const entry = normalizeRecentlyViewedEntry({
        eventId: 7,
        viewedAtUtc: '2026-09-09T12:00:00Z',
        event: { id: 7, name: 'Robotics Night' },
      });

      expect(entry.eventId).toBe(7);
      expect(entry.viewedAtUtc).toBe('2026-09-09T12:00:00Z');
      expect(entry.event.name).toBe('Robotics Night');
    });

    it('reads a PascalCase payload', () => {
      const entry = normalizeRecentlyViewedEntry({
        EventId: 7,
        ViewedAtUtc: '2026-09-09T12:00:00Z',
        Event: { Id: 7, Name: 'Robotics Night' },
      });

      expect(entry.eventId).toBe(7);
      expect(entry.viewedAtUtc).toBe('2026-09-09T12:00:00Z');
      expect(entry.event.name).toBe('Robotics Night');
    });

    it('falls back to the nested event id when the flat id is absent', () => {
      const entry = normalizeRecentlyViewedEntry({ event: { id: 12 } });

      // An entry still has to be addressable, or the page cannot offer to remove it.
      expect(entry.eventId).toBe(12);
    });

    it('survives an empty payload', () => {
      const entry = normalizeRecentlyViewedEntry({});

      expect(entry.viewedAtUtc).toBe('');
      expect(entry.event).toBeTruthy();
    });

    it('survives a null payload', () => {
      expect(() => normalizeRecentlyViewedEntry(null)).not.toThrow();
    });
  });

  describe('normalizeRecentlyViewedEntries', () => {
    it('maps every item in the list', () => {
      const entries = normalizeRecentlyViewedEntries([
        { eventId: 1, viewedAtUtc: 'a', event: { id: 1 } },
        { EventId: 2, ViewedAtUtc: 'b', Event: { Id: 2 } },
      ]);

      expect(entries.map((entry) => entry.eventId)).toEqual([1, 2]);
    });

    it('returns an empty list for a non-array payload', () => {
      expect(normalizeRecentlyViewedEntries(null)).toEqual([]);
      expect(normalizeRecentlyViewedEntries({})).toEqual([]);
      expect(normalizeRecentlyViewedEntries(undefined)).toEqual([]);
    });
  });

  describe('normalizeRecentlyViewedSettings', () => {
    it('reads a camelCase payload', () => {
      const settings = normalizeRecentlyViewedSettings({
        enabled: false,
        updatedAtUtc: '2026-09-09T12:00:00Z',
      });

      expect(settings.enabled).toBeFalse();
      expect(settings.updatedAtUtc).toBe('2026-09-09T12:00:00Z');
    });

    it('reads a PascalCase payload', () => {
      const settings = normalizeRecentlyViewedSettings({
        Enabled: false,
        UpdatedAtUtc: '2026-09-09T12:00:00Z',
      });

      expect(settings.enabled).toBeFalse();
      expect(settings.updatedAtUtc).toBe('2026-09-09T12:00:00Z');
    });

    it('defaults to enabled with no timestamp on an empty payload', () => {
      const settings = normalizeRecentlyViewedSettings({});

      // A malformed payload must never read as an opt-out the user did not make.
      expect(settings.enabled).toBeTrue();
      expect(settings.updatedAtUtc).toBeNull();
    });

    it('defaults to enabled on a null payload', () => {
      expect(normalizeRecentlyViewedSettings(null).enabled).toBeTrue();
    });
  });

  describe('normalizeMergeResult', () => {
    it('reads a camelCase payload', () => {
      expect(normalizeMergeResult({ merged: 2, skipped: 1, total: 3 })).toEqual({
        merged: 2,
        skipped: 1,
        total: 3,
      });
    });

    it('reads a PascalCase payload', () => {
      expect(normalizeMergeResult({ Merged: 2, Skipped: 1, Total: 3 })).toEqual({
        merged: 2,
        skipped: 1,
        total: 3,
      });
    });

    it('zeroes an empty payload', () => {
      expect(normalizeMergeResult({})).toEqual({ merged: 0, skipped: 0, total: 0 });
    });

    it('zeroes a payload that is not an object at all', () => {
      expect(normalizeMergeResult(null)).toEqual({ merged: 0, skipped: 0, total: 0 });
      expect(normalizeMergeResult('nope')).toEqual({ merged: 0, skipped: 0, total: 0 });
    });
  });

  describe('normalizeRecordViewResult', () => {
    it('reads a camelCase payload', () => {
      const result = normalizeRecordViewResult(1, {
        eventId: 7,
        recorded: true,
        viewedAtUtc: '2026-09-09T12:00:00Z',
      });

      expect(result).toEqual({
        eventId: 7,
        recorded: true,
        viewedAtUtc: '2026-09-09T12:00:00Z',
      });
    });

    it('reads a PascalCase payload', () => {
      const result = normalizeRecordViewResult(1, {
        EventId: 7,
        Recorded: true,
        ViewedAtUtc: '2026-09-09T12:00:00Z',
      });

      expect(result.eventId).toBe(7);
      expect(result.recorded).toBeTrue();
    });

    it('falls back to the requested id and reports not recorded on an empty payload', () => {
      expect(normalizeRecordViewResult(4, {})).toEqual({
        eventId: 4,
        recorded: false,
        viewedAtUtc: null,
      });
    });

    it('falls back the same way for a payload that is not an object', () => {
      expect(normalizeRecordViewResult(4, null)).toEqual({
        eventId: 4,
        recorded: false,
        viewedAtUtc: null,
      });
    });
  });
});
