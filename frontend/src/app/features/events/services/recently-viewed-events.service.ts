import { Injectable } from '@angular/core';
import { map, Observable } from 'rxjs';
import { environment } from '@environments/environment';
import { ApiEnvelope } from '../../../core/api/models/api-envelope.model';
import { ApiClient } from '../../../core/api/services/api-client.service';
import {
  normalizeMergeResult,
  normalizeRecentlyViewedEntries,
  normalizeRecentlyViewedSettings,
  normalizeRecordViewResult,
} from '../models/recently-viewed-normalizers';
import {
  RecentlyViewedEntry,
  RecentlyViewedLocalItem,
  RecentlyViewedMergeResult,
  RecentlyViewedSettings,
  RecordEventViewResult,
} from '../models/recently-viewed.types';

@Injectable({ providedIn: 'root' })
export class RecentlyViewedEventsService {
  private readonly base = `${environment.backendUrl}/events`;

  constructor(private api: ApiClient) {}

  recordView(eventId: number): Observable<RecordEventViewResult> {
    return this.api
      .post<ApiEnvelope<unknown>>(`${this.base}/${eventId}/view`, {}, { withCredentials: true })
      .pipe(map((response) => normalizeRecordViewResult(eventId, this.unwrap(response))));
  }

  getRecent(): Observable<RecentlyViewedEntry[]> {
    return this.api
      .get<ApiEnvelope<unknown>>(`${this.base}/me/recently-viewed`, { withCredentials: true })
      .pipe(map((response) => normalizeRecentlyViewedEntries(this.unwrap(response))));
  }

  remove(eventId: number): Observable<void> {
    return this.api
      .delete<ApiEnvelope<unknown>>(`${this.base}/me/recently-viewed/${eventId}`, {
        withCredentials: true,
      })
      .pipe(map(() => void 0));
  }

  /**
   * Removes a multi-selected subset in one request rather than one per entry. DELETE carries a
   * body here, which ApiClient already supports.
   */
  removeMany(eventIds: number[]): Observable<void> {
    return this.api
      .delete<ApiEnvelope<unknown>>(`${this.base}/me/recently-viewed/batch`, {
        withCredentials: true,
        body: { ids: eventIds },
      })
      .pipe(map(() => void 0));
  }

  clear(): Observable<void> {
    return this.api
      .delete<ApiEnvelope<unknown>>(`${this.base}/me/recently-viewed`, { withCredentials: true })
      .pipe(map(() => void 0));
  }

  /** Folds the browser-held history into the account at login. */
  merge(items: RecentlyViewedLocalItem[]): Observable<RecentlyViewedMergeResult> {
    const payload = {
      items: items.map((item) => ({ eventId: item.id, viewedAtUtc: item.at })),
    };

    return this.api
      .post<ApiEnvelope<unknown>>(`${this.base}/me/recently-viewed/merge`, payload, {
        withCredentials: true,
      })
      .pipe(map((response) => normalizeMergeResult(this.unwrap(response))));
  }

  getSettings(): Observable<RecentlyViewedSettings> {
    return this.api
      .get<ApiEnvelope<unknown>>(`${this.base}/me/recently-viewed/settings`, {
        withCredentials: true,
      })
      .pipe(map((response) => normalizeRecentlyViewedSettings(this.unwrap(response))));
  }

  updateSettings(enabled: boolean): Observable<RecentlyViewedSettings> {
    return this.api
      .put<ApiEnvelope<unknown>>(
        `${this.base}/me/recently-viewed/settings`,
        { enabled },
        { withCredentials: true },
      )
      .pipe(map((response) => normalizeRecentlyViewedSettings(this.unwrap(response))));
  }

  private unwrap<T>(response: ApiEnvelope<T, unknown>): T {
    return (response.data ?? (response as unknown as { Data?: T }).Data) as T;
  }
}
