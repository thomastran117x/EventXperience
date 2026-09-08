import { ApiEnvelope } from '../../../core/api/models/api-envelope.model';
import { Club, ClubType, normalizeClub } from './club.types';

// ---------------------------------------------------------------------------
// Create / update payloads
// ---------------------------------------------------------------------------

export interface ClubMutationPayload {
  name: string;
  description: string;
  // Backend accepts lowercase aliases (sport/academic/social/cultural/gaming/other).
  clubtype: string;
  clubImageUrl: string;
  bannerImageUrl?: string | null;
  galleryImageUrls?: string[];
  phone?: string | null;
  email?: string | null;
  websiteUrl?: string | null;
  location?: string | null;
  maxMemberCount?: number | null;
  isPrivate?: boolean;
}

export function toClubtypeAlias(type: ClubType): string {
  return type.toLowerCase();
}

// ---------------------------------------------------------------------------
// Staff
// ---------------------------------------------------------------------------

export type ClubStaffRole = 'Manager' | 'Volunteer';

export interface ClubStaff {
  id: number;
  clubId: number;
  userId: number;
  role: ClubStaffRole;
  grantedByUserId: number;
  createdAt: string;
  updatedAt: string;
  name: string | null;
  username: string | null;
  /** The username as its owner wrote it. Render this; link by `username`. */
  usernameDisplay: string | null;
  avatar: string | null;
}

type ClubStaffPayload = Partial<ClubStaff> & {
  Id?: number;
  ClubId?: number;
  UserId?: number;
  Role?: string;
  GrantedByUserId?: number;
  CreatedAt?: string;
  UpdatedAt?: string;
  Name?: string | null;
  Username?: string | null;
  usernameDisplay?: string | null;
  UsernameDisplay?: string | null;
  Avatar?: string | null;
};

function normalizeStaffRole(value: string | undefined): ClubStaffRole {
  return value === 'Volunteer' ? 'Volunteer' : 'Manager';
}

export function normalizeClubStaff(raw: ClubStaffPayload): ClubStaff {
  return {
    id: raw.id ?? raw.Id ?? 0,
    clubId: raw.clubId ?? raw.ClubId ?? 0,
    userId: raw.userId ?? raw.UserId ?? 0,
    role: normalizeStaffRole((raw.role ?? raw.Role) as string | undefined),
    grantedByUserId: raw.grantedByUserId ?? raw.GrantedByUserId ?? 0,
    createdAt: raw.createdAt ?? raw.CreatedAt ?? '',
    updatedAt: raw.updatedAt ?? raw.UpdatedAt ?? '',
    name: raw.name ?? raw.Name ?? null,
    username: raw.username ?? raw.Username ?? null,
    usernameDisplay:
      raw.usernameDisplay ?? raw.UsernameDisplay ?? raw.username ?? raw.Username ?? null,
    avatar: raw.avatar ?? raw.Avatar ?? null,
  };
}

// ---------------------------------------------------------------------------
// Members (follow records)
// ---------------------------------------------------------------------------

export interface ClubMember {
  id: number;
  userId: number;
  clubId: number;
  createdAt: string;
  name: string | null;
  username: string | null;
  /** The username as its owner wrote it. Render this; link by `username`. */
  usernameDisplay: string | null;
  avatar: string | null;
}

type ClubMemberPayload = Partial<ClubMember> & {
  Id?: number;
  UserId?: number;
  ClubId?: number;
  CreatedAt?: string;
  Name?: string | null;
  Username?: string | null;
  usernameDisplay?: string | null;
  UsernameDisplay?: string | null;
  Avatar?: string | null;
};

export function normalizeClubMember(raw: ClubMemberPayload): ClubMember {
  return {
    id: raw.id ?? raw.Id ?? 0,
    userId: raw.userId ?? raw.UserId ?? 0,
    clubId: raw.clubId ?? raw.ClubId ?? 0,
    createdAt: raw.createdAt ?? raw.CreatedAt ?? '',
    name: raw.name ?? raw.Name ?? null,
    username: raw.username ?? raw.Username ?? null,
    usernameDisplay:
      raw.usernameDisplay ?? raw.UsernameDisplay ?? raw.username ?? raw.Username ?? null,
    avatar: raw.avatar ?? raw.Avatar ?? null,
  };
}

