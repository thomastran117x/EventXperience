import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, ParamMap, Router, RouterModule } from '@angular/router';
import { Subject, debounceTime, takeUntil } from 'rxjs';

import { EventsService } from '../../services/events.service';
import { EventFavouriteToggleComponent } from '../../components/event-favourite-toggle/event-favourite-toggle.component';
import { RecentlyViewedRailComponent } from '../../components/recently-viewed-rail/recently-viewed-rail.component';
import {
  ALL_CATEGORIES,
  ALL_EVENT_SORTS,
  ALL_STATUSES,
  CATEGORY_STYLES,
  EventCategory,
  EventItem,
  EventSortBy,
  EventStatus,
} from '../../models/event.types';
import {
  extractEnvelopeData,
  extractEnvelopeMeta,
} from '../../../../core/api/models/api-envelope.model';
import { FeatureFlagsService } from '../../../../core/features/feature-flags.service';
import { FEATURE_KEYS } from '../../../../core/features/feature-flags.types';
import {
  getApiClientMessage,
  isApiClientClientError,
} from '../../../../core/api/models/api-client-error.model';

type SearchPageState = {
  searchQuery: string;
  cityQuery: string;
  selectedCategory: EventCategory | null;
  selectedStatus: EventStatus | null;
  selectedSort: EventSortBy;
  tags: string[];
  latitude: number | null;
  longitude: number | null;
  radiusKm: number | null;
  currentPage: number;
};

type FilterChip = {
  kind: 'category' | 'status' | 'tag' | 'location' | 'radius' | 'sort' | 'city' | 'search';
  value?: string;
  label: string;
};

@Component({
  selector: 'app-events-search',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterModule,
    EventFavouriteToggleComponent,
    RecentlyViewedRailComponent,
  ],
  templateUrl: './events-search.component.html',
})
export class EventsSearchComponent implements OnInit, OnDestroy {
  private static readonly DEFAULT_SORT: EventSortBy = 'Relevance';
  private static readonly MAX_TAGS = 5;

  searchQuery = '';
  cityQuery = '';
  selectedCategory: EventCategory | null = null;
  selectedStatus: EventStatus | null = null;
  selectedSort: EventSortBy = EventsSearchComponent.DEFAULT_SORT;
  tags: string[] = [];
  tagInput = '';
  latitude: number | null = null;
  longitude: number | null = null;
  radiusKm: number | null = null;

  currentPage = 1;
  readonly pageSize = 18;

  events: EventItem[] = [];
  totalCount = 0;
  totalPages = 0;
  loading = false;
  error = '';
  resultSource = '';
  geolocationError = '';
  locatingUser = false;

  readonly allCategories = ALL_CATEGORIES;
  readonly allStatuses = ALL_STATUSES;
  readonly categoryStyles = CATEGORY_STYLES;
  readonly suggestedTags = ['free', 'outdoor', 'student', 'career', 'community'];
  readonly baseRadiusOptions = [5, 10, 25, 50, 100, 250];
  readonly sortOptions: Array<{
    value: EventSortBy;
    label: string;
    requiresCoordinates?: boolean;
  }> = [
    { value: 'Relevance', label: 'Best match' },
    { value: 'Date', label: 'Soonest first' },
    { value: 'Popularity', label: 'Most popular' },
    { value: 'Distance', label: 'Closest first', requiresCoordinates: true },
  ];
  readonly statusOptions: { value: EventStatus | null; label: string }[] = [
    { value: null, label: 'Any status' },
    { value: 'Upcoming', label: 'Upcoming' },
    { value: 'Ongoing', label: 'Happening now' },
    { value: 'Closed', label: 'Closed' },
  ];

  private readonly textChange$ = new Subject<void>();
  private readonly destroy$ = new Subject<void>();
  private requestVersion = 0;

  /**
   * Search cards must not advertise a waitlist the backend won't accept: the controller is
   * [FeatureGate]-d, and the detail CTA, routes and navbar are already gated on this flag.
   */
  readonly waitlistFeatureEnabled: boolean;

  /** Same reasoning as the waitlist flag: don't offer a control the backend has gated off. */
  readonly favouritesFeatureEnabled: boolean;

