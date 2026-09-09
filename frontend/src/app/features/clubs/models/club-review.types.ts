import { ApiEnvelope } from '../../../core/api/models/api-envelope.model';

export interface ClubReview {
  id: number;
  userId: number;
  clubId: number;
  title: string;
  rating: number;
  comment: string | null;
  createdAt: string;
  name: string | null;
  username: string | null;
  /** The username as its owner wrote it. Render this; link by `username`. */
  usernameDisplay: string | null;
  avatar: string | null;
}

export interface ClubReviewsPagedData {
  items: ClubReview[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export type ClubReviewsApiResponse = ApiEnvelope<ClubReviewsPagedData>;

type ClubReviewPayload = Partial<ClubReview> & {
  Id?: number;
  UserId?: number;
  ClubId?: number;
  Title?: string;
  Rating?: number;
  Comment?: string | null;
  CreatedAt?: string;
  Name?: string | null;
  Username?: string | null;
  usernameDisplay?: string | null;
  UsernameDisplay?: string | null;
  Avatar?: string | null;
};

export function normalizeClubReview(raw: ClubReviewPayload): ClubReview {
  return {
    id: raw.id ?? raw.Id ?? 0,
    userId: raw.userId ?? raw.UserId ?? 0,
    clubId: raw.clubId ?? raw.ClubId ?? 0,
    title: raw.title ?? raw.Title ?? '',
    rating: raw.rating ?? raw.Rating ?? 0,
    comment: raw.comment ?? raw.Comment ?? null,
    createdAt: raw.createdAt ?? raw.CreatedAt ?? '',
    name: raw.name ?? raw.Name ?? null,
    username: raw.username ?? raw.Username ?? null,
    usernameDisplay:
      raw.usernameDisplay ?? raw.UsernameDisplay ?? raw.username ?? raw.Username ?? null,
    avatar: raw.avatar ?? raw.Avatar ?? null,
  };
}

export function normalizeClubReviewsPagedData(raw: {
  items?: ClubReviewPayload[];
  Items?: ClubReviewPayload[];
  totalCount?: number;
  TotalCount?: number;
  page?: number;
  Page?: number;
  pageSize?: number;
  PageSize?: number;
  totalPages?: number;
  TotalPages?: number;
}): ClubReviewsPagedData {
  return {
    items: (raw.items ?? raw.Items ?? []).map(normalizeClubReview),
    totalCount: raw.totalCount ?? raw.TotalCount ?? 0,
    page: raw.page ?? raw.Page ?? 1,
    pageSize: raw.pageSize ?? raw.PageSize ?? 20,
    totalPages: raw.totalPages ?? raw.TotalPages ?? 0,
  };
}
