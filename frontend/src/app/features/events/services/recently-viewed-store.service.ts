import { Injectable } from '@angular/core';
import { Store } from '@ngrx/store';
import {
  BehaviorSubject,
  catchError,
  concatMap,
  map,
  Observable,
  of,
  shareReplay,
  throwError,
} from 'rxjs';

import { UserState } from '../../../core/stores/user.reducer';
import { selectUser } from '../../../core/stores/user.selectors';
import { FeatureFlagsService } from '../../../core/features/feature-flags.service';
import { FEATURE_KEYS } from '../../../core/features/feature-flags.types';
import { EventItem } from '../models/event.types';
import { RecentlyViewedEntry, RecentlyViewedMaxItems } from '../models/recently-viewed.types';
import { EventsService } from './events.service';
import { RecentlyViewedEventsService } from './recently-viewed-events.service';
import {
  addLocalView,
  clearLocalHistory,
  readLocalHistory,
  readLocalOptOut,
  removeLocalViews,
  writeLocalOptOut,
} from './recently-viewed-storage';

/** How long a repeat view of the same event is ignored. */
const RecordDebounceMs = 30_000;

/**
 * The single source of recently-viewed state for every surface that shows it — the dedicated page,
 * the home rail and the search rail. Plain RxJS rather than NgRx, matching how the rest of the
 * feature code holds state; there are no effects anywhere in this app.
 *
 * Being the single fetch point is the whole design. Three surfaces share one `ensureLoaded()`, so
 * the server's per-event visibility check is paid once per session rather than once per page.
 *
 * Unlike the favourites store this holds hydrated events, not just ids: there is no other source a
 * rail could render from, and the list is the feature.
 */
@Injectable({ providedIn: 'root' })
export class RecentlyViewedStore {
  private readonly items = new BehaviorSubject<RecentlyViewedEntry[]>([]);
  private loaded = false;
  private loading = false;
  private currentUserId: number | null = null;
  private optedOutValue = false;
  private mergeInFlight = false;

  /** Last recorded time per event id, so a re-render does not re-POST. */
  private readonly lastRecorded = new Map<number, number>();

  /**
   * Bumped by every reset. An in-flight load carries the generation it started under and discards
   * its response if that no longer matches — otherwise a slow GET issued for the previous user
   * lands after a sign-out and repopulates their history for whoever is looking at the screen.
   */
  private readonly generation$ = new BehaviorSubject<number>(0);

  readonly items$ = this.items.asObservable();

  /**
   * Emits the current session generation, changing whenever the signed-in user does. Pages holding
   * per-user data they already loaded should watch this and drop it — signing out clears the user
   * without navigating, so nothing else tells them to.
   */
  readonly session$ = this.generation$.asObservable();

  private readonly featureEnabled: boolean;

  constructor(
    private recentlyViewed: RecentlyViewedEventsService,
    private events: EventsService,
    private store: Store<{ user: UserState }>,
    features: FeatureFlagsService,
  ) {
    this.featureEnabled = features.isEnabled(FEATURE_KEYS.eventsRecentlyViewed);
    this.optedOutValue = readLocalOptOut();

    this.store.select(selectUser).subscribe((user) => {
      const userId = user?.Id ?? null;
      if (userId === this.currentUserId) {
        return;
      }

      const wasSignedOut = this.currentUserId === null;
      this.currentUserId = userId;
      this.reset();

      if (userId !== null && wasSignedOut) {
        // The one moment the browser-held history can be folded into the account. There is no
        // login-success action or effect in this app, so this transition is the only hook.
        this.mergeLocalHistory();
        return;
      }

      // Signing out must not leave the previous user's history on screen, and must not write it
      // into localStorage either: on a shared machine that would hand it to the next visitor.
    });
  }

  get isSignedIn(): boolean {
    return this.currentUserId !== null;
  }

  /** True while the user has asked not to be tracked, signed in or not. */
  get optedOut(): boolean {
    return this.optedOutValue;
  }

  get sessionGeneration(): number {
    return this.generation$.value;
  }

  isCurrentSession(generation: number): boolean {
    return generation === this.generation$.value;
  }