  constructor(
    private eventsService: EventsService,
    private route: ActivatedRoute,
    private router: Router,
    private featureFlags: FeatureFlagsService,
  ) {
    this.waitlistFeatureEnabled = this.featureFlags.isEnabled(FEATURE_KEYS.eventsWaitlist);
    this.favouritesFeatureEnabled = this.featureFlags.isEnabled(FEATURE_KEYS.eventsFavourites);
  }

  ngOnInit(): void {
    this.textChange$
      .pipe(debounceTime(400), takeUntil(this.destroy$))
      .subscribe(() => this.syncUrlToState());

    this.route.queryParamMap
      .pipe(takeUntil(this.destroy$))
      .subscribe((params) => this.applyRouteState(params));
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  get hasCoordinates(): boolean {
    return this.latitude !== null && this.longitude !== null;
  }

  get hasActiveFilters(): boolean {
    return this.activeFilters.length > 0;
  }

  get pageNumbers(): number[] {
    const total = this.totalPages;
    if (total <= 7) return Array.from({ length: total }, (_, i) => i + 1);

    const pages: number[] = [1];
    const start = Math.max(2, this.currentPage - 1);
    const end = Math.min(total - 1, this.currentPage + 1);

    if (start > 2) pages.push(-1);
    for (let index = start; index <= end; index++) pages.push(index);
    if (end < total - 1) pages.push(-1);
    pages.push(total);

    return pages;
  }

  get activeFilters(): FilterChip[] {
    const chips: FilterChip[] = [];

    if (this.searchQuery) chips.push({ kind: 'search', label: `Search: ${this.searchQuery}` });
    if (this.cityQuery) chips.push({ kind: 'city', label: `City: ${this.cityQuery}` });
    if (this.selectedCategory) chips.push({ kind: 'category', label: this.selectedCategory });
    if (this.selectedStatus) chips.push({ kind: 'status', label: this.selectedStatus });
    if (this.selectedSort !== EventsSearchComponent.DEFAULT_SORT) {
      chips.push({
        kind: 'sort',
        label: `Sort: ${this.sortOptions.find((option) => option.value === this.selectedSort)?.label ?? this.selectedSort}`,
      });
    }
    for (const tag of this.tags) {
      chips.push({ kind: 'tag', value: tag, label: `#${tag}` });
    }
    if (this.hasCoordinates) {
      chips.push({ kind: 'location', label: `Nearby ${this.coordinateLabel}` });
    }
    if (this.radiusKm !== null) {
      chips.push({ kind: 'radius', label: `Within ${this.radiusKm} km` });
    }

    return chips;
  }

  get availableRadiusOptions(): number[] {
    const options = new Set(this.baseRadiusOptions);
    if (this.radiusKm !== null) {
      options.add(this.radiusKm);
    }

    return Array.from(options).sort((left, right) => left - right);
  }

  get coordinateLabel(): string {
    if (!this.hasCoordinates) {
      return '';
    }

    return `${this.latitude!.toFixed(2)}, ${this.longitude!.toFixed(2)}`;
  }

  get sourceLabel(): string {
    if (this.resultSource === 'database') {
      return 'Fallback results';
    }

    if (this.resultSource === 'elasticsearch') {
      return 'Search index';
    }

    return this.resultSource;
  }

  onTextChange(): void {
    this.currentPage = 1;
    this.textChange$.next();
  }

  selectCategory(category: EventCategory | null): void {
    this.selectedCategory = category;
    this.currentPage = 1;
    this.syncUrlToState();
  }

  onFilterChange(): void {
    if (this.selectedSort === 'Distance' && !this.hasCoordinates) {
      this.selectedSort = EventsSearchComponent.DEFAULT_SORT;
    }

    this.currentPage = 1;
    this.syncUrlToState();
  }

  onTagInputKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' || event.key === ',') {
      event.preventDefault();
      this.addTagFromInput();
      return;
    }

