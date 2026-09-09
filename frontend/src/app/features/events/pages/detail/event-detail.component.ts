import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormBuilder, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, ParamMap, Router } from '@angular/router';
import { Store } from '@ngrx/store';
import { Subject, take, takeUntil } from 'rxjs';

import { extractEnvelopeData } from '../../../../core/api/models/api-envelope.model';
import { getApiClientMessage } from '../../../../core/api/models/api-client-error.model';
import { UserState } from '../../../../core/stores/user.reducer';
import { selectUser } from '../../../../core/stores/user.selectors';
import { EventItem, CATEGORY_STYLES } from '../../models/event.types';
import {
  EventRegistrationService,
  RegistrationDetails,
  MyRegistrationStatus,
} from '../../services/event-registration.service';
import { EventsService } from '../../services/events.service';
import { EventWaitlistService } from '../../services/event-waitlist.service';
import { MyWaitlistStatus } from '../../models/event-waitlist.types';
import { FeatureFlagsService } from '../../../../core/features/feature-flags.service';
import { FEATURE_KEYS } from '../../../../core/features/feature-flags.types';
import { EventFavouritesStore } from '../../services/event-favourites-store.service';
import { RecentlyViewedStore } from '../../services/recently-viewed-store.service';
import { EventFavouriteToggleComponent } from '../../components/event-favourite-toggle/event-favourite-toggle.component';

@Component({
  selector: 'app-event-detail',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, EventFavouriteToggleComponent],
  templateUrl: './event-detail.component.html',
})
export class EventDetailComponent implements OnInit, OnDestroy {
  event: EventItem | null = null;
  loading = true;
  error = '';
  selectedImageIndex = 0;
  returnQueryParams: Record<string, string> = {};

  isRegistered = false;
  registrationLoading = false;
  registrationError = '';
  currentUserId: number | null = null;
  showRegistrationForm = false;
  isEditing = false;
  registrationDetails: RegistrationDetails | null = null;

  waitlistStatus: MyWaitlistStatus | null = null;
  waitlistLoading = false;
  waitlistError = '';
  readonly waitlistFeatureEnabled: boolean;

  /** Read-only here — the toggle component owns the write side. Drives the CTA wording. */
  isFavourited = false;
  favouriteError = '';
  readonly favouritesFeatureEnabled: boolean;

  registrationForm: FormGroup;

  readonly categoryStyles = CATEGORY_STYLES;

  private readonly destroy$ = new Subject<void>();
  private requestVersion = 0;

  constructor(
    private eventsService: EventsService,
    private registrationService: EventRegistrationService,
    private waitlistService: EventWaitlistService,
    private store: Store<{ user: UserState }>,
    private route: ActivatedRoute,
    private router: Router,
    private fb: FormBuilder,
    private featureFlags: FeatureFlagsService,
    private favouritesStore: EventFavouritesStore,
    private recentlyViewedStore: RecentlyViewedStore,
  ) {
    this.waitlistFeatureEnabled = this.featureFlags.isEnabled(FEATURE_KEYS.eventsWaitlist);
    this.favouritesFeatureEnabled = this.featureFlags.isEnabled(FEATURE_KEYS.eventsFavourites);
    this.registrationForm = this.fb.group({
      notes: [''],
      phoneNumber: [''],
      dietaryNeeds: [''],
    });
  }

