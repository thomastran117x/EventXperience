import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { environment } from '@environments/environment';
import { provideTestStore } from '@testing';

import { HomeComponent } from './home.component';

describe('HomeComponent', () => {
  const originalFeatureFlags = environment.featureFlags;

  afterEach(() => {
    environment.featureFlags = { ...originalFeatureFlags };
    TestBed.resetTestingModule();
  });

  it('hides auth and event entry points when those features are disabled', async () => {
    environment.featureFlags = {
      auth: false,
      events: false,
    };

    await TestBed.configureTestingModule({
      imports: [HomeComponent],
      providers: [provideRouter([]), ...provideTestStore()],
    }).compileComponents();

    const fixture = TestBed.createComponent(HomeComponent);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;

    expect(element.textContent).not.toContain('Get started');
    expect(element.textContent).not.toContain('Create an account');
    expect(element.textContent).not.toContain('Browse events');
    expect(
      element.querySelector('input[placeholder="Search artists, teams, venues..."]'),
    ).toBeNull();
  });

  it('shows no recently viewed rail until there is a history', async () => {
    environment.featureFlags = { auth: true, events: true, 'events.recentlyviewed': true };

    await TestBed.configureTestingModule({
      imports: [HomeComponent],
      providers: [provideRouter([]), ...provideTestStore()],
    }).compileComponents();

    const fixture = TestBed.createComponent(HomeComponent);
    fixture.detectChanges();

    // The rail renders itself away when empty, so a first-time visitor sees the page unchanged.
    const rail = (fixture.nativeElement as HTMLElement).querySelector('app-recently-viewed-rail');
    expect(rail).not.toBeNull();
    expect(rail!.querySelector('section')).toBeNull();
  });
});
