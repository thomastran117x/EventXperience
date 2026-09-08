import { Injectable } from '@angular/core';
import { Observable, from, map, switchMap } from 'rxjs';

import {
  ApiEnvelope,
  extractEnvelopeData,
  requireEnvelopeData,
} from '../../../core/api/models/api-envelope.model';
import { ApiClient } from '../../../core/api/services/api-client.service';
import { AuthTokenService } from '../../../core/api/services/auth-token.service';
import { environment } from '../../../../environments/environment';

export interface UpdateProfilePayload {
  name?: string;
  phone?: string;
  address?: string;
}

export interface MyProfile {
  Id: number;
  Email: string;
  Username: string;
  CanChangeUsername: boolean;
  UsernameChangeAvailableAtUtc?: string | null;
  Name?: string | null;
  Avatar?: string | null;
  Usertype: string;
  Phone?: string | null;
  Address?: string | null;
  HasLocalPassword: boolean;
  GoogleLinked: boolean;
  MicrosoftLinked: boolean;
  CreatedAtUtc: string;
  UpdatedAtUtc: string;
}

/** The OTP challenge handle returned when an email change is requested. */
export interface EmailChangeChallenge {
  Challenge: string;
  ExpiresAtUtc: string;
}

export interface PendingEmailChange {
  NewEmail: string;
  ExpiresAtUtc: string;
}

export interface PublicProfile {
  Username: string;
  Name?: string | null;
  Avatar?: string | null;
  Usertype: string;
  CreatedAtUtc: string;
}

@Injectable({ providedIn: 'root' })
export class ProfileService {
  private readonly baseUrl = `${environment.backendUrl}/profile`;

  constructor(
    private api: ApiClient,
    private authToken: AuthTokenService,
  ) {}

  getMyProfile(): Observable<MyProfile> {
    return this.getWithCsrf<ApiEnvelope<MyProfile>>(this.baseUrl).pipe(
      map((res) => requireEnvelopeData(res, 'Profile response was incomplete.')),
    );
  }

  getPublicProfile(username: string): Observable<PublicProfile> {
    return this.api
      .get<ApiEnvelope<PublicProfile>>(`${this.baseUrl}/${encodeURIComponent(username)}`)
      .pipe(map((res) => requireEnvelopeData(res, 'Profile response was incomplete.')));
  }

  updateProfile(payload: UpdateProfilePayload): Observable<MyProfile> {
    return this.patchWithCsrf<ApiEnvelope<MyProfile>>(this.baseUrl, payload).pipe(
      map((res) => requireEnvelopeData(res, 'Profile update response was incomplete.')),
    );
  }

  changeUsername(username: string): Observable<MyProfile> {
    return this.patchWithCsrf<ApiEnvelope<MyProfile>>(`${this.baseUrl}/username`, {
      username,
    }).pipe(map((res) => requireEnvelopeData(res, 'Username change response was incomplete.')));
  }

  uploadAvatar(file: File): Observable<MyProfile> {
    const formData = new FormData();
    formData.append('image', file);
    return this.postWithCsrf<ApiEnvelope<MyProfile>>(`${this.baseUrl}/avatar`, formData).pipe(
      map((res) => requireEnvelopeData(res, 'Avatar upload response was incomplete.')),
    );
  }

  changePassword(currentPassword: string, newPassword: string): Observable<void> {
    return this.postWithCsrf<void>(`${this.baseUrl}/change-password`, {
      currentPassword,
      newPassword,
    });
  }

  requestEmailChange(newEmail: string, currentPassword?: string): Observable<EmailChangeChallenge> {
    return this.postWithCsrf<ApiEnvelope<EmailChangeChallenge>>(`${this.baseUrl}/email`, {
      newEmail,
      currentPassword,
    }).pipe(map((res) => requireEnvelopeData(res, 'Email change response was incomplete.')));
  }

  /** Resolves to null when nothing is awaiting confirmation. */
  getPendingEmailChange(): Observable<PendingEmailChange | null> {
    return this.getWithCsrf<ApiEnvelope<PendingEmailChange>>(`${this.baseUrl}/email/pending`).pipe(
      map((res) => extractEnvelopeData(res) ?? null),
    );
  }

  cancelEmailChange(): Observable<void> {
    return from(this.authToken.ensureCsrfToken()).pipe(
      switchMap(() =>
        this.api.delete<void>(`${this.baseUrl}/email/pending`, {
          withCredentials: true,
        }),
      ),
    );
  }

  deleteAccount(): Observable<void> {
    return from(this.authToken.ensureCsrfToken()).pipe(
      switchMap(() =>
        this.api.delete<void>(this.baseUrl, {
          withCredentials: true,
        }),
      ),
    );
  }

  private getWithCsrf<T>(url: string): Observable<T> {
    return from(this.authToken.ensureCsrfToken()).pipe(
      switchMap(() =>
        this.api.get<T>(url, {
          withCredentials: true,
        }),
      ),
    );
  }

  private patchWithCsrf<T>(url: string, body: unknown): Observable<T> {
    return from(this.authToken.ensureCsrfToken()).pipe(
      switchMap(() =>
        this.api.patch<T>(url, body, {
          withCredentials: true,
        }),
      ),
    );
  }

  private postWithCsrf<T>(url: string, body: unknown): Observable<T> {
    return from(this.authToken.ensureCsrfToken()).pipe(
      switchMap(() =>
        this.api.post<T>(url, body, {
          withCredentials: true,
        }),
      ),
    );
  }
}
