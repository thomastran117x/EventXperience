import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BehaviorSubject, of, throwError } from 'rxjs';

import { makeEventItem, provideFeatureFlags, provideTestStore } from '@testing';

import { EventFavouritesStore } from '../../services/event-favourites-store.service';
import { RecentlyViewedEntry } from '../../models/recently-viewed.types';
import { RecentlyViewedStore } from '../../services/recently-viewed-store.service';
import { MyRecentComponent } from './my-recent.component';

describe('MyRecentComponent', () => {
  let items$: BehaviorSubject<RecentlyViewedEntry[]>;
  let session$: BehaviorSubject<number>;
  let recentlyViewedStore: jasmine.SpyObj<RecentlyViewedStore> & {
    items$: BehaviorSubject<RecentlyViewedEntry[]>;
    session$: BehaviorSubject<number>;
    isSignedIn: boolean;
    optedOut: boolean;
  };

  function entry(eventId: number): RecentlyViewedEntry {
    return {
      eventId,
      viewedAtUtc: new Date().toISOString(),
      event: makeEventItem({ id: eventId, name: `Event ${eventId}` }),
    };
  }

  function createFixture(): ComponentFixture<MyRecentComponent> {
    TestBed.configureTestingModule({
      imports: [MyRecentComponent],
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
        provideFeatureFlags({ events: true, 'events.recentlyviewed': true }),
      ],
    });

    const fixture = TestBed.createComponent(MyRecentComponent);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    items$ = new BehaviorSubject<RecentlyViewedEntry[]>([]);
    session$ = new BehaviorSubject<number>(0);

    recentlyViewedStore = jasmine.createSpyObj<RecentlyViewedStore>(
      'RecentlyViewedStore',
      ['ensureLoaded', 'remove', 'removeMany', 'clear', 'setEnabled'],
      { isSignedIn: true, optedOut: false },
    ) as typeof recentlyViewedStore;

    recentlyViewedStore.items$ = items$;
    recentlyViewedStore.session$ = session$;
    recentlyViewedStore.remove.and.returnValue(of(void 0));
    recentlyViewedStore.removeMany.and.returnValue(of(void 0));
    recentlyViewedStore.clear.and.returnValue(of(void 0));
    recentlyViewedStore.setEnabled.and.returnValue(of(void 0));
  });

  afterEach(() => TestBed.resetTestingModule());

  it('loads the history on init', () => {
    createFixture();

    expect(recentlyViewedStore.ensureLoaded).toHaveBeenCalled();
  });

  it('shows the empty state when there is no history', () => {
    const fixture = createFixture();

    expect(fixture.nativeElement.textContent).toContain('Nothing here yet');
  });

  it('renders the entries', () => {
    items$.next([entry(1), entry(2)]);
    const fixture = createFixture();

    expect(fixture.nativeElement.textContent).toContain('Event 1');
    expect(fixture.componentInstance.entries.length).toBe(2);
  });

  it('removes one entry', () => {
    items$.next([entry(1)]);
    const fixture = createFixture();

    fixture.componentInstance.removeOne(1);

    expect(recentlyViewedStore.remove).toHaveBeenCalledWith(1);
  });

  describe('select mode', () => {
    it('is off until entered', () => {
      items$.next([entry(1)]);
      const fixture = createFixture();

      expect(fixture.componentInstance.selectMode).toBeFalse();
    });

    it('tracks a selection and sends one request for it', () => {
      items$.next([entry(1), entry(2), entry(3)]);
      const fixture = createFixture();
      const component = fixture.componentInstance;

      component.toggleSelectMode();
      component.setSelected(1, true);
      component.setSelected(3, true);

      expect(component.selectedCount).toBe(2);
      expect(component.isSelected(1)).toBeTrue();
      expect(component.isSelected(2)).toBeFalse();

      component.removeSelected();

      // One request for the whole selection, not one per entry.
      expect(recentlyViewedStore.removeMany).toHaveBeenCalledOnceWith([1, 3]);
    });

    it('unticks an entry', () => {
      items$.next([entry(1)]);
      const component = createFixture().componentInstance;

      component.setSelected(1, true);
      component.setSelected(1, false);

      expect(component.selectedCount).toBe(0);
    });

    it('selects and deselects everything', () => {
      items$.next([entry(1), entry(2)]);
      const component = createFixture().componentInstance;

      component.toggleSelectAll();
      expect(component.allSelected).toBeTrue();

      component.toggleSelectAll();
      expect(component.selectedCount).toBe(0);
    });

    it('does nothing when removing an empty selection', () => {
      items$.next([entry(1)]);
      const component = createFixture().componentInstance;

      component.removeSelected();

      expect(recentlyViewedStore.removeMany).not.toHaveBeenCalled();
    });

    it('clears the selection on leaving select mode', () => {
      items$.next([entry(1)]);
      const component = createFixture().componentInstance;

      component.toggleSelectMode();
      component.setSelected(1, true);
      component.toggleSelectMode();

      expect(component.selectedCount).toBe(0);
    });

    it('clears the selection when the list changes from elsewhere', () => {
      items$.next([entry(1), entry(2)]);
      const component = createFixture().componentInstance;

      component.toggleSelectMode();
      component.setSelected(1, true);

      // A background refresh can retire ids that are still ticked.
      items$.next([entry(2)]);

      expect(component.selectedCount).toBe(0);
    });

    it('keeps the removal flowing without the selection surviving it', () => {
      items$.next([entry(1), entry(2)]);
      const component = createFixture().componentInstance;

      component.toggleSelectMode();
      component.setSelected(1, true);
      component.removeSelected();

      expect(recentlyViewedStore.removeMany).toHaveBeenCalledOnceWith([1]);
      expect(component.selectedCount).toBe(0);
    });
  });

  describe('clearing everything', () => {
    it('asks first and clears when confirmed', () => {
      items$.next([entry(1)]);
      const component = createFixture().componentInstance;
      spyOn(window, 'confirm').and.returnValue(true);

      component.clearAll();

      expect(window.confirm).toHaveBeenCalled();
      expect(recentlyViewedStore.clear).toHaveBeenCalled();
    });

    it('does nothing when the confirmation is dismissed', () => {
      items$.next([entry(1)]);
      const component = createFixture().componentInstance;
      spyOn(window, 'confirm').and.returnValue(false);

      component.clearAll();

      expect(recentlyViewedStore.clear).not.toHaveBeenCalled();
    });

    it('does nothing when there is nothing to clear', () => {
      const component = createFixture().componentInstance;
      spyOn(window, 'confirm').and.returnValue(true);

      component.clearAll();

      expect(window.confirm).not.toHaveBeenCalled();
      expect(recentlyViewedStore.clear).not.toHaveBeenCalled();
    });

    it('surfaces a failure', () => {
      items$.next([entry(1)]);
      recentlyViewedStore.clear.and.returnValue(throwError(() => new Error('offline')));
      const component = createFixture().componentInstance;
      spyOn(window, 'confirm').and.returnValue(true);

      component.clearAll();

      expect(component.error).toBeTruthy();
    });
  });

  describe('the anonymous opt-out', () => {
    it('is offered only while signed out', () => {
      items$.next([entry(1)]);
      const signedIn = createFixture();
      expect(signedIn.nativeElement.querySelector('app-toggle-switch')).toBeNull();

      TestBed.resetTestingModule();
      Object.defineProperty(recentlyViewedStore, 'isSignedIn', {
        value: false,
        configurable: true,
      });
      const signedOut = createFixture();

      // Signed-out visitors cannot reach /account, so the toggle has to live here for them.
      expect(signedOut.nativeElement.querySelector('app-toggle-switch')).not.toBeNull();
    });

    it('reverts and reports when the update fails', () => {
      recentlyViewedStore.setEnabled.and.returnValue(throwError(() => new Error('offline')));
      const component = createFixture().componentInstance;

      component.setTracking(false);

      expect(component.trackingEnabled).toBeTrue();
      expect(component.error).toBeTruthy();
    });

    it('shows a dedicated message while tracking is off', () => {
      Object.defineProperty(recentlyViewedStore, 'optedOut', { value: true, configurable: true });
      const fixture = createFixture();

      expect(fixture.nativeElement.textContent).toContain('View tracking is off');
    });
  });

  it('surfaces a favourite failure', () => {
    const component = createFixture().componentInstance;

    component.onFavouriteFailed(new Error('nope'));

    expect(component.error).toBeTruthy();
  });
});
