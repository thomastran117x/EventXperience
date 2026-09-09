import {
  UnknownRecord,
  asRecord,
  readNullableString,
  readNumber,
  readString,
} from './payload-casing';

export interface AuthenticatedSessionResponse {
  AccessToken: string;
  ExpiresAtUtc: string;
  RefreshToken?: string;
  SessionBindingToken?: string;
  ReturnPath?: string | null;
}

export interface CurrentUserResponse {
  Id: number;
  Email: string;
  Username: string;
  /** The username as its owner wrote it. Render this; resolve and link by `Username`. */
  UsernameDisplay: string;
  Name?: string | null;
  Avatar?: string | null;
  Usertype: string;
}

export function normalizeAuthenticatedSessionResponse(
  value: unknown,
): AuthenticatedSessionResponse | null {
  const source = asRecord(value);
  if (!source) {
    return null;
  }

  const accessToken = readString(source, 'AccessToken', 'accessToken');
  const expiresAtUtc = readString(source, 'ExpiresAtUtc', 'expiresAtUtc');

  if (!accessToken || !expiresAtUtc) {
    return null;
  }

  const refreshToken = readString(source, 'RefreshToken', 'refreshToken');
  const sessionBindingToken = readString(source, 'SessionBindingToken', 'sessionBindingToken');
  const returnPath = readNullableString(source, 'ReturnPath', 'returnPath');

  return {
    AccessToken: accessToken,
    ExpiresAtUtc: expiresAtUtc,
    ...(refreshToken ? { RefreshToken: refreshToken } : {}),
    ...(sessionBindingToken ? { SessionBindingToken: sessionBindingToken } : {}),
    ...(returnPath !== undefined ? { ReturnPath: returnPath } : {}),
  };
}

export function normalizeCurrentUserResponse(value: unknown): CurrentUserResponse | null {
  const source = asRecord(value);
  if (!source) {
    return null;
  }

  const id = readNumber(source, 'Id', 'id');
  const email = readString(source, 'Email', 'email');
  const username = readString(source, 'Username', 'username');
  const usertype = readString(source, 'Usertype', 'usertype');

  if (id === undefined || !email || !username || !usertype) {
    return null;
  }

  // Accounts created before the display column, and any payload from an older server, carry no
  // display form; the lookup key is the correct fallback and is what used to be rendered.
  const usernameDisplay =
    readNullableString(source, 'UsernameDisplay', 'usernameDisplay') || username;
  const name = readNullableString(source, 'Name', 'name');
  const avatar = readNullableString(source, 'Avatar', 'avatar');

  return {
    Id: id,
    Email: email,
    Username: username,
    UsernameDisplay: usernameDisplay,
    Name: name ?? undefined,
    Avatar: avatar ?? undefined,
    Usertype: usertype,
  };
}