    if (event.key === 'Backspace' && !this.tagInput.trim() && this.tags.length > 0) {
      this.removeTag(this.tags[this.tags.length - 1]);
    }
  }

  addTagFromInput(): void {
    const nextTags = this.parseTagInput(this.tagInput);
    this.tagInput = '';

    if (nextTags.length === 0) {
      return;
    }

    const merged = this.mergeTags(this.tags, nextTags);
    if (this.areSameTags(merged, this.tags)) {
      return;
    }

    this.tags = merged;
    this.currentPage = 1;
    this.syncUrlToState();
  }

  addSuggestedTag(tag: string): void {
    if (this.tags.includes(tag)) {
      return;
    }

    this.tags = this.mergeTags(this.tags, [tag]);
    this.currentPage = 1;
    this.syncUrlToState();
  }

  removeTag(tag: string): void {
    const nextTags = this.tags.filter((current) => current !== tag);
    if (nextTags.length === this.tags.length) {
      return;
    }

    this.tags = nextTags;
    this.currentPage = 1;
    this.syncUrlToState();
  }

  useCurrentLocation(): void {
    this.geolocationError = '';

    if (!navigator.geolocation) {
      this.geolocationError = 'This browser does not support location access.';
      return;
    }

    this.locatingUser = true;

    navigator.geolocation.getCurrentPosition(
      (position) => {
        this.locatingUser = false;
        this.latitude = Number(position.coords.latitude.toFixed(6));
        this.longitude = Number(position.coords.longitude.toFixed(6));
        this.radiusKm ??= 25;
        this.selectedSort = 'Distance';
        this.currentPage = 1;
        this.syncUrlToState();
      },
      (error) => {
        this.locatingUser = false;
        this.geolocationError = error.message || 'Unable to retrieve your location.';
      },
      {
        enableHighAccuracy: false,
        timeout: 10000,
        maximumAge: 300000,
      },
    );
  }

  clearLocation(): void {
    const hadLocationFilters = this.hasCoordinates || this.radiusKm !== null;

    this.latitude = null;
    this.longitude = null;
    this.radiusKm = null;
    this.geolocationError = '';

    if (this.selectedSort === 'Distance') {
      this.selectedSort = EventsSearchComponent.DEFAULT_SORT;
    }

    if (hadLocationFilters) {
      this.currentPage = 1;
      this.syncUrlToState();
    }
  }

  clearChip(chip: FilterChip): void {
    switch (chip.kind) {
      case 'search':
        this.searchQuery = '';
        break;
      case 'city':
        this.cityQuery = '';
        break;
      case 'category':
        this.selectedCategory = null;
        break;
      case 'status':
        this.selectedStatus = null;
        break;
      case 'sort':
        this.selectedSort = EventsSearchComponent.DEFAULT_SORT;
        break;
      case 'tag':
        if (chip.value) {
          this.tags = this.tags.filter((tag) => tag !== chip.value);
        }
        break;
      case 'location':
        this.latitude = null;
        this.longitude = null;
        this.radiusKm = null;
        this.geolocationError = '';
        if (this.selectedSort === 'Distance') {
          this.selectedSort = EventsSearchComponent.DEFAULT_SORT;
        }
        break;
      case 'radius':
        this.radiusKm = null;
        break;
    }

    this.currentPage = 1;
    this.syncUrlToState();
  }

  clearFilters(): void {
    this.searchQuery = '';
    this.cityQuery = '';
    this.selectedCategory = null;
    this.selectedStatus = null;
    this.selectedSort = EventsSearchComponent.DEFAULT_SORT;
    this.tags = [];
    this.tagInput = '';
    this.latitude = null;
    this.longitude = null;
    this.radiusKm = null;
    this.geolocationError = '';
    this.currentPage = 1;
    this.syncUrlToState();
  }

  viewEvent(eventId: number): void {
    this.router.navigate(['/events', eventId], {
      queryParams: this.route.snapshot.queryParams,
    });
  }

  onFavouriteFailed(response: unknown): void {
    this.error = getApiClientMessage(response, 'We could not update your saved events.');
  }

  goToPage(page: number): void {
    if (page < 1 || page > this.totalPages) return;

    this.currentPage = page;
    this.syncUrlToState();
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  formatDate(iso: string): string {
    const date = new Date(iso);
    return date.toLocaleDateString('en-CA', {
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

  formatCost(cost: number): string {
    return cost === 0 ? 'Free' : `$${cost}`;
  }

  registrationPercent(event: EventItem): number {
    if (event.maxParticipants <= 0) {
      return 0;
    }

    return Math.min(100, (event.registrationCount / event.maxParticipants) * 100);
  }

  isFull(event: EventItem): boolean {
    return event.maxParticipants > 0 && event.registrationCount >= event.maxParticipants;
  }

  private applyRouteState(params: ParamMap): void {
    const nextState = this.readStateFromParams(params);
    const needsUrlSync = this.stateRequiresCanonicalUrl(params, nextState);

    this.searchQuery = nextState.searchQuery;
    this.cityQuery = nextState.cityQuery;
    this.selectedCategory = nextState.selectedCategory;
    this.selectedStatus = nextState.selectedStatus;
    this.selectedSort = nextState.selectedSort;
    this.tags = nextState.tags;
    this.latitude = nextState.latitude;
    this.longitude = nextState.longitude;
    this.radiusKm = nextState.radiusKm;
    this.currentPage = nextState.currentPage;

    if (needsUrlSync) {
      this.syncUrlToState();
      return;
    }

    this.fetch();
  }

  private fetch(): void {
    const requestVersion = ++this.requestVersion;
    this.loading = true;
    this.error = '';

    this.eventsService
      .getEvents({
        search: this.searchQuery || undefined,
        city: this.cityQuery || undefined,
        category: this.selectedCategory ?? undefined,
        status: this.selectedStatus ?? undefined,
        sortBy: this.selectedSort,
        tags: this.tags.length > 0 ? this.tags.join(',') : undefined,
        lat: this.latitude ?? undefined,
        lng: this.longitude ?? undefined,
        radiusKm: this.radiusKm ?? undefined,
        page: this.currentPage,
        pageSize: this.pageSize,
      })
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (response) => {
          if (requestVersion !== this.requestVersion) {
            return;
          }

          const data = extractEnvelopeData(response);
          if (!data) {
            this.resetResults();
            this.error =
              response.message || response.Message || 'Failed to load events. Please try again.';
            this.loading = false;
            return;
          }

          this.events = data.items;
          this.totalCount = data.totalCount;
          this.totalPages = data.totalPages;
          const source = extractEnvelopeMeta(response)?.source;
          this.resultSource = typeof source === 'string' ? source : '';
          this.loading = false;
        },
        error: (response) => {
          if (requestVersion !== this.requestVersion) {
            return;
          }

          this.resetResults();
          this.error = isApiClientClientError(response)
            ? response.message
            : getApiClientMessage(response, 'Failed to load events. Please try again.');
          this.loading = false;
        },
      });
  }

  private resetResults(): void {
    this.events = [];
    this.totalCount = 0;
    this.totalPages = 0;
    this.resultSource = '';
  }

  private readStateFromParams(params: ParamMap): SearchPageState {
    const latitude = this.parseCoordinate(params.get('lat'), -90, 90);
    const longitude = this.parseCoordinate(params.get('lng'), -180, 180);
    const hasCoordinates = latitude !== null && longitude !== null;

    return this.normalizeState({
      searchQuery: (params.get('search') ?? '').trim(),
      cityQuery: (params.get('city') ?? '').trim(),
      selectedCategory: this.parseCategory(params.get('category')),
      selectedStatus: this.parseStatus(params.get('status')),
      selectedSort: this.parseSort(params.get('sort')),
      tags: this.parseTagInput(params.get('tags') ?? ''),
      latitude: hasCoordinates ? latitude : null,
      longitude: hasCoordinates ? longitude : null,
      radiusKm: hasCoordinates ? this.parseRadius(params.get('radiusKm')) : null,
      currentPage: this.parsePage(params.get('page')),
    });
  }

  private normalizeState(state: SearchPageState): SearchPageState {
    const hasCoordinates = state.latitude !== null && state.longitude !== null;

    return {
      searchQuery: state.searchQuery.trim(),
      cityQuery: state.cityQuery.trim(),
      selectedCategory: state.selectedCategory,
      selectedStatus: state.selectedStatus,
      selectedSort:
        state.selectedSort === 'Distance' && !hasCoordinates
          ? EventsSearchComponent.DEFAULT_SORT
          : state.selectedSort,
      tags: this.mergeTags([], state.tags),
      latitude: hasCoordinates ? state.latitude : null,
      longitude: hasCoordinates ? state.longitude : null,
      radiusKm: hasCoordinates ? state.radiusKm : null,
      currentPage: state.currentPage > 0 ? state.currentPage : 1,
    };
  }

  private stateRequiresCanonicalUrl(params: ParamMap, state: SearchPageState): boolean {
    const current = this.serializeQueryParams(
      params.keys.reduce<Record<string, string>>((accumulator, key) => {
        const value = params.get(key);
        if (value !== null) {
          accumulator[key] = value;
        }

        return accumulator;
      }, {}),
    );

    return current !== this.serializeQueryParams(this.buildQueryParams(state));
  }

  private syncUrlToState(): void {
    const queryParams = this.buildQueryParams();

    this.router.navigate([], {
      relativeTo: this.route,
      queryParams,
      replaceUrl: true,
    });
  }

  private buildQueryParams(state: SearchPageState = this.currentState()): Record<string, string> {
    const queryParams: Record<string, string> = {};

    if (state.searchQuery) queryParams['search'] = state.searchQuery;
    if (state.cityQuery) queryParams['city'] = state.cityQuery;
    if (state.selectedCategory) queryParams['category'] = state.selectedCategory;
    if (state.selectedStatus) queryParams['status'] = state.selectedStatus;
    if (state.selectedSort !== EventsSearchComponent.DEFAULT_SORT)
      queryParams['sort'] = state.selectedSort;
    if (state.tags.length > 0) queryParams['tags'] = state.tags.join(',');
    if (state.latitude !== null && state.longitude !== null) {
      queryParams['lat'] = String(state.latitude);
      queryParams['lng'] = String(state.longitude);
      if (state.radiusKm !== null) {
        queryParams['radiusKm'] = String(state.radiusKm);
      }
    }
    if (state.currentPage > 1) queryParams['page'] = String(state.currentPage);

    return queryParams;
  }

  private currentState(): SearchPageState {
    return this.normalizeState({
      searchQuery: this.searchQuery,
      cityQuery: this.cityQuery,
      selectedCategory: this.selectedCategory,
      selectedStatus: this.selectedStatus,
      selectedSort: this.selectedSort,
      tags: this.tags,
      latitude: this.latitude,
      longitude: this.longitude,
      radiusKm: this.radiusKm,
      currentPage: this.currentPage,
    });
  }

  private serializeQueryParams(params: Record<string, string>): string {
    return Object.entries(params)
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([key, value]) => `${key}=${value}`)
      .join('&');
  }

  private parseCategory(value: string | null): EventCategory | null {
    return value && ALL_CATEGORIES.includes(value as EventCategory)
      ? (value as EventCategory)
      : null;
  }

  private parseStatus(value: string | null): EventStatus | null {
    return value && ALL_STATUSES.includes(value as EventStatus) ? (value as EventStatus) : null;
  }

  private parseSort(value: string | null): EventSortBy {
    return value && ALL_EVENT_SORTS.includes(value as EventSortBy)
      ? (value as EventSortBy)
      : EventsSearchComponent.DEFAULT_SORT;
  }

  private parsePage(value: string | null): number {
    if (!value) {
      return 1;
    }

    const page = Number.parseInt(value, 10);
    return Number.isFinite(page) && page > 0 ? page : 1;
  }

  private parseRadius(value: string | null): number | null {
    if (!value) {
      return null;
    }

    const radiusKm = Number.parseFloat(value);
    return Number.isFinite(radiusKm) && radiusKm > 0 && radiusKm <= 500 ? radiusKm : null;
  }

  private parseCoordinate(value: string | null, min: number, max: number): number | null {
    if (!value) {
      return null;
    }

    const coordinate = Number.parseFloat(value);
    return Number.isFinite(coordinate) && coordinate >= min && coordinate <= max
      ? coordinate
      : null;
  }

  private parseTagInput(value: string): string[] {
    if (!value.trim()) {
      return [];
    }

    return value
      .split(',')
      .map((tag) => tag.trim().toLowerCase())
      .filter((tag) => tag.length > 0);
  }

  private mergeTags(existing: string[], incoming: string[]): string[] {
    const merged = new Set(existing);

    for (const tag of incoming) {
      if (merged.size >= EventsSearchComponent.MAX_TAGS) {
        break;
      }

      merged.add(tag);
    }

    return Array.from(merged);
  }

  private areSameTags(left: string[], right: string[]): boolean {
    return left.length === right.length && left.every((value, index) => value === right[index]);
  }
}
