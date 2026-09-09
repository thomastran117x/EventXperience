import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { makeEventItem, provideTestStore } from '@testing';

import { EventFavouritesStore } from '../../services/event-favourites-store.service';
import { RecentlyViewedEntry } from '../../models/recently-viewed.types';
import { RecentlyViewedCardComponent } from './recently-viewed-card.component';

describe('RecentlyViewedCardComponent', () => {
  let fixture: ComponentFixture<RecentlyViewedCardComponent>;
  let component: RecentlyViewedCardComponent;

  const entry: RecentlyViewedEntry = {
    eventId: 7,
    viewedAtUtc: new Date().toISOString(),
    event: makeEventItem({ id: 7, name: 'Robotics Night' }),
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RecentlyViewedCardComponent],
      providers: [
        provideRouter([]),
        ...provideTestStore(),
        {
          provide: EventFavouritesStore,
          useValue: {
            ensureLoaded: jasmine.createSpy('ensureLoaded'),
            isFavourited$: () => of(false),
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(RecentlyViewedCardComponent);
    component = fixture.componentInstance;
    component.entry = entry;
    fixture.detectChanges();
  });

  it('renders the event', () => {
    expect(fixture.nativeElement.textContent).toContain('Robotics Night');
  });

  it('shows no checkbox or remove control by default', () => {
    expect(fixture.nativeElement.querySelector('input[type="checkbox"]')).toBeNull();
    expect(fixture.nativeElement.textContent).not.toContain('Remove');
  });

  it('offers a checkbox in selectable mode', () => {
    component.selectable = true;
    fixture.detectChanges();

    const checkbox: HTMLInputElement =
      fixture.nativeElement.querySelector('input[type="checkbox"]');
    expect(checkbox).not.toBeNull();

    const emitted: boolean[] = [];
    component.selectedChange.subscribe((value) => emitted.push(value));
    checkbox.dispatchEvent(new Event('change'));

    expect(emitted).toEqual([true]);
  });

  it('emits the opposite selection when already selected', () => {
    component.selectable = true;
    component.selected = true;
    fixture.detectChanges();

    const emitted: boolean[] = [];
    component.selectedChange.subscribe((value) => emitted.push(value));
    component.toggleSelected();

    expect(emitted).toEqual([false]);
  });

  it('emits the event id when removed', () => {
    component.showRemove = true;
    fixture.detectChanges();

    const emitted: number[] = [];
    component.removed.subscribe((value) => emitted.push(value));
    fixture.nativeElement.querySelector('button').click();

    expect(emitted).toEqual([7]);
  });

  describe('formatViewedAt', () => {
    it('describes a very recent view', () => {
      expect(component.formatViewedAt(new Date().toISOString())).toBe('Just now');
    });

    it('describes minutes, hours and days', () => {
      const minutesAgo = new Date(Date.now() - 5 * 60_000).toISOString();
      const hoursAgo = new Date(Date.now() - 3 * 3_600_000).toISOString();
      const daysAgo = new Date(Date.now() - 2 * 86_400_000).toISOString();

      expect(component.formatViewedAt(minutesAgo)).toBe('5m ago');
      expect(component.formatViewedAt(hoursAgo)).toBe('3h ago');
      expect(component.formatViewedAt(daysAgo)).toBe('2d ago');
    });

    it('falls back to a date for anything older than a week', () => {
      const longAgo = new Date(Date.now() - 30 * 86_400_000).toISOString();

      expect(component.formatViewedAt(longAgo)).not.toContain('ago');
    });

    it('returns nothing for an unparseable timestamp', () => {
      expect(component.formatViewedAt('not a date')).toBe('');
    });
  });

  describe('formatting helpers', () => {
    it('formats a start date and falls back when it is unparseable', () => {
      expect(component.formatDate('2026-09-09T12:00:00Z')).toBeTruthy();
      expect(component.formatDate('nope')).toBe('Date TBD');
    });

    it('formats cost, calling zero free', () => {
      expect(component.formatCost(0)).toBe('Free');
      expect(component.formatCost(25)).toBe('$25');
    });
  });
});
