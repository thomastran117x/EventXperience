import { ActivatedRoute, convertToParamMap, ParamMap, Router } from '@angular/router';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BehaviorSubject, of, throwError } from 'rxjs';
import { Store } from '@ngrx/store';

import { EventDetailComponent } from './event-detail.component';
import { EventsService } from '../../services/events.service';
import { EventApiResponse } from '../../models/event.types';
import { EventRegistrationService } from '../../services/event-registration.service';
import { EventWaitlistService } from '../../services/event-waitlist.service';
import { EventFavouritesStore } from '../../services/event-favourites-store.service';
import { RecentlyViewedStore } from '../../services/recently-viewed-store.service';
import { FeatureFlagsService } from '../../../../core/features/feature-flags.service';
import {
  ApiClientClientError,
  ApiClientServerError,
  GENERIC_API_ERROR_MESSAGE,
} from '../../../../core/api/models/api-client-error.model';

class ActivatedRouteStub {
  private readonly paramSubject = new BehaviorSubject<ParamMap>(
    convertToParamMap({ eventId: '42' }),
  );
  private readonly querySubject = new BehaviorSubject<ParamMap>(
    convertToParamMap({ search: 'hack' }),
  );

  readonly paramMap = this.paramSubject.asObservable();
  readonly queryParamMap = this.querySubject.asObservable();
  snapshot = {
    queryParams: { search: 'hack' } as Record<string, string>,
  };

  setParamMap(params: Record<string, string>) {
    this.paramSubject.next(convertToParamMap(params));
  }

  setQueryParamMap(params: Record<string, string>) {
    this.snapshot.queryParams = params;
    this.querySubject.next(convertToParamMap(params));
  }
}

