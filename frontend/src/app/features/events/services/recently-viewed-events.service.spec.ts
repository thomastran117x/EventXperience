import { HttpTestingController } from '@angular/common/http/testing';
import { environment } from '@environments/environment';
import { envelope, pascalEnvelope, setupService } from '@testing';

import { ApiClient } from '../../../core/api/services/api-client.service';
import { RecentlyViewedEventsService } from './recently-viewed-events.service';

describe('RecentlyViewedEventsService', () => {
  const base = `${environment.backendUrl}/events`;
  let service: RecentlyViewedEventsService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    ({ service, httpMock } = setupService(RecentlyViewedEventsService, [ApiClient]));
  });

  afterEach(() => httpMock.verify());

  it('records a view', () => {
    let recorded = false;
    service.recordView(9).subscribe((result) => (recorded = result.recorded));

    const request = httpMock.expectOne(`${base}/9/view`);
    expect(request.request.method).toBe('POST');
    expect(request.request.withCredentials).toBeTrue();
    request.flush(envelope({ eventId: 9, recorded: true, viewedAtUtc: '2026-09-09T12:00:00Z' }));

    expect(recorded).toBeTrue();
  });

  it('reports when a view was not recorded because tracking is off', () => {
    let recorded = true;
    service.recordView(9).subscribe((result) => (recorded = result.recorded));

    httpMock
      .expectOne(`${base}/9/view`)
      .flush(envelope({ eventId: 9, recorded: false, viewedAtUtc: null }));

    expect(recorded).toBeFalse();
  });

  it('fetches the history', () => {
    let ids: number[] = [];
    service.getRecent().subscribe((entries) => (ids = entries.map((entry) => entry.eventId)));

    const request = httpMock.expectOne(`${base}/me/recently-viewed`);
    expect(request.request.method).toBe('GET');
    request.flush(
      envelope([
        { eventId: 3, viewedAtUtc: 'c', event: { id: 3 } },
        { eventId: 1, viewedAtUtc: 'a', event: { id: 1 } },
      ]),
    );

    expect(ids).toEqual([3, 1]);
  });

  it('reads the history from a PascalCase envelope', () => {
    let ids: number[] = [];
    service.getRecent().subscribe((entries) => (ids = entries.map((entry) => entry.eventId)));

    httpMock
      .expectOne(`${base}/me/recently-viewed`)
      .flush(pascalEnvelope([{ EventId: 5, ViewedAtUtc: 'e', Event: { Id: 5 } }]));

    expect(ids).toEqual([5]);
  });

  it('removes one entry', () => {
    service.remove(9).subscribe();

    const request = httpMock.expectOne(`${base}/me/recently-viewed/9`);
    expect(request.request.method).toBe('DELETE');
    request.flush(envelope(null));
  });

  it('removes a selection in a single request carrying the ids', () => {
    service.removeMany([3, 5]).subscribe();

    const request = httpMock.expectOne(`${base}/me/recently-viewed/batch`);
    expect(request.request.method).toBe('DELETE');
    // One request for the whole selection, not one per entry.
    expect(request.request.body).toEqual({ ids: [3, 5] });
    request.flush(envelope(null));
  });

  it('clears the history', () => {
    service.clear().subscribe();

    const request = httpMock.expectOne(`${base}/me/recently-viewed`);
    expect(request.request.method).toBe('DELETE');
    request.flush(envelope(null));
  });

  it('merges a browser-held history, mapping ids and timestamps to the wire shape', () => {
    let merged = 0;
    service
      .merge([
        { id: 1, at: '2026-09-08T12:00:00Z' },
        { id: 2, at: '2026-09-09T12:00:00Z' },
      ])
      .subscribe((result) => (merged = result.merged));

    const request = httpMock.expectOne(`${base}/me/recently-viewed/merge`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      items: [
        { eventId: 1, viewedAtUtc: '2026-09-08T12:00:00Z' },
        { eventId: 2, viewedAtUtc: '2026-09-09T12:00:00Z' },
      ],
    });
    request.flush(envelope({ merged: 2, skipped: 0, total: 2 }));

    expect(merged).toBe(2);
  });

  it('fetches the settings', () => {
    let enabled: boolean | null = null;
    service.getSettings().subscribe((settings) => (enabled = settings.enabled));

    const request = httpMock.expectOne(`${base}/me/recently-viewed/settings`);
    expect(request.request.method).toBe('GET');
    request.flush(envelope({ enabled: false, updatedAtUtc: '2026-09-09T12:00:00Z' }));

    expect(enabled).toBeFalse();
  });

  it('updates the settings', () => {
    let enabled: boolean | null = null;
    service.updateSettings(false).subscribe((settings) => (enabled = settings.enabled));

    const request = httpMock.expectOne(`${base}/me/recently-viewed/settings`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ enabled: false });
    request.flush(envelope({ enabled: false, updatedAtUtc: null }));

    expect(enabled).toBeFalse();
  });
});
