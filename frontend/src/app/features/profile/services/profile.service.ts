import { Injectable } from '@angular/core';
import { Observable, from, map, switchMap } from 'rxjs';

import {
  ApiEnvelope,
  extractEnvelopeData,
  requireEnvelopeData,
} from '../../../core/api/models/api-envelope.model';
import {
  asRecord,
  readBoolean,
  readNullableString,
  readNumber,
  readString,
} from '../../../core/models/payload-casing';
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
  /** The username as the owner wrote it. Render this; link and resolve by `Username`. */
  UsernameDisplay: string;
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
  /** The username as the owner wrote it. Render this; link and resolve by `Username`. */
  UsernameDisplay: string;
  Name?: string | null;
  Avatar?: string | null;
  Usertype: string;
  CreatedAtUtc: string;
}

/**
 * The API serialises camelCase while these interfaces are declared PascalCase, so a raw cast reads
 * every field as `undefined`. Normalising at the service boundary — the same thing
 * `normalizeCurrentUserResponse` does for session payloads — is what makes the declared shape true.
 *
 * `UsernameDisplay` falls back to `Username`, so an account created before the display column, or a
 * response from an older server, renders exactly what it renders today.
 */
function normalizeMyProfile(value: unknown): MyProfile | null {
  const source = asRecord(value);
  if (!source) {
    return null;
  }

  const id = readNumber(source, 'Id', 'id');
  const email = readString(source, 'Email', 'email');
  const username = readString(source, 'Username', 'username');
  const usertype = readString(source, 'Usertype', 'usertype');

  if (id === undefined || email === undefined || username === undefined || usertype === undefined) {
    return null;
  }

  return {
    Id: id,
    Email: email,
    Username: username,
    UsernameDisplay: readString(source, 'UsernameDisplay', 'usernameDisplay') || username,
    CanChangeUsername: readBoolean(source, 'CanChangeUsername', 'canChangeUsername') ?? false,
    UsernameChangeAvailableAtUtc: readNullableString(
      source,
      'UsernameChangeAvailableAtUtc',
      'usernameChangeAvailableAtUtc',
    ),
    Name: readNullableString(source, 'Name', 'name'),
    Avatar: readNullableString(source, 'Avatar', 'avatar'),
    Usertype: usertype,
    Phone: readNullableString(source, 'Phone', 'phone'),
    Address: readNullableString(source, 'Address', 'address'),
    HasLocalPassword: readBoolean(source, 'HasLocalPassword', 'hasLocalPassword') ?? false,
    GoogleLinked: readBoolean(source, 'GoogleLinked', 'googleLinked') ?? false,
    MicrosoftLinked: readBoolean(source, 'MicrosoftLinked', 'microsoftLinked') ?? false,
    CreatedAtUtc: readString(source, 'CreatedAtUtc', 'createdAtUtc') ?? '',
    UpdatedAtUtc: readString(source, 'UpdatedAtUtc', 'updatedAtUtc') ?? '',
  };
}

function normalizePublicProfile(value: unknown): PublicProfile | null {
  const source = asRecord(value);
  if (!source) {
    return null;
  }

  const username = readString(source, 'Username', 'username');
  const usertype = readString(source, 'Usertype', 'usertype');
  if (username === undefined || usertype === undefined) {
    return null;
  }

  return {
    Username: username,
    UsernameDisplay: readString(source, 'UsernameDisplay', 'usernameDisplay') || username,
    Name: readNullableString(source, 'Name', 'name'),
    Avatar: readNullableString(source, 'Avatar', 'avatar'),
    Usertype: usertype,
    CreatedAtUtc: readString(source, 'CreatedAtUtc', 'createdAtUtc') ?? '',
  };
}

function normalizeEmailChangeChallenge(value: unknown): EmailChangeChallenge | null {
  const source = asRecord(value);
  if (!source) {
    return null;
  }

  const challenge = readString(source, 'Challenge', 'challenge');
  if (challenge === undefined) {
    return null;
  }

  return {
    Challenge: challenge,
    ExpiresAtUtc: readString(source, 'ExpiresAtUtc', 'expiresAtUtc') ?? '',
  };
}

function normalizePendingEmailChange(value: unknown): PendingEmailChange | null {
  const source = asRecord(value);
  if (!source) {
    return null;
  }

  const newEmail = readString(source, 'NewEmail', 'newEmail');
  if (newEmail === undefined) {
    return null;
  }

  return {
    NewEmail: newEmail,
    ExpiresAtUtc: readString(source, 'ExpiresAtUtc', 'expiresAtUtc') ?? '',
  };
}

function requireProfile<T>(value: T | null, message: string): T {
  if (value === null) {
    throw new Error(message);
  }

  return value;
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
      map((res) =>
        requireProfile(
          normalizeMyProfile(requireEnvelopeData(res, 'Profile response was incomplete.')),
          'Profile response was incomplete.',
        ),
      ),
    );
  }

  getPublicProfile(username: string): Observable<PublicProfile> {
    return this.api
      .get<ApiEnvelope<PublicProfile>>(`${this.baseUrl}/${encodeURIComponent(username)}`)
      .pipe(
        map((res) =>
          requireProfile(
            normalizePublicProfile(requireEnvelopeData(res, 'Profile response was incomplete.')),
            'Profile response was incomplete.',
          ),
        ),
      );
  }

  updateProfile(payload: UpdateProfilePayload): Observable<MyProfile> {
    return this.patchWithCsrf<ApiEnvelope<MyProfile>>(this.baseUrl, payload).pipe(
      map((res) =>
        requireProfile(
          normalizeMyProfile(requireEnvelopeData(res, 'Profile update response was incomplete.')),
          'Profile update response was incomplete.',
        ),
      ),
    );
  }

  changeUsername(username: string): Observable<MyProfile> {
    return this.patchWithCsrf<ApiEnvelope<MyProfile>>(`${this.baseUrl}/username`, {
      username,
    }).pipe(
      map((res) =>
        requireProfile(
          normalizeMyProfile(requireEnvelopeData(res, 'Username change response was incomplete.')),
          'Username change response was incomplete.',
        ),
      ),
    );
  }

  uploadAvatar(file: File): Observable<MyProfile> {
    const formData = new FormData();
    formData.append('image', file);
    return this.postWithCsrf<ApiEnvelope<MyProfile>>(`${this.baseUrl}/avatar`, formData).pipe(
      map((res) =>
        requireProfile(
          normalizeMyProfile(requireEnvelopeData(res, 'Avatar upload response was incomplete.')),
          'Avatar upload response was incomplete.',
        ),
      ),
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
    }).pipe(
      map((res) =>
        requireProfile(
          normalizeEmailChangeChallenge(
            requireEnvelopeData(res, 'Email change response was incomplete.'),
          ),
          'Email change response was incomplete.',
        ),
      ),
    );
  }

  /** Resolves to null when nothing is awaiting confirmation. */
  getPendingEmailChange(): Observable<PendingEmailChange | null> {
    return this.getWithCsrf<ApiEnvelope<PendingEmailChange>>(`${this.baseUrl}/email/pending`).pipe(
      // Null is a normal answer here — nothing is awaiting confirmation — so a payload that does
      // not normalise reads the same way rather than throwing.
      map((res) => normalizePendingEmailChange(extractEnvelopeData(res))),
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
