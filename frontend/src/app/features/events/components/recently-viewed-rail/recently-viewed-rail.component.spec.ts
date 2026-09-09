import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PLATFORM_ID } from '@angular/core';
import { provideRouter } from '@angular/router';
import { BehaviorSubject, of } from 'rxjs';

import { makeEventItem, provideFeatureFlags, provideTestStore } from '@testing';

import { EventFavouritesStore } from '../../services/event-favourites-store.service';
import { RecentlyViewedEntry } from '../../models/recently-viewed.types';
import { RecentlyViewedStore } from '../../services/recently-viewed-store.service';
import { RecentlyViewedRailComponent } from './recently-viewed-rail.component';

describe('RecentlyViewedRailComponent', () => {
  let items$: BehaviorSubject<RecentlyViewedEntry[]>;
  let recentlyViewedStore: {
    items$: BehaviorSubject<RecentlyViewedEntry[]>;
    ensureLoaded: jasmine.Spy;
  };

  function entry(eventId: number): RecentlyViewedEntry {
    return {
      eventId,
      viewedAtUtc: new Date().toISOString(),
      event: makeEventItem({ id: eventId, name: `Event ${eventId}` }),
    };
  }

  function createFixture(
    options: { enabled?: boolean; platform?: string } = {},
  ): ComponentFixture<RecentlyViewedRailComponent> {
    TestBed.configureTestingModule({
      imports: [RecentlyViewedRailComponent],
      providers: [
        provideRouter([]),
        ...provideTestStore(),
        { provide: RecentlyViewedStore, useValue: recentlyViewedStore },
        {
          provide: EventFavouritesStore,
          useValue: {
            ensureLoaded: jasmine.createSpy('ensureLoaded'),
            isFavourited$: () => of(false),
          },
        },
        provideFeatureFlags({
          events: true,
          'events.recentlyviewed': options.enabled ?? true,
        }),
        { provide: PLATFORM_ID, useValue: options.platform ?? 'browser' },
      ],
    });

    const fixture = TestBed.createComponent(RecentlyViewedRailComponent);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    items$ = new BehaviorSubject<RecentlyViewedEntry[]>([]);
    recentlyViewedStore = { items$, ensureLoaded: jasmine.createSpy('ensureLoaded') };
  });

  afterEach(() => TestBed.resetTestingModule());

  it('renders nothing while the history is empty', () => {
    const fixture = createFixture();

    // A first-time visitor should see the page exactly as it was before the rail existed.
    expect(fixture.nativeElement.querySelector('section')).toBeNull();
  });

  it('renders the entries once there are some', () => {
    items$.next([entry(1), entry(2)]);
    const fixture = createFixture();

    expect(fixture.nativeElement.querySelector('section')).not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Event 1');
  });

  it('shows at most the requested number of entries', () => {
    items$.next([entry(1), entry(2), entry(3), entry(4)]);
    const fixture = createFixture();
    fixture.componentInstance.limit = 2;
    fixture.componentInstance.ngOnInit();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('app-recently-viewed-card').length).toBe(2);
  });

  it('asks the shared store to load rather than fetching for itself', () => {
    createFixture();

    expect(recentlyViewedStore.ensureLoaded).toHaveBeenCalledTimes(1);
  });

  it('does not fetch or render during server-side rendering', () => {
    items$.next([entry(1)]);
    const fixture = createFixture({ platform: 'server' });

    // The history is per-user and, signed out, lives in localStorage, so there is nothing
    // meaningful to paint on the server.
    expect(recentlyViewedStore.ensureLoaded).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('section')).toBeNull();
  });

  it('renders nothing when the feature flag is off', () => {
    items$.next([entry(1)]);
    const fixture = createFixture({ enabled: false });

    expect(recentlyViewedStore.ensureLoaded).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('section')).toBeNull();
  });
});
