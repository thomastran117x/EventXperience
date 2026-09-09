import { installMemoryStorage, installThrowingStorage } from '@testing';

import {
  RecentlyViewedOptOutStorageKey,
  RecentlyViewedStorageKey,
  addLocalView,
  clearLocalHistory,
  readLocalHistory,
  readLocalOptOut,
  removeLocalViews,
  writeLocalHistory,
  writeLocalOptOut,
} from './recently-viewed-storage';

describe('recently-viewed-storage', () => {
  const now = Date.parse('2026-09-09T12:00:00Z');
  let restore: () => void;

  function daysAgo(days: number): string {
    return new Date(now - days * 24 * 60 * 60 * 1000).toISOString();
  }

  function seed(items: { id: number; at: string }[]): Record<string, string> {
    return { [RecentlyViewedStorageKey]: JSON.stringify({ v: 1, items }) };
  }

  afterEach(() => restore?.());

  describe('readLocalHistory', () => {
    it('returns an empty history when nothing is stored', () => {
      restore = installMemoryStorage('local');

      expect(readLocalHistory(now)).toEqual([]);
    });

    it('returns stored entries newest first', () => {
      restore = installMemoryStorage(
        'local',
        seed([
          { id: 1, at: daysAgo(3) },
          { id: 2, at: daysAgo(1) },
          { id: 3, at: daysAgo(2) },
        ]),
      );

      expect(readLocalHistory(now).map((item) => item.id)).toEqual([2, 3, 1]);
    });

    it('prunes entries past the retention window', () => {
      restore = installMemoryStorage(
        'local',
        seed([
          { id: 1, at: daysAgo(91) },
          { id: 2, at: daysAgo(89) },
        ]),
      );

      // The local buffer honours the same 90-day promise as the server, without a sweeper.
      expect(readLocalHistory(now).map((item) => item.id)).toEqual([2]);
    });

    it('caps the history at 50 entries', () => {
      const items = Array.from({ length: 60 }, (_, index) => ({
        id: index + 1,
        at: new Date(now - index * 60_000).toISOString(),
      }));
      restore = installMemoryStorage('local', seed(items));

      expect(readLocalHistory(now).length).toBe(50);
    });

    it('discards a blob that is not valid JSON', () => {
      restore = installMemoryStorage('local', { [RecentlyViewedStorageKey]: 'not json{' });

      expect(readLocalHistory(now)).toEqual([]);
    });

    it('discards a blob that is not an object', () => {
      restore = installMemoryStorage('local', { [RecentlyViewedStorageKey]: '"a string"' });

      expect(readLocalHistory(now)).toEqual([]);
    });

    it('discards a null blob', () => {
      restore = installMemoryStorage('local', { [RecentlyViewedStorageKey]: 'null' });

      expect(readLocalHistory(now)).toEqual([]);
    });

    it('discards a blob from a different version', () => {
      restore = installMemoryStorage('local', {
        [RecentlyViewedStorageKey]: JSON.stringify({ v: 2, items: [{ id: 1, at: daysAgo(1) }] }),
      });

      expect(readLocalHistory(now)).toEqual([]);
    });

    it('discards a blob whose items are not an array', () => {
      restore = installMemoryStorage('local', {
        [RecentlyViewedStorageKey]: JSON.stringify({ v: 1, items: 'nope' }),
      });

      expect(readLocalHistory(now)).toEqual([]);
    });

    it('drops malformed entries but keeps the valid ones', () => {
      restore = installMemoryStorage('local', {
        [RecentlyViewedStorageKey]: JSON.stringify({
          v: 1,
          items: [
            { id: 1, at: daysAgo(1) },
            { id: 'two', at: daysAgo(1) },
            { id: 3 },
            { at: daysAgo(1) },
            { id: 0, at: daysAgo(1) },
            { id: 5, at: 'not a date' },
            null,
            'nope',
          ],
        }),
      });

      expect(readLocalHistory(now).map((item) => item.id)).toEqual([1]);
    });

    it('returns an empty history when storage throws', () => {
      restore = installThrowingStorage('local');

      // Private mode and blocked site data both throw on read; a history that cannot be read is a
      // lost convenience, not a broken page.
      expect(() => readLocalHistory(now)).not.toThrow();
      expect(readLocalHistory(now)).toEqual([]);
    });
  });

  describe('addLocalView', () => {
    it('puts a new event at the head', () => {
      restore = installMemoryStorage('local', seed([{ id: 1, at: daysAgo(1) }]));

      expect(addLocalView(2, now).map((item) => item.id)).toEqual([2, 1]);
      expect(readLocalHistory(now).map((item) => item.id)).toEqual([2, 1]);
    });

    it('moves a repeat view to the head without duplicating it', () => {
      restore = installMemoryStorage(
        'local',
        seed([
          { id: 1, at: daysAgo(2) },
          { id: 2, at: daysAgo(1) },
        ]),
      );

      const result = addLocalView(1, now);

      expect(result.map((item) => item.id)).toEqual([1, 2]);
      expect(result.length).toBe(2);
    });

    it('caps the stored history at 50 entries', () => {
      const items = Array.from({ length: 50 }, (_, index) => ({
        id: index + 1,
        at: new Date(now - (index + 1) * 60_000).toISOString(),
      }));
      restore = installMemoryStorage('local', seed(items));

      const result = addLocalView(999, now);

      expect(result.length).toBe(50);
      expect(result[0].id).toBe(999);
      expect(result.map((item) => item.id)).not.toContain(50);
    });

    it('does not throw when storage rejects the write', () => {
      restore = installThrowingStorage('local');

      expect(() => addLocalView(1, now)).not.toThrow();
    });
  });

  describe('removeLocalViews', () => {
    it('removes only the listed ids', () => {
      restore = installMemoryStorage(
        'local',
        seed([
          { id: 1, at: daysAgo(1) },
          { id: 2, at: daysAgo(2) },
          { id: 3, at: daysAgo(3) },
        ]),
      );

      expect(removeLocalViews([1, 3], now).map((item) => item.id)).toEqual([2]);
      expect(readLocalHistory(now).map((item) => item.id)).toEqual([2]);
    });

    it('ignores ids that are not stored', () => {
      restore = installMemoryStorage('local', seed([{ id: 1, at: daysAgo(1) }]));

      expect(removeLocalViews([99], now).map((item) => item.id)).toEqual([1]);
    });
  });

  describe('clearLocalHistory', () => {
    it('removes everything', () => {
      restore = installMemoryStorage('local', seed([{ id: 1, at: daysAgo(1) }]));

      clearLocalHistory();

      expect(readLocalHistory(now)).toEqual([]);
    });

    it('does not throw when storage rejects the removal', () => {
      restore = installThrowingStorage('local');

      expect(() => clearLocalHistory()).not.toThrow();
    });
  });

  describe('writeLocalHistory', () => {
    it('prunes and caps on the way in', () => {
      restore = installMemoryStorage('local');

      writeLocalHistory(
        [
          { id: 1, at: daysAgo(91) },
          { id: 2, at: daysAgo(1) },
        ],
        now,
      );

      expect(readLocalHistory(now).map((item) => item.id)).toEqual([2]);
    });
  });

  describe('when storage does not exist at all', () => {
    // Server-side rendering has no localStorage, so every entry point has to no-op rather than
    // throw a ReferenceError while the page is being rendered.
    let restoreMissing: () => void;

    beforeEach(() => {
      const original = Object.getOwnPropertyDescriptor(window, 'localStorage');
      Object.defineProperty(window, 'localStorage', { value: undefined, configurable: true });

      restoreMissing = () => {
        if (original) {
          Object.defineProperty(window, 'localStorage', original);
        }
      };

      restore = () => restoreMissing();
    });

    it('reads an empty history', () => {
      expect(readLocalHistory(now)).toEqual([]);
    });

    it('accepts a write without throwing', () => {
      expect(() => writeLocalHistory([{ id: 1, at: daysAgo(1) }], now)).not.toThrow();
      expect(() => addLocalView(1, now)).not.toThrow();
    });

    it('accepts a clear without throwing', () => {
      expect(() => clearLocalHistory()).not.toThrow();
    });

    it('reads as opted in', () => {
      expect(readLocalOptOut()).toBeFalse();
      expect(() => writeLocalOptOut(true)).not.toThrow();
    });
  });

  describe('the default clock', () => {
    it('uses the current time when none is supplied', () => {
      restore = installMemoryStorage('local');

      writeLocalHistory([{ id: 1, at: new Date().toISOString() }]);

      expect(readLocalHistory().map((item) => item.id)).toEqual([1]);
    });

    it('records and removes using the current time', () => {
      restore = installMemoryStorage('local');

      addLocalView(3);
      expect(readLocalHistory().map((item) => item.id)).toEqual([3]);

      removeLocalViews([3]);
      expect(readLocalHistory()).toEqual([]);
    });
  });

  describe('the opt-out flag', () => {
    it('defaults to opted in', () => {
      restore = installMemoryStorage('local');

      expect(readLocalOptOut()).toBeFalse();
    });

    it('round-trips an opt-out', () => {
      restore = installMemoryStorage('local');

      writeLocalOptOut(true);
      expect(readLocalOptOut()).toBeTrue();

      writeLocalOptOut(false);
      expect(readLocalOptOut()).toBeFalse();
    });

    it('clears the key rather than storing false', () => {
      restore = installMemoryStorage('local', { [RecentlyViewedOptOutStorageKey]: 'true' });

      writeLocalOptOut(false);

      expect(localStorage.getItem(RecentlyViewedOptOutStorageKey)).toBeNull();
    });

    it('reads as opted in when storage throws', () => {
      restore = installThrowingStorage('local');

      expect(readLocalOptOut()).toBeFalse();
      expect(() => writeLocalOptOut(true)).not.toThrow();
    });
  });
});