export interface ClubMembersPagedData {
  items: ClubMember[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export function normalizeClubMembersPagedData(raw: {
  items?: ClubMemberPayload[];
  Items?: ClubMemberPayload[];
  totalCount?: number;
  TotalCount?: number;
  page?: number;
  Page?: number;
  pageSize?: number;
  PageSize?: number;
  totalPages?: number;
  TotalPages?: number;
}): ClubMembersPagedData {
  return {
    items: (raw.items ?? raw.Items ?? []).map(normalizeClubMember),
    totalCount: raw.totalCount ?? raw.TotalCount ?? 0,
    page: raw.page ?? raw.Page ?? 1,
    pageSize: raw.pageSize ?? raw.PageSize ?? 20,
    totalPages: raw.totalPages ?? raw.TotalPages ?? 0,
  };
}

// ---------------------------------------------------------------------------
// Version history
// ---------------------------------------------------------------------------

export interface ClubVersionFieldChange {
  field: string;
  oldValue: string | null;
  newValue: string | null;
}

export interface ClubVersionSnapshot {
  name: string;
  description: string;
  clubtype: string;
  clubImage: string;
  phone: string | null;
  email: string | null;
  websiteUrl: string | null;
  location: string | null;
  maxMemberCount: number;
  isPrivate: boolean;
}

export interface ClubVersionListItem {
  versionNumber: number;
  actionType: string;
  createdAt: string;
  actorUserId: number;
  actorRole: string;
  rollbackEligible: boolean;
  rollbackExpiresAt: string;
  rollbackSourceVersionNumber: number | null;
  changedFields: ClubVersionFieldChange[];
  actorName: string | null;
  actorUsername: string | null;
  actorAvatar: string | null;
}

export interface ClubVersionDetail extends ClubVersionListItem {
  snapshot: ClubVersionSnapshot;
}

export interface ClubVersionsPagedData {
  items: ClubVersionListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

type FieldChangePayload = Partial<ClubVersionFieldChange> & {
  Field?: string;
  OldValue?: string | null;
  NewValue?: string | null;
};

type SnapshotPayload = Partial<ClubVersionSnapshot> & {
  Name?: string;
  Description?: string;
  Clubtype?: string;
  ClubImage?: string;
  Phone?: string | null;
  Email?: string | null;
  WebsiteUrl?: string | null;
  Location?: string | null;
  MaxMemberCount?: number;
  IsPrivate?: boolean;
};

type VersionListItemPayload = Partial<ClubVersionListItem> & {
  VersionNumber?: number;
  ActionType?: string;
  CreatedAt?: string;
  ActorUserId?: number;
  ActorRole?: string;
  RollbackEligible?: boolean;
  RollbackExpiresAt?: string;
  RollbackSourceVersionNumber?: number | null;
  ChangedFields?: FieldChangePayload[];
  changedFields?: FieldChangePayload[];
  ActorName?: string | null;
  ActorUsername?: string | null;
  ActorAvatar?: string | null;
};

type VersionDetailPayload = VersionListItemPayload & {
  Snapshot?: SnapshotPayload;
  snapshot?: SnapshotPayload;
};

function normalizeFieldChange(raw: FieldChangePayload): ClubVersionFieldChange {
  return {
    field: raw.field ?? raw.Field ?? '',
    oldValue: raw.oldValue ?? raw.OldValue ?? null,
    newValue: raw.newValue ?? raw.NewValue ?? null,
  };
}

export function normalizeClubVersionListItem(raw: VersionListItemPayload): ClubVersionListItem {
  return {
    versionNumber: raw.versionNumber ?? raw.VersionNumber ?? 0,
    actionType: raw.actionType ?? raw.ActionType ?? '',
    createdAt: raw.createdAt ?? raw.CreatedAt ?? '',
    actorUserId: raw.actorUserId ?? raw.ActorUserId ?? 0,
    actorRole: raw.actorRole ?? raw.ActorRole ?? '',
    rollbackEligible: raw.rollbackEligible ?? raw.RollbackEligible ?? false,
    rollbackExpiresAt: raw.rollbackExpiresAt ?? raw.RollbackExpiresAt ?? '',
    rollbackSourceVersionNumber:
      raw.rollbackSourceVersionNumber ?? raw.RollbackSourceVersionNumber ?? null,
    changedFields: (raw.changedFields ?? raw.ChangedFields ?? []).map(normalizeFieldChange),
    actorName: raw.actorName ?? raw.ActorName ?? null,
    actorUsername: raw.actorUsername ?? raw.ActorUsername ?? null,
    actorAvatar: raw.actorAvatar ?? raw.ActorAvatar ?? null,
  };
}

function normalizeSnapshot(raw: SnapshotPayload): ClubVersionSnapshot {
  return {
    name: raw.name ?? raw.Name ?? '',
    description: raw.description ?? raw.Description ?? '',
    clubtype: raw.clubtype ?? raw.Clubtype ?? '',
    clubImage: raw.clubImage ?? raw.ClubImage ?? '',
    phone: raw.phone ?? raw.Phone ?? null,
    email: raw.email ?? raw.Email ?? null,
    websiteUrl: raw.websiteUrl ?? raw.WebsiteUrl ?? null,
    location: raw.location ?? raw.Location ?? null,
    maxMemberCount: raw.maxMemberCount ?? raw.MaxMemberCount ?? 0,
    isPrivate: raw.isPrivate ?? raw.IsPrivate ?? false,
  };
}

export function normalizeClubVersionDetail(raw: VersionDetailPayload): ClubVersionDetail {
  return {
    ...normalizeClubVersionListItem(raw),
    snapshot: normalizeSnapshot(raw.snapshot ?? raw.Snapshot ?? {}),
  };
}

export function normalizeClubVersionsPagedData(raw: {
  items?: VersionListItemPayload[];
  Items?: VersionListItemPayload[];
  totalCount?: number;
  TotalCount?: number;
  page?: number;
  Page?: number;
  pageSize?: number;
  PageSize?: number;
  totalPages?: number;
  TotalPages?: number;
}): ClubVersionsPagedData {
  return {
    items: (raw.items ?? raw.Items ?? []).map(normalizeClubVersionListItem),
    totalCount: raw.totalCount ?? raw.TotalCount ?? 0,
    page: raw.page ?? raw.Page ?? 1,
    pageSize: raw.pageSize ?? raw.PageSize ?? 20,
    totalPages: raw.totalPages ?? raw.TotalPages ?? 0,
  };
}

export interface ClubRollback {
  club: Club;
  restoredFromVersionNumber: number;
  newVersionNumber: number;
}

type ClubRollbackPayload = {
  club?: Parameters<typeof normalizeClub>[0];
  Club?: Parameters<typeof normalizeClub>[0];
  restoredFromVersionNumber?: number;
  RestoredFromVersionNumber?: number;
  newVersionNumber?: number;
  NewVersionNumber?: number;
};

export function normalizeClubRollback(raw: ClubRollbackPayload): ClubRollback {
  return {
    club: normalizeClub(raw.club ?? raw.Club ?? {}),
    restoredFromVersionNumber: raw.restoredFromVersionNumber ?? raw.RestoredFromVersionNumber ?? 0,
    newVersionNumber: raw.newVersionNumber ?? raw.NewVersionNumber ?? 0,
  };
}

// ---------------------------------------------------------------------------
// Analytics
// ---------------------------------------------------------------------------

export interface TopEventEntry {
  id: number;
  name: string;
  registrationCount: number;
  fillRate: number;
  revenue: number;
}

export interface TrendPoint {
  date: string;
  value: number;
}

export interface ClubAnalytics {
  clubId: number;
  totalEvents: number;
  draftEvents: number;
  publishedEvents: number;
  pausedEvents: number;
  cancelledEvents: number;
  archivedEvents: number;
  upcomingEvents: number;
  ongoingEvents: number;
  pastEvents: number;
  totalRegistrations: number;
  uniqueAttendees: number;
  repeatAttendees: number;
  totalRevenue: number;
  pendingRevenue: number;
  avgFillRate: number;
  topEventsByRegistrations: TopEventEntry[];
  topEventsByRevenue: TopEventEntry[];
  topEventsByFillRate: TopEventEntry[];
  registrationTrend: TrendPoint[];
  revenueTrend: TrendPoint[];
}

type TopEventPayload = Partial<TopEventEntry> & {
  Id?: number;
  Name?: string;
  RegistrationCount?: number;
  FillRate?: number;
  Revenue?: number;
};

type AnalyticsPayload = {
  [K in keyof ClubAnalytics]?: unknown;
} & Record<string, unknown>;

function num(raw: AnalyticsPayload, camel: string, pascal: string): number {
  return (raw[camel] ?? raw[pascal] ?? 0) as number;
}

function normalizeTopEvent(raw: TopEventPayload): TopEventEntry {
  return {
    id: raw.id ?? raw.Id ?? 0,
    name: raw.name ?? raw.Name ?? '',
    registrationCount: raw.registrationCount ?? raw.RegistrationCount ?? 0,
    fillRate: raw.fillRate ?? raw.FillRate ?? 0,
    revenue: raw.revenue ?? raw.Revenue ?? 0,
  };
}

function topList(raw: AnalyticsPayload, camel: string, pascal: string): TopEventEntry[] {
  const list = (raw[camel] ?? raw[pascal] ?? []) as TopEventPayload[];
  return list.map(normalizeTopEvent);
}

type TrendPayload = {
  date?: string;
  Date?: string;
  count?: number;
  Count?: number;
  amount?: number;
  Amount?: number;
};

function trendList(
  raw: AnalyticsPayload,
  camel: string,
  pascal: string,
  valueCamel: 'count' | 'amount',
  valuePascal: 'Count' | 'Amount',
): TrendPoint[] {
  const list = (raw[camel] ?? raw[pascal] ?? []) as TrendPayload[];
  return list.map((point) => ({
    date: point.date ?? point.Date ?? '',
    value: (point[valueCamel] ?? point[valuePascal] ?? 0) as number,
  }));
}

export function normalizeClubAnalytics(raw: AnalyticsPayload): ClubAnalytics {
  return {
    clubId: num(raw, 'clubId', 'ClubId'),
    totalEvents: num(raw, 'totalEvents', 'TotalEvents'),
    draftEvents: num(raw, 'draftEvents', 'DraftEvents'),
    publishedEvents: num(raw, 'publishedEvents', 'PublishedEvents'),
    pausedEvents: num(raw, 'pausedEvents', 'PausedEvents'),
    cancelledEvents: num(raw, 'cancelledEvents', 'CancelledEvents'),
    archivedEvents: num(raw, 'archivedEvents', 'ArchivedEvents'),
    upcomingEvents: num(raw, 'upcomingEvents', 'UpcomingEvents'),
    ongoingEvents: num(raw, 'ongoingEvents', 'OngoingEvents'),
    pastEvents: num(raw, 'pastEvents', 'PastEvents'),
    totalRegistrations: num(raw, 'totalRegistrations', 'TotalRegistrations'),
    uniqueAttendees: num(raw, 'uniqueAttendees', 'UniqueAttendees'),
    repeatAttendees: num(raw, 'repeatAttendees', 'RepeatAttendees'),
    totalRevenue: num(raw, 'totalRevenue', 'TotalRevenue'),
    pendingRevenue: num(raw, 'pendingRevenue', 'PendingRevenue'),
    avgFillRate: num(raw, 'avgFillRate', 'AvgFillRate'),
    topEventsByRegistrations: topList(raw, 'topEventsByRegistrations', 'TopEventsByRegistrations'),
    topEventsByRevenue: topList(raw, 'topEventsByRevenue', 'TopEventsByRevenue'),
    topEventsByFillRate: topList(raw, 'topEventsByFillRate', 'TopEventsByFillRate'),
    registrationTrend: trendList(raw, 'registrationTrend', 'RegistrationTrend', 'count', 'Count'),
    revenueTrend: trendList(raw, 'revenueTrend', 'RevenueTrend', 'amount', 'Amount'),
  };
}

// ---------------------------------------------------------------------------
// Envelope response aliases
// ---------------------------------------------------------------------------

export type ManagedClubsApiResponse = ApiEnvelope<Club[]>;
export type ClubMembersApiResponse = ApiEnvelope<ClubMembersPagedData>;
export type ClubStaffListApiResponse = ApiEnvelope<ClubStaff[]>;
export type ClubStaffApiResponse = ApiEnvelope<ClubStaff>;
export type ClubVersionsApiResponse = ApiEnvelope<ClubVersionsPagedData>;
export type ClubVersionDetailApiResponse = ApiEnvelope<ClubVersionDetail>;
export type ClubRollbackApiResponse = ApiEnvelope<ClubRollback>;
export type ClubAnalyticsApiResponse = ApiEnvelope<ClubAnalytics>;
