import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';

import {
  MockStore,
  installMemoryStorage,
  makeEventItem,
  provideFeatureFlags,
  provideTestStore,
} from '@testing';

import { selectUser } from '../../../core/stores/user.selectors';
import { User } from '../../../core/stores/user.model';
import { RecentlyViewedEntry } from '../models/recently-viewed.types';
import { EventsService } from './events.service';
import { RecentlyViewedEventsService } from './recently-viewed-events.service';
import { RecentlyViewedStore } from './recently-viewed-store.service';
import {
  RecentlyViewedStorageKey,
  readLocalHistory,
  readLocalOptOut,
} from './recently-viewed-storage';

describe('RecentlyViewedStore', () => {
  let recentlyViewed: jasmine.SpyObj<RecentlyViewedEventsService>;
  let events: jasmine.SpyObj<EventsService>;
  let store: MockStore;
  let restoreStorage: () => void;

  const signedInUser: User = {
    Id: 7,
    Email: 'user@example.com',
    Username: 'user',
    Usertype: 'Participant',
  };

  function entry(eventId: number, viewedAtUtc = '2026-09-09T12:00:00Z'): RecentlyViewedEntry {
    return { eventId, viewedAtUtc, event: makeEventItem({ id: eventId }) };
  }

  function seedLocal(items: { id: number; at: string }[]): Record<string, string> {
    return { [RecentlyViewedStorageKey]: JSON.stringify({ v: 1, items }) };
  }

  function createStore(
    options: { user?: User | null; enabled?: boolean } = {},
  ): RecentlyViewedStore {
    TestBed.configureTestingModule({
      providers: [
        RecentlyViewedStore,
        { provide: RecentlyViewedEventsService, useValue: recentlyViewed },
        { provide: EventsService, useValue: events },
        ...provideTestStore({ user: options.user ?? null }),
        provideFeatureFlags({
          events: true,
          'events.recentlyviewed': options.enabled ?? true,
        }),
      ],
    });

    store = TestBed.inject(MockStore);
    return TestBed.inject(RecentlyViewedStore);
  }

  function signIn(user: User = signedInUser): void {
    store.overrideSelector(selectUser, user);
    store.refreshState();
  }

  function signOut(): void {
    store.overrideSelector(selectUser, null);
    store.refreshState();
  }

  beforeEach(() => {
    recentlyViewed = jasmine.createSpyObj<RecentlyViewedEventsService>(
      'RecentlyViewedEventsService',
      [
        'recordView',
        'getRecent',
        'remove',
        'removeMany',
        'clear',
        'merge',
        'getSettings',
        'updateSettings',
      ],
    );
    events = jasmine.createSpyObj<EventsService>('EventsService', ['getEventsBatch']);

    recentlyViewed.recordView.and.returnValue(
      of({ eventId: 1, recorded: true, viewedAtUtc: '2026-09-09T12:00:00Z' }),
    );
    recentlyViewed.getRecent.and.returnValue(of([]));
    recentlyViewed.remove.and.returnValue(of(void 0));
    recentlyViewed.removeMany.and.returnValue(of(void 0));
    recentlyViewed.clear.and.returnValue(of(void 0));
    recentlyViewed.merge.and.returnValue(of({ merged: 0, skipped: 0, total: 0 }));
    recentlyViewed.getSettings.and.returnValue(of({ enabled: true, updatedAtUtc: null }));
    recentlyViewed.updateSettings.and.returnValue(of({ enabled: true, updatedAtUtc: null }));
    events.getEventsBatch.and.returnValue(of([]));

    restoreStorage = installMemoryStorage('local');
  });

  afterEach(() => {
    restoreStorage();
    TestBed.resetTestingModule();
  });

  describe('when signed out', () => {
    it('buffers a view in localStorage rather than calling the server', () => {
      events.getEventsBatch.and.returnValue(of([makeEventItem({ id: 4 })]));
      const service = createStore();

      service.recordView(4);

      expect(recentlyViewed.recordView).not.toHaveBeenCalled();
      expect(readLocalHistory().map((item) => item.id)).toEqual([4]);
    });

    it('hydrates the buffered ids through the batch endpoint', () => {
      restoreStorage();
      restoreStorage = installMemoryStorage(
        'local',
        seedLocal([{ id: 4, at: '2026-09-09T12:00:00Z' }]),
      );
      events.getEventsBatch.and.returnValue(of([makeEventItem({ id: 4 })]));
      const service = createStore();

      service.ensureLoaded();

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));

      expect(events.getEventsBatch).toHaveBeenCalledWith([4]);
      expect(entries.map((e) => e.eventId)).toEqual([4]);
      expect(recentlyViewed.getRecent).not.toHaveBeenCalled();
    });

    it('drops buffered ids the batch endpoint will not return', () => {
      restoreStorage();
      restoreStorage = installMemoryStorage(
        'local',
        seedLocal([
          { id: 4, at: '2026-09-09T12:00:00Z' },
          { id: 5, at: '2026-09-08T12:00:00Z' },
        ]),
      );
      // Id 5 has gone private since it was viewed, so the endpoint omits it.
      events.getEventsBatch.and.returnValue(of([makeEventItem({ id: 4 })]));
      const service = createStore();

      service.ensureLoaded();

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries.map((e) => e.eventId)).toEqual([4]);
    });

    it('removes entries from the local buffer', () => {
      restoreStorage();
      restoreStorage = installMemoryStorage(
        'local',
        seedLocal([
          { id: 4, at: '2026-09-09T12:00:00Z' },
          { id: 5, at: '2026-09-08T12:00:00Z' },
        ]),
      );
      const service = createStore();

      service.removeMany([4]).subscribe();

      expect(recentlyViewed.removeMany).not.toHaveBeenCalled();
      expect(readLocalHistory().map((item) => item.id)).toEqual([5]);
    });

    it('clears the local buffer', () => {
      restoreStorage();
      restoreStorage = installMemoryStorage(
        'local',
        seedLocal([{ id: 4, at: '2026-09-09T12:00:00Z' }]),
      );
      const service = createStore();

      service.clear().subscribe();

      expect(recentlyViewed.clear).not.toHaveBeenCalled();
      expect(readLocalHistory()).toEqual([]);
    });

    it('persists the opt-out locally and stops recording', () => {
      const service = createStore();

      service.setEnabled(false).subscribe();
      service.recordView(4);

      expect(readLocalOptOut()).toBeTrue();
      expect(readLocalHistory()).toEqual([]);
      expect(recentlyViewed.updateSettings).not.toHaveBeenCalled();
    });
  });

  describe('when signed in', () => {
    it('loads the history from the server once per session', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1), entry(2)]));
      const service = createStore({ user: signedInUser });

      service.ensureLoaded();
      service.ensureLoaded();

      // Three surfaces share this store, so the visibility fan-out is paid once, not per page.
      expect(recentlyViewed.getRecent).toHaveBeenCalledTimes(1);
    });

    it('posts a view and promotes the entry to the head', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1), entry(2)]));
      recentlyViewed.recordView.and.returnValue(
        of({ eventId: 2, recorded: true, viewedAtUtc: '2026-09-10T12:00:00Z' }),
      );
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      service.recordView(2);

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries.map((e) => e.eventId)).toEqual([2, 1]);
    });

    it('ignores a repeat view within the debounce window', () => {
      const service = createStore({ user: signedInUser });

      service.recordView(4);
      service.recordView(4);

      // The detail page re-fetches on every paramMap emission and on back-navigation.
      expect(recentlyViewed.recordView).toHaveBeenCalledTimes(1);
    });

    it('swallows a failed view write', () => {
      recentlyViewed.recordView.and.returnValue(throwError(() => new Error('offline')));
      const service = createStore({ user: signedInUser });

      // A history that fails to record must never surface an error on the event page.
      expect(() => service.recordView(4)).not.toThrow();
    });

    it('does not promote an entry when the server reports tracking is off', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1), entry(2)]));
      recentlyViewed.recordView.and.returnValue(
        of({ eventId: 2, recorded: false, viewedAtUtc: null }),
      );
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      service.recordView(2);

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries.map((e) => e.eventId)).toEqual([1, 2]);
    });
  });

  describe('deleting', () => {
    it('removes one entry optimistically', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1), entry(2)]));
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      service.remove(1).subscribe();

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries.map((e) => e.eventId)).toEqual([2]);
      expect(recentlyViewed.remove).toHaveBeenCalledWith(1);
    });

    it('sends one request for a whole selection', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1), entry(2), entry(3)]));
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      service.removeMany([1, 3]).subscribe();

      expect(recentlyViewed.removeMany).toHaveBeenCalledOnceWith([1, 3]);
      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries.map((e) => e.eventId)).toEqual([2]);
    });

    it('de-duplicates a selection and ignores an empty one', () => {
      const service = createStore({ user: signedInUser });

      service.removeMany([1, 1]).subscribe();
      expect(recentlyViewed.removeMany).toHaveBeenCalledOnceWith([1]);

      recentlyViewed.removeMany.calls.reset();
      service.removeMany([]).subscribe();
      expect(recentlyViewed.removeMany).not.toHaveBeenCalled();
    });

    it('restores the entries in their original order when a removal fails', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1), entry(2), entry(3)]));
      recentlyViewed.removeMany.and.returnValue(throwError(() => new Error('offline')));
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      service.removeMany([1, 3]).subscribe({ error: () => undefined });

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries.map((e) => e.eventId)).toEqual([1, 2, 3]);
    });

    it('clears everything optimistically', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1), entry(2)]));
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      service.clear().subscribe();

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries).toEqual([]);
      expect(recentlyViewed.clear).toHaveBeenCalled();
    });
  });

  describe('the login merge', () => {
    it('syncs the buffered history and reloads once', () => {
      restoreStorage();
      restoreStorage = installMemoryStorage(
        'local',
        seedLocal([{ id: 4, at: '2026-09-09T12:00:00Z' }]),
      );
      recentlyViewed.getRecent.and.returnValue(of([entry(4)]));
      const service = createStore();

      signIn();

      expect(recentlyViewed.merge).toHaveBeenCalledOnceWith([
        { id: 4, at: '2026-09-09T12:00:00Z' },
      ]);
      // Sequenced behind the merge, so the list cannot land before the sync commits.
      expect(recentlyViewed.getRecent).toHaveBeenCalledTimes(1);

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries.map((e) => e.eventId)).toEqual([4]);
    });

    it('clears the local buffer once the server has it', () => {
      restoreStorage();
      restoreStorage = installMemoryStorage(
        'local',
        seedLocal([{ id: 4, at: '2026-09-09T12:00:00Z' }]),
      );
      createStore();

      signIn();

      expect(readLocalHistory()).toEqual([]);
    });

    it('keeps the buffer for the next sign-in when the merge fails', () => {
      restoreStorage();
      restoreStorage = installMemoryStorage(
        'local',
        seedLocal([{ id: 4, at: '2026-09-09T12:00:00Z' }]),
      );
      recentlyViewed.merge.and.returnValue(throwError(() => new Error('offline')));
      createStore();

      signIn();

      expect(readLocalHistory().map((item) => item.id)).toEqual([4]);
      // The list still loads, so a failed sync does not leave the page blank either.
      expect(recentlyViewed.getRecent).toHaveBeenCalled();
    });

    it('does not merge when there is nothing buffered', () => {
      createStore();

      signIn();

      expect(recentlyViewed.merge).not.toHaveBeenCalled();
      expect(recentlyViewed.getRecent).toHaveBeenCalled();
    });

    it('discards the buffer without syncing when the visitor opted out', () => {
      restoreStorage();
      restoreStorage = installMemoryStorage('local', {
        ...seedLocal([{ id: 4, at: '2026-09-09T12:00:00Z' }]),
        'recently-viewed-opt-out': 'true',
      });
      createStore();

      signIn();

      // Someone who opted out while signed out has not agreed to a server-side history either.
      expect(recentlyViewed.merge).not.toHaveBeenCalled();
      expect(readLocalHistory()).toEqual([]);
    });
  });

  describe('signing out', () => {
    it('empties the list', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1)]));
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      signOut();

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries).toEqual([]);
    });

    it('does not write the account history into localStorage', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1), entry(2)]));
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      signOut();

      // On a shared machine this would hand one user's browsing history to the next visitor.
      expect(readLocalHistory()).toEqual([]);
    });

    it('reloads for a different account without merging', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1)]));
      createStore({ user: signedInUser });

      signIn({ ...signedInUser, Id: 99 });

      expect(recentlyViewed.merge).not.toHaveBeenCalled();
    });
  });

  describe('stale responses', () => {
    it('discards a history that lands after the session changed', () => {
      const pending = new Subject<RecentlyViewedEntry[]>();
      recentlyViewed.getRecent.and.returnValue(pending.asObservable());

      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      signOut();
      pending.next([entry(1)]);

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      // A slow GET for the previous user must not repopulate the screen after a sign-out.
      expect(entries).toEqual([]);
    });
  });

  describe('the opt-out', () => {
    it('empties the list and stops recording when turned off', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1)]));
      recentlyViewed.updateSettings.and.returnValue(of({ enabled: false, updatedAtUtc: null }));
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      service.setEnabled(false).subscribe();
      recentlyViewed.recordView.calls.reset();
      service.recordView(9);

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries).toEqual([]);
      expect(recentlyViewed.recordView).not.toHaveBeenCalled();
      expect(service.optedOut).toBeTrue();
    });

    it('short-circuits ensureLoaded while opted out', () => {
      recentlyViewed.updateSettings.and.returnValue(of({ enabled: false, updatedAtUtc: null }));
      const service = createStore({ user: signedInUser });

      service.setEnabled(false).subscribe();
      recentlyViewed.getRecent.calls.reset();
      service.ensureLoaded();

      expect(recentlyViewed.getRecent).not.toHaveBeenCalled();
    });

    it('reloads the retained history when turned back on', () => {
      recentlyViewed.updateSettings.and.returnValue(of({ enabled: true, updatedAtUtc: null }));
      recentlyViewed.getRecent.and.returnValue(of([entry(1)]));
      const service = createStore({ user: signedInUser });

      service.setEnabled(true).subscribe();

      expect(recentlyViewed.getRecent).toHaveBeenCalled();
    });

    it('reads the stored preference for a signed-in user', () => {
      recentlyViewed.getSettings.and.returnValue(of({ enabled: false, updatedAtUtc: null }));
      const service = createStore({ user: signedInUser });

      let enabled: boolean | null = null;
      service.loadSettings().subscribe((value) => (enabled = value));

      expect(enabled).toBeFalse();
      expect(service.optedOut).toBeTrue();
    });

    it('reads the local preference when signed out', () => {
      restoreStorage();
      restoreStorage = installMemoryStorage('local', { 'recently-viewed-opt-out': 'true' });
      const service = createStore();

      let enabled: boolean | null = null;
      service.loadSettings().subscribe((value) => (enabled = value));

      expect(enabled).toBeFalse();
      expect(recentlyViewed.getSettings).not.toHaveBeenCalled();
    });
  });

  describe('when the feature flag is off', () => {
    it('records nothing and loads nothing', () => {
      const service = createStore({ user: signedInUser, enabled: false });

      service.recordView(4);
      service.ensureLoaded();

      expect(recentlyViewed.recordView).not.toHaveBeenCalled();
      expect(recentlyViewed.getRecent).not.toHaveBeenCalled();
    });
  });

  describe('a first-time view of an event not already in the list', () => {
    it('hydrates the single event and puts it at the head', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1)]));
      recentlyViewed.recordView.and.returnValue(
        of({ eventId: 9, recorded: true, viewedAtUtc: '2026-09-10T12:00:00Z' }),
      );
      events.getEventsBatch.and.returnValue(of([makeEventItem({ id: 9 })]));
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      service.recordView(9);

      // Without this the event is missing from every rail for the rest of the session, because
      // the list is already loaded and nothing refetches it.
      expect(events.getEventsBatch).toHaveBeenCalledWith([9]);
      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries.map((e) => e.eventId)).toEqual([9, 1]);
    });

    it('keeps the list capped at 50', () => {
      const full = Array.from({ length: 50 }, (_, index) => entry(index + 1));
      recentlyViewed.getRecent.and.returnValue(of(full));
      recentlyViewed.recordView.and.returnValue(
        of({ eventId: 999, recorded: true, viewedAtUtc: '2026-09-10T12:00:00Z' }),
      );
      events.getEventsBatch.and.returnValue(of([makeEventItem({ id: 999 })]));
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      service.recordView(999);

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries.length).toBe(50);
      expect(entries[0].eventId).toBe(999);
    });

    it('does not hydrate while the list is still loading', () => {
      // Hold the initial load open so the store has not settled a list yet.
      recentlyViewed.getRecent.and.returnValue(new Subject<RecentlyViewedEntry[]>().asObservable());
      recentlyViewed.recordView.and.returnValue(
        of({ eventId: 9, recorded: true, viewedAtUtc: '2026-09-10T12:00:00Z' }),
      );
      const service = createStore({ user: signedInUser });

      service.recordView(9);

      // The in-flight load will include it anyway, so paying for a second request is waste.
      expect(events.getEventsBatch).not.toHaveBeenCalled();
    });

    it('does not duplicate an entry that arrived while the hydration was in flight', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1)]));
      recentlyViewed.recordView.and.returnValue(
        of({ eventId: 9, recorded: true, viewedAtUtc: '2026-09-10T12:00:00Z' }),
      );
      const pending = new Subject<ReturnType<typeof makeEventItem>[]>();
      events.getEventsBatch.and.returnValue(pending.asObservable());
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      service.recordView(9);
      service.removeMany([1]).subscribe();
      pending.next([makeEventItem({ id: 9 })]);

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries.filter((e) => e.eventId === 9).length).toBe(1);
    });

    it('falls back to refetching the list when the hydration fails', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1)]));
      recentlyViewed.recordView.and.returnValue(
        of({ eventId: 9, recorded: true, viewedAtUtc: '2026-09-10T12:00:00Z' }),
      );
      events.getEventsBatch.and.returnValue(throwError(() => new Error('offline')));
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();
      service.recordView(9);

      recentlyViewed.getRecent.calls.reset();
      service.ensureLoaded();

      expect(recentlyViewed.getRecent).toHaveBeenCalled();
    });
  });

  describe('overlapping local hydrations', () => {
    it('lets only the newest hydration write', () => {
      const first = new Subject<ReturnType<typeof makeEventItem>[]>();
      const second = new Subject<ReturnType<typeof makeEventItem>[]>();
      events.getEventsBatch.and.returnValues(first.asObservable(), second.asObservable());
      const service = createStore();

      service.recordView(4);
      service.recordView(5);

      // The second view resolves first, then the stale first response arrives.
      second.next([makeEventItem({ id: 5 }), makeEventItem({ id: 4 })]);
      first.next([makeEventItem({ id: 4 })]);

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      // The older snapshot must not hide the event the visitor just opened.
      expect(entries.map((e) => e.eventId)).toContain(5);
    });
  });

  describe('failures and stale sessions', () => {
    it('leaves the list empty when the history fails to load', () => {
      recentlyViewed.getRecent.and.returnValue(throwError(() => new Error('offline')));
      const service = createStore({ user: signedInUser });

      service.ensureLoaded();

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      // A failed load leaves the rails empty; nothing else on the page depends on it.
      expect(entries).toEqual([]);
    });

    it('falls back to the local clock when the server omits the timestamp', () => {
      recentlyViewed.getRecent.and.returnValue(of([entry(1), entry(2)]));
      recentlyViewed.recordView.and.returnValue(
        of({ eventId: 2, recorded: true, viewedAtUtc: null }),
      );
      const service = createStore({ user: signedInUser });
      service.ensureLoaded();

      service.recordView(2);

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries[0].eventId).toBe(2);
      expect(entries[0].viewedAtUtc).toBeTruthy();
    });

    it('leaves the local list alone when hydration fails', () => {
      restoreStorage();
      restoreStorage = installMemoryStorage(
        'local',
        seedLocal([{ id: 4, at: '2026-09-09T12:00:00Z' }]),
      );
      events.getEventsBatch.and.returnValue(throwError(() => new Error('offline')));
      const service = createStore();

      service.ensureLoaded();

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries).toEqual([]);
    });

    it('discards a local hydration that lands after the session changed', () => {
      restoreStorage();
      restoreStorage = installMemoryStorage(
        'local',
        seedLocal([{ id: 4, at: '2026-09-09T12:00:00Z' }]),
      );
      const pending = new Subject<ReturnType<typeof makeEventItem>[]>();
      events.getEventsBatch.and.returnValue(pending.asObservable());
      const service = createStore();

      service.ensureLoaded();
      signIn();
      pending.next([makeEventItem({ id: 4 })]);

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries).toEqual([]);
    });

    it('discards a merge that lands after the session changed', () => {
      restoreStorage();
      restoreStorage = installMemoryStorage(
        'local',
        seedLocal([{ id: 4, at: '2026-09-09T12:00:00Z' }]),
      );
      const pending = new Subject<RecentlyViewedEntry[]>();
      recentlyViewed.getRecent.and.returnValue(pending.asObservable());
      const service = createStore();

      signIn();
      signOut();
      pending.next([entry(4)]);

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries).toEqual([]);
    });

    it('restores the toggle when the settings write fails', () => {
      recentlyViewed.updateSettings.and.returnValue(throwError(() => new Error('offline')));
      const service = createStore({ user: signedInUser });

      service.setEnabled(false).subscribe({ error: () => undefined });

      // The toggle goes back where it was; the caller surfaces the failure.
      expect(service.optedOut).toBeFalse();
    });

    it('reloads the local buffer when a signed-out visitor opts back in', () => {
      restoreStorage();
      restoreStorage = installMemoryStorage('local', {
        ...seedLocal([{ id: 4, at: '2026-09-09T12:00:00Z' }]),
        'recently-viewed-opt-out': 'true',
      });
      events.getEventsBatch.and.returnValue(of([makeEventItem({ id: 4 })]));
      const service = createStore();

      service.setEnabled(true).subscribe();

      let entries: RecentlyViewedEntry[] = [];
      service.items$.subscribe((value) => (entries = value));
      expect(entries.map((e) => e.eventId)).toEqual([4]);
      expect(readLocalOptOut()).toBeFalse();
    });
  });

  it('ignores an invalid event id', () => {
    const service = createStore({ user: signedInUser });

    service.recordView(0);

    expect(recentlyViewed.recordView).not.toHaveBeenCalled();
  });
});