  ngOnInit(): void {
    this.route.queryParamMap.pipe(takeUntil(this.destroy$)).subscribe((params) => {
      this.returnQueryParams = params.keys.reduce<Record<string, string>>((accumulator, key) => {
        const value = params.get(key);
        if (value !== null) {
          accumulator[key] = value;
        }

        return accumulator;
      }, {});
    });

    this.route.paramMap
      .pipe(takeUntil(this.destroy$))
      .subscribe((params) => this.loadEventFromParams(params));
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  get heroImage(): string | null {
    if (!this.event?.imageUrls?.length) {
      return null;
    }

    return this.event.imageUrls[this.selectedImageIndex] ?? this.event.imageUrls[0] ?? null;
  }

  get canRegister(): boolean {
    if (!this.event) return false;
    if (this.event.lifecycleState !== 'Published') return false;
    if (this.isEventStarted(this.event)) return false;
    if (this.event.registerCost > 0) return false;
    if (
      this.event.maxParticipants > 0 &&
      this.event.registrationCount >= this.event.maxParticipants
    )
      return false;
    return true;
  }

  get isFull(): boolean {
    if (!this.event) return false;
    return (
      this.event.maxParticipants > 0 && this.event.registrationCount >= this.event.maxParticipants
    );
  }

  get onWaitlist(): boolean {
    return this.waitlistStatus?.onWaitlist === true;
  }

  /**
   * Whether a waitlist is on offer for this event. Note this is deliberately independent of
   * `canRegister`: a full event still blocks registration, and the waitlist is an additional
   * path rather than a relaxation of the capacity rule.
   */
  get waitlistOffered(): boolean {
    if (!this.event) return false;
    // The controller is [FeatureGate]-d, so without the global flag every click would fail.
    // The routes and navbar entry are already flag-gated; this keeps the CTA consistent for
    // events that stored waitlistEnabled=true before the flag was turned off.
    if (!this.waitlistFeatureEnabled) return false;
    if (!this.event.waitlistEnabled) return false;
    if (this.event.lifecycleState !== 'Published') return false;
    if (this.isEventStarted(this.event)) return false;
    if (this.event.registerCost > 0) return false;
    return this.isFull;
  }

  get canJoinWaitlist(): boolean {
    return (
      this.waitlistOffered && this.currentUserId !== null && !this.isRegistered && !this.onWaitlist
    );
  }

  joinWaitlist(): void {
    if (!this.event || this.waitlistLoading) return;
    this.waitlistLoading = true;
    this.waitlistError = '';

    this.waitlistService
      .join(this.event.id)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (entry) => {
          this.waitlistLoading = false;
          this.waitlistStatus = {
            onWaitlist: true,
            entryId: entry.id,
            position: entry.position,
            joinedAtUtc: entry.joinedAtUtc,
            waitlistCount: (this.event?.waitlistCount ?? 0) + 1,
          };
          if (this.event) {
            this.event = { ...this.event, waitlistCount: this.event.waitlistCount + 1 };
          }
        },
        error: (response) => {
          this.waitlistLoading = false;
          this.waitlistError = getApiClientMessage(
            response,
            'We could not add you to the waitlist.',
          );
        },
      });
  }

  leaveWaitlist(): void {
    if (!this.event || this.waitlistLoading) return;
    const eventId = this.event.id;
    this.waitlistLoading = true;
    this.waitlistError = '';

    this.waitlistService
      .leave(eventId)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.waitlistLoading = false;
          this.waitlistStatus = null;
          if (this.event) {
            this.event = {
              ...this.event,
              waitlistCount: Math.max(0, this.event.waitlistCount - 1),
            };
          }
          // Leaving shifts everyone below us, so the remaining positions must be refetched
          // rather than guessed.
          this.loadWaitlistStatus(eventId);
        },
        error: (response) => {
          this.waitlistLoading = false;
          this.waitlistError = getApiClientMessage(
            response,
            'We could not remove you from the waitlist.',
          );
        },
      });
  }

  private loadWaitlistStatus(eventId: number): void {
    if (!this.event?.waitlistEnabled) {
      this.waitlistStatus = null;
      return;
    }

    this.waitlistService
      .getMyStatus(eventId)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (status) => (this.waitlistStatus = status),
        error: () => (this.waitlistStatus = null),
      });
  }

  isEventStarted(event: EventItem): boolean {
    return new Date(event.startTime) <= new Date();
  }

  goBack(): void {
    this.router.navigate(['/events'], {
      queryParams: this.returnQueryParams,
    });
  }

  selectImage(index: number): void {
    this.selectedImageIndex = index;
  }

  formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString('en-CA', {
      weekday: 'short',
      month: 'short',
      day: 'numeric',
      year: 'numeric',
    });
  }

  formatTime(iso: string): string {
    return new Date(iso).toLocaleTimeString('en-CA', {
      hour: '2-digit',
      minute: '2-digit',
    });
  }

  formatSchedule(event: EventItem): string {
    const start = `${this.formatDate(event.startTime)} at ${this.formatTime(event.startTime)}`;
    if (!event.endTime) {
      return start;
    }

    return `${start} - ${this.formatTime(event.endTime)}`;
  }

  formatCost(cost: number): string {
    return cost === 0 ? 'Free' : `$${cost}`;
  }

  registrationPercent(event: EventItem): number {
    if (event.maxParticipants <= 0) {
      return 0;
    }

    return Math.min(100, (event.registrationCount / event.maxParticipants) * 100);
  }

  openRegistrationForm(): void {
    this.isEditing = false;
    this.registrationForm.reset();
    this.showRegistrationForm = true;
    this.registrationError = '';
  }

  openEditForm(): void {
    this.isEditing = true;
    this.registrationForm.patchValue({
      notes: this.registrationDetails?.notes ?? '',
      phoneNumber: this.registrationDetails?.phoneNumber ?? '',
      dietaryNeeds: this.registrationDetails?.dietaryNeeds ?? '',
    });
    this.showRegistrationForm = true;
    this.registrationError = '';
  }

  closeRegistrationForm(): void {
    this.showRegistrationForm = false;
  }

  submitRegistration(): void {
    if (!this.event || this.registrationLoading) return;
    this.registrationLoading = true;
    this.registrationError = '';

    const details: RegistrationDetails = {
      notes: this.registrationForm.value.notes || undefined,
      phoneNumber: this.registrationForm.value.phoneNumber || undefined,
      dietaryNeeds: this.registrationForm.value.dietaryNeeds || undefined,
    };

    this.registrationService
      .register(this.event.id, details)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.isRegistered = true;
          this.registrationLoading = false;
          this.showRegistrationForm = false;
          this.registrationDetails = details;
          if (this.event) {
            this.event = { ...this.event, registrationCount: this.event.registrationCount + 1 };
          }
        },
        error: (response) => {
          this.registrationLoading = false;
          this.registrationError = getApiClientMessage(response, 'Registration failed.');
        },
      });
  }

  submitUpdate(): void {
    if (!this.event || this.registrationLoading) return;
    this.registrationLoading = true;
    this.registrationError = '';

    const details: RegistrationDetails = {
      notes: this.registrationForm.value.notes || undefined,
      phoneNumber: this.registrationForm.value.phoneNumber || undefined,
      dietaryNeeds: this.registrationForm.value.dietaryNeeds || undefined,
    };

    this.registrationService
      .updateRegistration(this.event.id, details)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.registrationLoading = false;
          this.showRegistrationForm = false;
          this.registrationDetails = details;
        },
        error: (response) => {
          this.registrationLoading = false;
          this.registrationError = getApiClientMessage(
            response,
            'Failed to update registration details.',
          );
        },
      });
  }

  unregister(): void {
    if (!this.event || this.registrationLoading) return;
    this.registrationLoading = true;
    this.registrationError = '';

    this.registrationService
      .unregister(this.event.id)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.isRegistered = false;
          this.registrationLoading = false;
          this.registrationDetails = null;
          if (this.event) {
            this.event = {
              ...this.event,
              registrationCount: Math.max(0, this.event.registrationCount - 1),
            };

            // A waitlisted user may have been promoted into the seat we just freed, in
            // which case the optimistic decrement above is wrong. Refetch the real counts.
            if (this.event.waitlistEnabled) {
              this.fetch(this.event.id);
            }
          }
        },
        error: (response) => {
          this.registrationLoading = false;
          this.registrationError = getApiClientMessage(response, 'Unregistration failed.');
        },
      });
  }

  onFavouriteFailed(response: unknown): void {
    this.favouriteError = getApiClientMessage(response, 'We could not update your saved events.');
  }

  private loadFavouriteStatus(eventId: number): void {
    if (!this.favouritesFeatureEnabled) return;

    this.favouritesStore
      .isFavourited$(eventId)
      .pipe(takeUntil(this.destroy$))
      .subscribe((favourited) => (this.isFavourited = favourited));

    this.favouritesStore.ensureLoaded();
  }

  private loadRegistrationStatus(eventId: number): void {
    this.store
      .select(selectUser)
      .pipe(take(1))
      .subscribe((user) => {
        this.currentUserId = user?.Id ?? null;
        if (!user) {
          this.isRegistered = false;
          this.waitlistStatus = null;
          this.isFavourited = false;
          return;
        }

        this.loadFavouriteStatus(eventId);

        this.registrationService
          .checkRegistration(eventId)
          .pipe(takeUntil(this.destroy$))
          .subscribe({
            next: (status: MyRegistrationStatus) => {
              this.isRegistered = status.isRegistered;
              this.registrationDetails = status.details;
            },
            error: () => {
              this.isRegistered = false;
            },
          });

        this.loadWaitlistStatus(eventId);
      });
  }

  private loadEventFromParams(params: ParamMap): void {
    const eventId = this.parseEventId(params.get('eventId'));
    if (eventId === null) {
      this.event = null;
      this.loading = false;
      this.error = 'Invalid event ID.';
      return;
    }

    this.fetch(eventId);
  }

  private fetch(eventId: number): void {
    const requestVersion = ++this.requestVersion;
    this.loading = true;
    this.error = '';

    this.eventsService
      .getEvent(eventId)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (response) => {
          if (requestVersion !== this.requestVersion) {
            return;
          }

          const event = extractEnvelopeData(response);
          if (!event) {
            this.event = null;
            this.loading = false;
            this.error = response.message || response.Message || 'Failed to load the event.';
            return;
          }

          this.event = event;
          this.selectedImageIndex = 0;
          this.loading = false;

          // Deliberately beside loadRegistrationStatus rather than inside it: that method returns
          // early for signed-out visitors, and recording their views is the whole point of the
          // browser-held history. Recording only after a successful fetch is also what keeps a 404
          // or an invisible private event out of the history in the first place.
          this.recentlyViewedStore.recordView(event.id);

          this.loadRegistrationStatus(event.id);
        },
        error: (response) => {
          if (requestVersion !== this.requestVersion) {
            return;
          }

          this.event = null;
          this.loading = false;
          this.error = getApiClientMessage(response, 'Failed to load the event.');
        },
      });
  }

  private parseEventId(value: string | null): number | null {
    if (!value) {
      return null;
    }

    const parsed = Number.parseInt(value, 10);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
  }
}
