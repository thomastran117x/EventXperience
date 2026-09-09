import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { RecentlyViewedStore } from '../../../../../events/services/recently-viewed-store.service';
import { PrivacyTabComponent } from './privacy-tab.component';

describe('PrivacyTabComponent', () => {
  let recentlyViewedStore: jasmine.SpyObj<RecentlyViewedStore>;

  function createFixture(): ComponentFixture<PrivacyTabComponent> {
    TestBed.configureTestingModule({
      imports: [PrivacyTabComponent],
      providers: [
        provideRouter([]),
        { provide: RecentlyViewedStore, useValue: recentlyViewedStore },
      ],
    });

    const fixture = TestBed.createComponent(PrivacyTabComponent);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    recentlyViewedStore = jasmine.createSpyObj<RecentlyViewedStore>('RecentlyViewedStore', [
      'loadSettings',
      'setEnabled',
      'clear',
    ]);

    recentlyViewedStore.loadSettings.and.returnValue(of(true));
    recentlyViewedStore.setEnabled.and.returnValue(of(void 0));
    recentlyViewedStore.clear.and.returnValue(of(void 0));
  });

  afterEach(() => TestBed.resetTestingModule());

  it('loads the stored preference on init', () => {
    recentlyViewedStore.loadSettings.and.returnValue(of(false));
    const component = createFixture().componentInstance;

    expect(recentlyViewedStore.loadSettings).toHaveBeenCalled();
    expect(component.trackingEnabled).toBeFalse();
    expect(component.loading).toBeFalse();
  });

  it('reports a failure to load', () => {
    recentlyViewedStore.loadSettings.and.returnValue(throwError(() => new Error('offline')));
    const component = createFixture().componentInstance;

    expect(component.error).toBeTruthy();
    expect(component.loading).toBeFalse();
  });

  it('says the history is kept when tracking is switched off', () => {
    const component = createFixture().componentInstance;

    component.setTracking(false);

    expect(recentlyViewedStore.setEnabled).toHaveBeenCalledWith(false);
    expect(component.trackingEnabled).toBeFalse();
    // The copy has to be explicit, or switching off reads like a delete.
    expect(component.success).toContain('existing history has been kept');
  });

  it('confirms when tracking is switched on', () => {
    const component = createFixture().componentInstance;

    component.setTracking(true);

    expect(component.success).toContain('on');
  });

  it('reverts the toggle when the update fails', () => {
    recentlyViewedStore.setEnabled.and.returnValue(throwError(() => new Error('offline')));
    const component = createFixture().componentInstance;

    component.setTracking(false);

    expect(component.trackingEnabled).toBeTrue();
    expect(component.error).toBeTruthy();
  });

  it('explains the cap and the retention window', () => {
    const fixture = createFixture();

    expect(fixture.nativeElement.textContent).toContain('50');
    expect(fixture.nativeElement.textContent).toContain('90');
  });

  describe('clearing the history', () => {
    it('asks first and clears when confirmed', () => {
      const component = createFixture().componentInstance;
      spyOn(window, 'confirm').and.returnValue(true);

      component.clearHistory();

      expect(recentlyViewedStore.clear).toHaveBeenCalled();
      expect(component.success).toBeTruthy();
    });

    it('does nothing when the confirmation is dismissed', () => {
      const component = createFixture().componentInstance;
      spyOn(window, 'confirm').and.returnValue(false);

      component.clearHistory();

      expect(recentlyViewedStore.clear).not.toHaveBeenCalled();
    });

    it('reports a failure', () => {
      recentlyViewedStore.clear.and.returnValue(throwError(() => new Error('offline')));
      const component = createFixture().componentInstance;
      spyOn(window, 'confirm').and.returnValue(true);

      component.clearHistory();

      expect(component.error).toBeTruthy();
      expect(component.clearing).toBeFalse();
    });
  });
});
