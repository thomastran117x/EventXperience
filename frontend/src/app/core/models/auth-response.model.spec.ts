import {
  normalizeAuthenticatedSessionResponse,
  normalizeCurrentUserResponse,
} from './auth-response.model';

describe('normalizeAuthenticatedSessionResponse', () => {
  it('reads PascalCase payloads', () => {
    expect(
      normalizeAuthenticatedSessionResponse({
        AccessToken: 'token',
        ExpiresAtUtc: '2026-01-01T00:00:00Z',
      }),
    ).toEqual({ AccessToken: 'token', ExpiresAtUtc: '2026-01-01T00:00:00Z' });
  });

  it('reads camelCase payloads', () => {
    expect(
      normalizeAuthenticatedSessionResponse({
        accessToken: 'token',
        expiresAtUtc: '2026-01-01T00:00:00Z',
      }),
    ).toEqual({ AccessToken: 'token', ExpiresAtUtc: '2026-01-01T00:00:00Z' });
  });

  it('carries optional fields through when present', () => {
    expect(
      normalizeAuthenticatedSessionResponse({
        accessToken: 'token',
        expiresAtUtc: '2026-01-01T00:00:00Z',
        refreshToken: 'refresh',
        sessionBindingToken: 'binding',
        returnPath: '/events/1',
      }),
    ).toEqual({
      AccessToken: 'token',
      ExpiresAtUtc: '2026-01-01T00:00:00Z',
      RefreshToken: 'refresh',
      SessionBindingToken: 'binding',
      ReturnPath: '/events/1',
    });
  });

  it('omits absent optional keys rather than emitting undefined', () => {
    const result = normalizeAuthenticatedSessionResponse({
      AccessToken: 'token',
      ExpiresAtUtc: '2026-01-01T00:00:00Z',
    });

    expect(Object.keys(result ?? {})).toEqual(['AccessToken', 'ExpiresAtUtc']);
  });

  it('preserves an explicitly null ReturnPath', () => {
    const result = normalizeAuthenticatedSessionResponse({
      AccessToken: 'token',
      ExpiresAtUtc: '2026-01-01T00:00:00Z',
      ReturnPath: null,
    });

    expect(result?.ReturnPath).toBeNull();
  });

  it('returns null when the access token is missing', () => {
    expect(
      normalizeAuthenticatedSessionResponse({ ExpiresAtUtc: '2026-01-01T00:00:00Z' }),
    ).toBeNull();
  });

  it('returns null when the expiry is missing', () => {
    expect(normalizeAuthenticatedSessionResponse({ AccessToken: 'token' })).toBeNull();
  });

  it('returns null for non-object input', () => {
    expect(normalizeAuthenticatedSessionResponse(null)).toBeNull();
    expect(normalizeAuthenticatedSessionResponse('token')).toBeNull();
    expect(normalizeAuthenticatedSessionResponse(undefined)).toBeNull();
  });
});

describe('normalizeCurrentUserResponse', () => {
  const pascalUser = {
    Id: 7,
    Email: 'member@example.com',
    Username: 'member',
    UsernameDisplay: 'member',
    Usertype: 'User',
  };

  it('reads PascalCase payloads', () => {
    expect(normalizeCurrentUserResponse(pascalUser)).toEqual({
      Id: 7,
      Email: 'member@example.com',
      Username: 'member',
      UsernameDisplay: 'member',
      Name: undefined,
      Avatar: undefined,
      Usertype: 'User',
    });
  });

  it('reads camelCase payloads', () => {
    expect(
      normalizeCurrentUserResponse({
        id: 7,
        email: 'member@example.com',
        username: 'member',
        usertype: 'User',
        name: 'Test Member',
        avatar: 'https://example.com/a.png',
      }),
    ).toEqual({
      Id: 7,
      Email: 'member@example.com',
      Username: 'member',
      UsernameDisplay: 'member',
      Name: 'Test Member',
      Avatar: 'https://example.com/a.png',
      Usertype: 'User',
    });
  });

  it('coerces a numeric-string id', () => {
    expect(normalizeCurrentUserResponse({ ...pascalUser, Id: '7' })?.Id).toBe(7);
  });

  it('rejects a non-numeric id', () => {
    expect(normalizeCurrentUserResponse({ ...pascalUser, Id: 'seven' })).toBeNull();
  });

  it('normalizes a null Name to undefined', () => {
    expect(normalizeCurrentUserResponse({ ...pascalUser, Name: null })?.Name).toBeUndefined();
  });

  it('returns null when a required field is missing', () => {
    expect(normalizeCurrentUserResponse({ ...pascalUser, Email: undefined })).toBeNull();
    expect(normalizeCurrentUserResponse({ ...pascalUser, Username: undefined })).toBeNull();
    expect(normalizeCurrentUserResponse({ ...pascalUser, Usertype: undefined })).toBeNull();
  });

  it('accepts id 0 rather than treating it as missing', () => {
    expect(normalizeCurrentUserResponse({ ...pascalUser, Id: 0 })?.Id).toBe(0);
  });

  it('returns null for non-object input', () => {
    expect(normalizeCurrentUserResponse(null)).toBeNull();
    expect(normalizeCurrentUserResponse(42)).toBeNull();
  });
});
