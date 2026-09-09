import { Injectable } from '@angular/core';
import { HttpParams } from '@angular/common/http';
import { map, Observable, of } from 'rxjs';
import { environment } from '@environments/environment';
import {
  ALL_CATEGORIES,
  ALL_LIFECYCLE_STATES,
  ALL_STATUSES,
  ClubType,
  EventApiResponse,
  EventCategory,
  EventHostClub,
  EventItem,
  EventLifecycleState,
  EventSearchParams,
  EventsApiResponse,
  EventsPagedData,
  EventStatus,
} from '../models/event.types';
import { EventItemPayload, normalizeEventItem } from '../models/event-normalizers';
import { ApiEnvelope } from '../../../core/api/models/api-envelope.model';
import { ApiClient } from '../../../core/api/services/api-client.service';

type EventsPagedPayload = Partial<EventsPagedData> & {
  Items?: EventItemPayload[];
  TotalCount?: number;
  Page?: number;
  PageSize?: number;
  TotalPages?: number;
};

type EventsApiPayload = ApiEnvelope<EventsPagedPayload> & {
  Data?: EventsPagedPayload | null;
};

type EventApiPayload = ApiEnvelope<EventItemPayload> & {
  Data?: EventItemPayload | null;
};

@Injectable({ providedIn: 'root' })
export class EventsService {
  private readonly base = `${environment.backendUrl}/events`;

  constructor(private api: ApiClient) {}

  getEvents(params: EventSearchParams): Observable<EventsApiResponse> {
    let httpParams = new HttpParams();

    if (params.search?.trim()) httpParams = httpParams.set('search', params.search.trim());
    if (params.city?.trim()) httpParams = httpParams.set('city', params.city.trim());
    if (params.category) httpParams = httpParams.set('category', params.category);
    if (params.status) httpParams = httpParams.set('status', params.status);
    if (params.sortBy) httpParams = httpParams.set('sortBy', params.sortBy);
    if (params.tags?.trim()) httpParams = httpParams.set('tags', params.tags.trim());
    if (params.lat !== undefined) httpParams = httpParams.set('lat', String(params.lat));
    if (params.lng !== undefined) httpParams = httpParams.set('lng', String(params.lng));
    if (params.radiusKm !== undefined)
      httpParams = httpParams.set('radiusKm', String(params.radiusKm));
    if (params.page) httpParams = httpParams.set('page', String(params.page));
    if (params.pageSize) httpParams = httpParams.set('pageSize', String(params.pageSize));

    return this.api
      .get<EventsApiPayload>(this.base, { params: httpParams })
      .pipe(map((response) => this.normalizeResponse(response)));
  }

  getEvent(eventId: number): Observable<EventApiResponse> {
    return this.api
      .get<EventApiPayload>(`${this.base}/${eventId}`)
      .pipe(map((response) => this.normalizeEventResponse(response)));
  }

  /**
   * Resolves a set of ids to events in one request, preserving the order asked for.
   *
   * Unauthenticated-safe: the endpoint applies visibility with whatever caller it can see, so ids
   * the caller may not view simply fall out rather than erroring. That is what lets a signed-out
   * visitor hydrate a locally-held list of ids without disclosing anything.
   */
  getEventsBatch(eventIds: number[]): Observable<EventItem[]> {
    if (eventIds.length === 0) {
      return of([]);
    }

    const httpParams = new HttpParams().set('ids', eventIds.join(','));

    return this.api
      .get<ApiEnvelope<EventItemPayload[]>>(`${this.base}/batch`, { params: httpParams })
      .pipe(
        map((response) => {
          const payload = response.data ?? (response as { Data?: EventItemPayload[] }).Data ?? [];
          return payload.map((item) => normalizeEventItem(item));
        }),
      );
  }

  getEventsByClub(
    clubId: number,
    params: { status?: EventStatus; page?: number; pageSize?: number; search?: string } = {},
  ): Observable<EventsApiResponse> {
    let httpParams = new HttpParams();
    if (params.status) httpParams = httpParams.set('status', params.status);
    if (params.page) httpParams = httpParams.set('page', String(params.page));
    if (params.pageSize) httpParams = httpParams.set('pageSize', String(params.pageSize));
    if (params.search?.trim()) httpParams = httpParams.set('search', params.search.trim());

    return this.api
      .get<EventsApiPayload>(`${this.base}/clubs/${clubId}`, { params: httpParams })
      .pipe(map((response) => this.normalizeResponse(response)));
  }

  private normalizeResponse(response: EventsApiPayload): EventsApiResponse {
    const payload = response.data ?? response.Data ?? null;

    return {
      ...response,
      data: payload ? this.normalizePagedData(payload) : null,
      Data: undefined,
    };
  }

  private normalizeEventResponse(response: EventApiPayload): EventApiResponse {
    const payload = response.data ?? response.Data ?? null;

    return {
      ...response,
      data: payload ? normalizeEventItem(payload) : null,
      Data: undefined,
    };
  }

  private normalizePagedData(payload: EventsPagedPayload): EventsPagedData {
    return {
      items: (payload.items ?? payload.Items ?? []).map((item) => normalizeEventItem(item)),
      totalCount: payload.totalCount ?? payload.TotalCount ?? 0,
      page: payload.page ?? payload.Page ?? 1,
      pageSize: payload.pageSize ?? payload.PageSize ?? 20,
      totalPages: payload.totalPages ?? payload.TotalPages ?? 0,
    };
  }
}