  /** Loads the history once per session. Safe to call from every surface. */
  ensureLoaded(): void {
    if (!this.featureEnabled || this.loaded || this.loading || this.optedOutValue) {
      return;
    }

    if (this.currentUserId === null) {
      this.loadLocal();
      return;
    }

    const generation = this.sessionGeneration;
    this.loading = true;

    this.recentlyViewed.getRecent().subscribe({
      next: (entries) => {
        if (!this.isCurrentSession(generation)) {
          return;
        }

        this.loading = false;
        this.loaded = true;
        this.items.next(entries);
      },
      error: () => {
        if (!this.isCurrentSession(generation)) {
          return;
        }

        // A failed load leaves the rails empty; nothing else on the page depends on it.
        this.loading = false;
      },
    });
  }

  /**
   * Records that the user opened an event.
   *
   * The detail page re-runs its fetch on every route parameter change and on back-navigation, so
   * repeats are the expected case rather than an anomaly — hence the debounce.
   */
  recordView(eventId: number): void {
    if (!this.featureEnabled || this.optedOutValue || eventId <= 0) {
      return;
    }

    const now = Date.now();
    const last = this.lastRecorded.get(eventId);
    if (last !== undefined && now - last < RecordDebounceMs) {
      return;
    }

    this.lastRecorded.set(eventId, now);

    if (this.currentUserId === null) {
      this.recordLocalView(eventId);
      return;
    }

    const generation = this.sessionGeneration;

    // The write is owned by this store, not by whoever called it: an event page can be left
    // immediately after landing, and if the only subscription belonged to the destroyed view its
    // teardown would cancel the request.
    const request = this.recentlyViewed.recordView(eventId).pipe(
      catchError(() => of(null)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    request.subscribe((result) => {
      if (!this.isCurrentSession(generation) || result === null || !result.recorded) {
        return;
      }

      this.moveToHead(eventId, result.viewedAtUtc ?? new Date(now).toISOString());
    });
  }

  /** Removes one entry, optimistically. */
  remove(eventId: number): Observable<void> {
    return this.applyRemoval([eventId], () => this.recentlyViewed.remove(eventId));
  }

  /** Removes a multi-selected subset as one request, optimistically. */
  removeMany(eventIds: number[]): Observable<void> {
    const ids = [...new Set(eventIds)].filter((id) => id > 0);
    if (ids.length === 0) {
      return of(void 0);
    }

    return this.applyRemoval(ids, () => this.recentlyViewed.removeMany(ids));
  }

  /** Wipes the whole history, optimistically. */
  clear(): Observable<void> {
    const ids = this.items.value.map((entry) => entry.eventId);
    return this.applyRemoval(ids, () => this.recentlyViewed.clear(), true);
  }

  /**
   * Turns tracking on or off. Switching off stops collection and empties the surfaces, but does
   * not delete anything — that is what {@link clear} is for, and the UI says so.
   */
  setEnabled(enabled: boolean): Observable<void> {
    this.optedOutValue = !enabled;
    this.lastRecorded.clear();

    if (!enabled) {
      this.items.next([]);
      this.loaded = false;
    }

    if (this.currentUserId === null) {
      writeLocalOptOut(!enabled);

      if (enabled) {
        this.loadLocal();
      }

      return of(void 0);
    }

    const generation = this.sessionGeneration;

    const request = this.recentlyViewed.updateSettings(enabled).pipe(
      map(() => void 0),
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    request.subscribe({
      next: () => {
        if (this.isCurrentSession(generation) && enabled) {
          // Switching back on restores the retained history.
          this.ensureLoaded();
        }
      },
      error: () => {
        // Put the toggle back where it was; the caller surfaces the failure.
        if (this.isCurrentSession(generation)) {
          this.optedOutValue = enabled;
        }
      },
    });

    return request;
  }

  /** Reads the stored preference for a signed-in user, so the settings tab opens in sync. */
  loadSettings(): Observable<boolean> {
    if (this.currentUserId === null) {
      this.optedOutValue = readLocalOptOut();
      return of(!this.optedOutValue);
    }

    return this.recentlyViewed.getSettings().pipe(
      map((settings) => {
        this.optedOutValue = !settings.enabled;
        return settings.enabled;
      }),
    );
  }

  /** Drops the cached history so the next `ensureLoaded` refetches. */
  reset(): void {
    this.loaded = false;
    this.loading = false;
    this.lastRecorded.clear();
    this.items.next([]);
    this.generation$.next(this.generation$.value + 1);
  }

  /**
   * Applies a deletion optimistically and puts the entries back, in their original positions, if
   * the write fails. Single and batch removal share this so the two cannot drift apart.
   */
  private applyRemoval(
    eventIds: number[],
    write: () => Observable<void>,
    clearLocalAll = false,
  ): Observable<void> {
    const generation = this.sessionGeneration;
    const previous = this.items.value;
    const doomed = new Set(eventIds);

    this.items.next(previous.filter((entry) => !doomed.has(entry.eventId)));

    if (this.currentUserId === null) {
      if (clearLocalAll) {
        clearLocalHistory();
      } else {
        removeLocalViews(eventIds);
      }

      return of(void 0);
    }

    const request = write().pipe(
      catchError((error: unknown) => {
        if (this.isCurrentSession(generation)) {
          this.items.next(previous);
        }

        return throwError(() => error);
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    // Owned here so a delete pressed just before navigating away still reaches the server.
    request.subscribe({ error: () => undefined });

    return request;
  }

  /** Hydrates the locally-held ids into events through the batch endpoint. */
  private loadLocal(): void {
    const local = readLocalHistory();
    if (local.length === 0) {
      this.items.next([]);
      this.loaded = true;
      return;
    }

    const generation = this.sessionGeneration;
    this.loading = true;

    this.events.getEventsBatch(local.map((item) => item.id)).subscribe({
      next: (events) => {
        if (!this.isCurrentSession(generation)) {
          return;
        }

        this.loading = false;
        this.loaded = true;
        this.items.next(this.toEntries(local, events));
      },
      error: () => {
        if (!this.isCurrentSession(generation)) {
          return;
        }

        this.loading = false;
      },
    });
  }

  private recordLocalView(eventId: number): void {
    const local = addLocalView(eventId);
    const generation = this.sessionGeneration;

    this.events.getEventsBatch(local.map((item) => item.id)).subscribe({
      next: (events) => {
        if (!this.isCurrentSession(generation)) {
          return;
        }

        this.items.next(this.toEntries(local, events));
      },
      error: () => undefined,
    });
  }

  /**
   * Syncs the browser-held history up at login, then reloads.
   *
   * Sequenced rather than run in parallel: a list fetched before the merge commits would not show
   * what was just synced, and the user would be looking at a history that is missing the very
   * events they browsed on their way to signing in.
   */
  private mergeLocalHistory(): void {
    if (!this.featureEnabled || this.mergeInFlight) {
      return;
    }

    if (this.optedOutValue) {
      // Someone who opted out while signed out has not agreed to a server-side history either.
      clearLocalHistory();
      return;
    }

    const local = readLocalHistory();
    if (local.length === 0) {
      this.ensureLoaded();
      return;
    }

    const generation = this.sessionGeneration;
    this.mergeInFlight = true;
    this.loading = true;

    this.recentlyViewed
      .merge(local)
      .pipe(
        map(() => true),
        catchError(() => of(false)),
        concatMap((merged) =>
          this.recentlyViewed.getRecent().pipe(map((entries) => ({ merged, entries }))),
        ),
        catchError(() => of({ merged: false, entries: null as RecentlyViewedEntry[] | null })),
      )
      .subscribe(({ merged, entries }) => {
        this.mergeInFlight = false;

        if (!this.isCurrentSession(generation)) {
          return;
        }

        this.loading = false;

        if (merged) {
          // Only once the server has it. A failed merge keeps the buffer for the next sign-in.
          clearLocalHistory();
        }

        if (entries !== null) {
          this.loaded = true;
          this.items.next(entries);
        }
      });
  }

  /** Pairs locally-held ids with hydrated events, dropping ids the server would not return. */
  private toEntries(
    local: { id: number; at: string }[],
    events: EventItem[],
  ): RecentlyViewedEntry[] {
    const byId = new Map(events.map((event) => [event.id, event]));

    return local
      .filter((item) => byId.has(item.id))
      .map((item) => ({
        eventId: item.id,
        viewedAtUtc: item.at,
        event: byId.get(item.id)!,
      }))
      .slice(0, RecentlyViewedMaxItems);
  }

  private moveToHead(eventId: number, viewedAtUtc: string): void {
    const existing = this.items.value.find((entry) => entry.eventId === eventId);
    if (!existing) {
      // Nothing to promote: the entry arrives on the next load, with its event attached.
      return;
    }

    this.items.next([
      { ...existing, viewedAtUtc },
      ...this.items.value.filter((entry) => entry.eventId !== eventId),
    ]);
  }
}