describe('EventDetailComponent', () => {
  let fixture: ComponentFixture<EventDetailComponent>;
  let component: EventDetailComponent;
  let route: ActivatedRouteStub;
  let eventsService: jasmine.SpyObj<EventsService>;
  let registrationService: jasmine.SpyObj<EventRegistrationService>;
  let waitlistService: jasmine.SpyObj<EventWaitlistService>;
  let router: jasmine.SpyObj<Router>;
  let favouritesStore: jasmine.SpyObj<EventFavouritesStore>;
  let recentlyViewedStore: jasmine.SpyObj<RecentlyViewedStore>;
  let favourited$: BehaviorSubject<boolean>;
  // Most specs here predate auth-aware behaviour and assumed a signed-out user; the
  // waitlist states need a signed-in one, so it is overridable per test.
  let signedInUser: { Id: number } | null = null;
  let waitlistFeatureEnabled = true;

  const response: EventApiResponse = {
    success: true,
    message: 'ok',
    data: {
      id: 42,
      name: 'Hack Night',
      description: 'Build things together',
      location: 'Student Center',
      imageUrls: ['https://example.com/poster.png'],
      isPrivate: false,
      maxParticipants: 120,
      registerCost: 0,
      startTime: '2026-12-20T18:00:00Z',
      endTime: '2026-12-20T21:00:00Z',
      clubId: 7,
      createdAt: '2026-05-01T12:00:00Z',
      lifecycleState: 'Published',
      status: 'Upcoming',
      category: 'Workshop',
      venueName: 'Main Hall',
      city: 'Ottawa',
      latitude: 45.4215,
      longitude: -75.6972,
      tags: ['tech', 'community'],
      registrationCount: 34,
      waitlistEnabled: false,
      waitlistCount: 0,
      distanceKm: undefined,
      club: {
        id: 7,
        name: 'uOttaHack',
        description: 'Hackathons and builder meetups',
        clubType: 'Academic',
        clubImage: 'https://example.com/club.png',
        memberCount: 240,
        eventCount: 18,
        availableEventCount: 3,
        isPrivate: false,
        email: 'hello@uottahack.ca',
        phone: '555-0101',
        rating: 4.8,
        websiteUrl: 'https://uottahack.ca',
        location: 'Ottawa',
      },
    },
    error: null,
    meta: null,
  };

  beforeEach(async () => {
    route = new ActivatedRouteStub();
    eventsService = jasmine.createSpyObj<EventsService>('EventsService', ['getEvent']);
    registrationService = jasmine.createSpyObj<EventRegistrationService>(
      'EventRegistrationService',
      ['register', 'updateRegistration', 'unregister', 'checkRegistration'],
    );
    router = jasmine.createSpyObj<Router>('Router', ['navigate']);
    router.navigate.and.resolveTo(true);
    eventsService.getEvent.and.returnValue(of(response));
    registrationService.checkRegistration.and.returnValue(
      of({ isRegistered: false, details: null }),
    );
    waitlistService = jasmine.createSpyObj<EventWaitlistService>('EventWaitlistService', [
      'join',
      'leave',
      'getMyStatus',
      'getEventWaitlist',
      'removeEntry',
      'promoteNext',
      'getMine',
    ]);
    waitlistService.getMyStatus.and.returnValue(of({ onWaitlist: false, waitlistCount: 0 }));

    favourited$ = new BehaviorSubject(false);
    favouritesStore = jasmine.createSpyObj<EventFavouritesStore>(
      'EventFavouritesStore',
      ['ensureLoaded', 'toggle', 'isFavourited$'],
      { isSignedIn: true },
    );
    recentlyViewedStore = jasmine.createSpyObj<RecentlyViewedStore>('RecentlyViewedStore', [
      'recordView',
    ]);
    favouritesStore.isFavourited$.and.returnValue(favourited$.asObservable());
    favouritesStore.toggle.and.returnValue(of(true));

    await TestBed.configureTestingModule({
      imports: [EventDetailComponent],
      providers: [
        { provide: ActivatedRoute, useValue: route },
        { provide: EventsService, useValue: eventsService },
        { provide: EventRegistrationService, useValue: registrationService },
        { provide: EventWaitlistService, useValue: waitlistService },
        {
          provide: FeatureFlagsService,
          useValue: { isEnabled: () => waitlistFeatureEnabled },
        },
        { provide: Router, useValue: router },
        { provide: Store, useValue: { select: () => of(signedInUser) } },
        { provide: EventFavouritesStore, useValue: favouritesStore },
        { provide: RecentlyViewedStore, useValue: recentlyViewedStore },
      ],
    }).compileComponents();
  });

  function createComponent() {
    fixture = TestBed.createComponent(EventDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  /** Rebuilds the event payload as a full, waitlist-enabled event. */
  function makeFullWaitlistEvent(overrides: Record<string, unknown> = {}) {
    eventsService.getEvent.and.returnValue(
      of({
        ...response,
        data: {
          ...response.data!,
          maxParticipants: 10,
          registrationCount: 10,
          waitlistEnabled: true,
          waitlistCount: 2,
          ...overrides,
        },
      }),
    );
  }

  describe('waitlist', () => {
    beforeEach(() => {
      signedInUser = { Id: 7 };
      waitlistFeatureEnabled = true;
    });
    afterEach(() => {
      signedInUser = null;
      waitlistFeatureEnabled = true;
    });

    it('offers the waitlist when the event is full and has one enabled', () => {
      makeFullWaitlistEvent();
      createComponent();

      expect(component.isFull).toBeTrue();
      expect(component.waitlistOffered).toBeTrue();
      expect(component.canJoinWaitlist).toBeTrue();
      // The waitlist is an additional path, not a relaxation of the capacity rule.
      expect(component.canRegister).toBeFalse();
    });

    it('does not offer the waitlist when the global feature flag is disabled', () => {
      // The backend controller is feature-gated, so the CTA must not appear for an event that
      // stored waitlistEnabled=true before the flag was turned off.
      waitlistFeatureEnabled = false;
      makeFullWaitlistEvent();
      createComponent();

      expect(component.waitlistOffered).toBeFalse();
      expect(component.canJoinWaitlist).toBeFalse();
    });

    it('does not offer the waitlist when the event is full but the waitlist is off', () => {
      makeFullWaitlistEvent({ waitlistEnabled: false, waitlistCount: 0 });
      createComponent();

      expect(component.isFull).toBeTrue();
      expect(component.waitlistOffered).toBeFalse();
      expect(component.canJoinWaitlist).toBeFalse();
    });

    it('does not offer the waitlist when seats remain', () => {
      makeFullWaitlistEvent({ registrationCount: 3 });
      createComponent();

      expect(component.waitlistOffered).toBeFalse();
      expect(component.canRegister).toBeTrue();
    });

    it('does not offer the waitlist for paid events', () => {
      makeFullWaitlistEvent({ registerCost: 2500 });
      createComponent();

      expect(component.waitlistOffered).toBeFalse();
    });

    it('skips the status lookup when the event has no waitlist', () => {
      makeFullWaitlistEvent({ waitlistEnabled: false });
      createComponent();

      expect(waitlistService.getMyStatus).not.toHaveBeenCalled();
    });

    it('optimistically records the position after joining', () => {
      makeFullWaitlistEvent();
      waitlistService.join.and.returnValue(
        of({
          id: 5,
          eventId: 42,
          userId: 7,
          position: 3,
          status: 'Waiting' as const,
          joinedAtUtc: 'now',
        }),
      );
      createComponent();

      component.joinWaitlist();

      expect(component.onWaitlist).toBeTrue();
      expect(component.waitlistStatus?.position).toBe(3);
      expect(component.event?.waitlistCount).toBe(3);
      expect(component.canJoinWaitlist).toBeFalse();
    });

    it('surfaces a join failure as an inline banner message', () => {
      makeFullWaitlistEvent();
      waitlistService.join.and.returnValue(
        throwError(() => new ApiClientClientError('Seats are still available', 409)),
      );
      createComponent();

      component.joinWaitlist();

      expect(component.waitlistError).toBe('Seats are still available');
      expect(component.onWaitlist).toBeFalse();
    });

    it('refetches the status after leaving, because positions shift for everyone', () => {
      makeFullWaitlistEvent();
      waitlistService.getMyStatus.and.returnValue(
        of({ onWaitlist: true, position: 2, waitlistCount: 2 }),
      );
      waitlistService.leave.and.returnValue(of(void 0));
      createComponent();
      waitlistService.getMyStatus.calls.reset();

      component.leaveWaitlist();

      expect(waitlistService.leave).toHaveBeenCalledWith(42);
      expect(waitlistService.getMyStatus).toHaveBeenCalledWith(42);
    });

    it('refetches the event after unregistering so an instant promotion is reflected', () => {
      makeFullWaitlistEvent();
      registrationService.unregister.and.returnValue(of(void 0));
      createComponent();
      eventsService.getEvent.calls.reset();

      component.unregister();

      expect(eventsService.getEvent).toHaveBeenCalledWith(42);
    });
  });

  it('loads the event from the route id', () => {
    createComponent();

    expect(eventsService.getEvent).toHaveBeenCalledWith(42);
    expect(component.event?.id).toBe(42);
    expect(component.event?.club?.name).toBe('uOttaHack');
    expect(component.loading).toBeFalse();
    expect(component.error).toBe('');
  });

  it('navigates back to the search route with the preserved query params', () => {
    createComponent();

    component.goBack();

    expect(router.navigate).toHaveBeenCalledWith(['/events'], {
      queryParams: { search: 'hack' },
    });
  });

  it('shows an error for invalid route ids', () => {
    route.setParamMap({ eventId: 'abc' });

    createComponent();

    expect(eventsService.getEvent).not.toHaveBeenCalled();
    expect(component.error).toBe('Invalid event ID.');
    expect(component.loading).toBeFalse();
  });

  it('surfaces 4xx event fetch failures from the adapter', () => {
    eventsService.getEvent.and.returnValue(
      throwError(() => new ApiClientClientError('Not found.', 404, 'RESOURCE_NOT_FOUND')),
    );

    createComponent();

    expect(component.event).toBeNull();
    expect(component.error).toBe('Not found.');
    expect(component.loading).toBeFalse();
  });

  describe('registration', () => {
    beforeEach(() => {
      signedInUser = { Id: 7 };
    });
    afterEach(() => {
      signedInUser = null;
    });

    it('opens a blank form for a new registration', () => {
      createComponent();

      component.openRegistrationForm();

      expect(component.showRegistrationForm).toBeTrue();
      expect(component.isEditing).toBeFalse();
      expect(component.registrationForm.value.notes).toBeFalsy();
    });

    it('pre-fills the form when editing an existing registration', () => {
      registrationService.checkRegistration.and.returnValue(
        of({
          isRegistered: true,
          details: { notes: 'Vegan', phoneNumber: '555', dietaryNeeds: 'None' },
        }),
      );
      createComponent();

      component.openEditForm();

      expect(component.isEditing).toBeTrue();
      expect(component.registrationForm.value).toEqual(
        jasmine.objectContaining({ notes: 'Vegan', phoneNumber: '555', dietaryNeeds: 'None' }),
      );
    });

    it('blanks the edit form when there are no stored details', () => {
      createComponent();

      component.openEditForm();

      expect(component.registrationForm.value).toEqual(
        jasmine.objectContaining({ notes: '', phoneNumber: '', dietaryNeeds: '' }),
      );
    });

    it('closes the form', () => {
      createComponent();
      component.openRegistrationForm();

      component.closeRegistrationForm();

      expect(component.showRegistrationForm).toBeFalse();
    });

    it('registers, closes the form and bumps the count', () => {
      registrationService.register.and.returnValue(of(undefined as void));
      createComponent();
      component.openRegistrationForm();
      component.registrationForm.patchValue({ notes: 'Bringing a laptop' });

      component.submitRegistration();

      expect(registrationService.register).toHaveBeenCalledWith(42, {
        notes: 'Bringing a laptop',
        phoneNumber: undefined,
        dietaryNeeds: undefined,
      });
      expect(component.isRegistered).toBeTrue();
      expect(component.showRegistrationForm).toBeFalse();
      expect(component.event?.registrationCount).toBe(35);
      expect(component.registrationLoading).toBeFalse();
    });

    it('reports a failed registration and keeps the form open', () => {
      registrationService.register.and.returnValue(
        throwError(() => new ApiClientClientError('That event is full.', 409, 'CONFLICT')),
      );
      createComponent();
      component.openRegistrationForm();

      component.submitRegistration();

      expect(component.registrationError).toBe('That event is full.');
      expect(component.showRegistrationForm).toBeTrue();
      expect(component.isRegistered).toBeFalse();
      expect(component.registrationLoading).toBeFalse();
    });

    it('updates the stored details without changing the count', () => {
      registrationService.updateRegistration.and.returnValue(of(undefined as void));
      createComponent();
      component.openEditForm();
      component.registrationForm.patchValue({ dietaryNeeds: 'Gluten free' });

      component.submitUpdate();

      expect(registrationService.updateRegistration).toHaveBeenCalledWith(42, {
        notes: undefined,
        phoneNumber: undefined,
        dietaryNeeds: 'Gluten free',
      });
      expect(component.showRegistrationForm).toBeFalse();
      expect(component.event?.registrationCount).toBe(34);
    });

    it('reports a failed update', () => {
      registrationService.updateRegistration.and.returnValue(
        throwError(() => new ApiClientServerError(GENERIC_API_ERROR_MESSAGE, 500)),
      );
      createComponent();
      component.openEditForm();

      component.submitUpdate();

      expect(component.registrationError).toBe(GENERIC_API_ERROR_MESSAGE);
      expect(component.showRegistrationForm).toBeTrue();
    });

    it('unregisters and decrements the count', () => {
      registrationService.unregister.and.returnValue(of(undefined as void));
      createComponent();
      component.isRegistered = true;

      component.unregister();

      expect(component.isRegistered).toBeFalse();
      expect(component.registrationDetails).toBeNull();
      expect(component.event?.registrationCount).toBe(33);
    });

    it('never drives the registration count below zero', () => {
      eventsService.getEvent.and.returnValue(
        of({ ...response, data: { ...response.data!, registrationCount: 0 } }),
      );
      registrationService.unregister.and.returnValue(of(undefined as void));
      createComponent();

      component.unregister();

      expect(component.event?.registrationCount).toBe(0);
    });

    it('reports a failed unregistration', () => {
      registrationService.unregister.and.returnValue(
        throwError(() => new ApiClientClientError('Too late to withdraw.', 400, 'BAD')),
      );
      createComponent();

      component.unregister();

      expect(component.registrationError).toBe('Too late to withdraw.');
      expect(component.registrationLoading).toBeFalse();
    });

    it('ignores repeat submissions while one is in flight', () => {
      registrationService.register.and.returnValue(of(undefined as void));
      createComponent();
      component.registrationLoading = true;

      component.submitRegistration();
      component.submitUpdate();
      component.unregister();

      expect(registrationService.register).not.toHaveBeenCalled();
      expect(registrationService.updateRegistration).not.toHaveBeenCalled();
      expect(registrationService.unregister).not.toHaveBeenCalled();
    });

    it('adopts the stored details when the status lookup reports a registration', () => {
      registrationService.checkRegistration.and.returnValue(
        of({ isRegistered: true, details: { notes: 'Existing' } }),
      );

      createComponent();

      expect(component.isRegistered).toBeTrue();
      expect(component.registrationDetails).toEqual({ notes: 'Existing' });
    });

    it('treats a failed status lookup as not registered', () => {
      registrationService.checkRegistration.and.returnValue(throwError(() => new Error('offline')));

      createComponent();

      expect(component.isRegistered).toBeFalse();
    });
  });

  describe('signed-out visitors', () => {
    it('skips the registration and waitlist lookups entirely', () => {
      createComponent();

      expect(component.currentUserId).toBeNull();
      expect(registrationService.checkRegistration).not.toHaveBeenCalled();
      expect(waitlistService.getMyStatus).not.toHaveBeenCalled();
      expect(component.isRegistered).toBeFalse();
      expect(component.waitlistStatus).toBeNull();
    });

    it('cannot join a waitlist even when one is offered', () => {
      makeFullWaitlistEvent();
      createComponent();

      expect(component.waitlistOffered).toBeTrue();
      expect(component.canJoinWaitlist).toBeFalse();
    });
  });

  describe('registration eligibility', () => {
    function withEvent(overrides: Record<string, unknown>) {
      eventsService.getEvent.and.returnValue(
        of({ ...response, data: { ...response.data!, ...overrides } }),
      );
      createComponent();
    }

    it('allows registering for a free, published, future event with seats', () => {
      createComponent();

      expect(component.canRegister).toBeTrue();
      expect(component.isFull).toBeFalse();
    });

    it('refuses before the event has loaded', () => {
      eventsService.getEvent.and.returnValue(of({ ...response, data: null }));
      createComponent();

      expect(component.canRegister).toBeFalse();
      expect(component.isFull).toBeFalse();
      expect(component.waitlistOffered).toBeFalse();
    });

    it('refuses for an unpublished event', () => {
      withEvent({ lifecycleState: 'Draft' });

      expect(component.canRegister).toBeFalse();
    });

    it('refuses once the event has started', () => {
      withEvent({ startTime: '2020-01-01T00:00:00Z' });

      expect(component.canRegister).toBeFalse();
      expect(component.isEventStarted(component.event!)).toBeTrue();
    });

    it('refuses for a paid event', () => {
      withEvent({ registerCost: 25 });

      expect(component.canRegister).toBeFalse();
    });

    it('refuses once capacity is reached', () => {
      withEvent({ maxParticipants: 10, registrationCount: 10 });

      expect(component.canRegister).toBeFalse();
      expect(component.isFull).toBeTrue();
    });

    it('treats an uncapped event as never full', () => {
      withEvent({ maxParticipants: 0, registrationCount: 999 });

      expect(component.isFull).toBeFalse();
      expect(component.canRegister).toBeTrue();
    });

    it('does not offer the waitlist for an unpublished or started event', () => {
      makeFullWaitlistEvent({ lifecycleState: 'Draft' });
      createComponent();
      expect(component.waitlistOffered).toBeFalse();

      makeFullWaitlistEvent({ startTime: '2020-01-01T00:00:00Z' });
      createComponent();
      expect(component.waitlistOffered).toBeFalse();
    });
  });

  describe('display helpers', () => {
    beforeEach(() => createComponent());

    it('selects the hero image and falls back to the first', () => {
      expect(component.heroImage).toBe('https://example.com/poster.png');

      component.selectImage(9);
      expect(component.selectedImageIndex).toBe(9);
      expect(component.heroImage).toBe('https://example.com/poster.png');
    });

    it('has no hero image without any', () => {
      eventsService.getEvent.and.returnValue(
        of({ ...response, data: { ...response.data!, imageUrls: [] } }),
      );
      createComponent();

      expect(component.heroImage).toBeNull();
    });

    it('formats a schedule with and without an end time', () => {
      const withEnd = component.formatSchedule(component.event!);
      expect(withEnd).toContain(' - ');

      const withoutEnd = component.formatSchedule({ ...component.event!, endTime: undefined });
      expect(withoutEnd).not.toContain(' - ');
    });

    it('labels a zero cost as Free', () => {
      expect(component.formatCost(0)).toBe('Free');
      expect(component.formatCost(25)).toBe('$25');
    });

    it('reports registration as a clamped percentage', () => {
      expect(
        component.registrationPercent({
          ...component.event!,
          registrationCount: 60,
          maxParticipants: 120,
        }),
      ).toBe(50);
      expect(
        component.registrationPercent({
          ...component.event!,
          registrationCount: 200,
          maxParticipants: 120,
        }),
      ).toBe(100);
      expect(component.registrationPercent({ ...component.event!, maxParticipants: 0 })).toBe(0);
    });

    it('reports whether the visitor is on the waitlist', () => {
      expect(component.onWaitlist).toBeFalse();

      component.waitlistStatus = { onWaitlist: true, waitlistCount: 1 };
      expect(component.onWaitlist).toBeTrue();
    });
  });

  it('shows the generic adapter message for 5xx event fetch failures', () => {
    eventsService.getEvent.and.returnValue(
      throwError(() => new ApiClientServerError(GENERIC_API_ERROR_MESSAGE, 500)),
    );

    createComponent();

    expect(component.event).toBeNull();
    expect(component.error).toBe(GENERIC_API_ERROR_MESSAGE);
    expect(component.loading).toBeFalse();
  });

  describe('favourites', () => {
    afterEach(() => {
      signedInUser = null;
    });

    it('mirrors the shared star state for a signed-in visitor', () => {
      signedInUser = { Id: 7 };

      createComponent();

      expect(favouritesStore.ensureLoaded).toHaveBeenCalled();
      expect(favouritesStore.isFavourited$).toHaveBeenCalledWith(42);
      expect(component.isFavourited).toBeFalse();

      favourited$.next(true);
      expect(component.isFavourited).toBeTrue();
    });

    it('does not track star state for a signed-out visitor', () => {
      signedInUser = null;

      createComponent();

      // The star still renders — pressing it routes to login — but the page's own
      // "Saved to your pinned events" label must not claim a state nobody has.
      favourited$.next(true);
      expect(component.isFavourited).toBeFalse();
    });

    it('surfaces a message when a toggle fails', () => {
      signedInUser = { Id: 7 };
      createComponent();

      component.onFavouriteFailed(new ApiClientServerError(GENERIC_API_ERROR_MESSAGE, 500));

      expect(component.favouriteError).toBe(GENERIC_API_ERROR_MESSAGE);
    });
  });
  describe('recording the view', () => {
    it('records once the event has loaded', () => {
      signedInUser = { Id: 7 };
      createComponent();

      expect(recentlyViewedStore.recordView).toHaveBeenCalledOnceWith(42);
    });

    it('records for a signed-out visitor too', () => {
      signedInUser = null;
      createComponent();

      // The browser-held history is the whole point of recording anonymously, so this must not
      // sit behind the signed-in branch that gates registration and favourite state.
      expect(recentlyViewedStore.recordView).toHaveBeenCalledOnceWith(42);
    });

    it('records nothing when the event fails to load', () => {
      eventsService.getEvent.and.returnValue(
        throwError(() => new ApiClientServerError('boom', 500)),
      );

      createComponent();

      // Recording only after a successful fetch is what keeps a 404 or an invisible private
      // event out of the history in the first place.
      expect(recentlyViewedStore.recordView).not.toHaveBeenCalled();
    });

    it('records nothing when the payload is empty', () => {
      eventsService.getEvent.and.returnValue(of({ ...response, data: null }));

      createComponent();

      expect(recentlyViewedStore.recordView).not.toHaveBeenCalled();
    });

    it('records the new event when the route changes', () => {
      createComponent();
      recentlyViewedStore.recordView.calls.reset();

      eventsService.getEvent.and.returnValue(
        of({ ...response, data: { ...response.data!, id: 99 } }),
      );
      route.setParamMap({ eventId: '99' });

      expect(recentlyViewedStore.recordView).toHaveBeenCalledOnceWith(99);
    });
  });
});
