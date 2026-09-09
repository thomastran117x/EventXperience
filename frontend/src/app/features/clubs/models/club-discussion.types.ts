import { ApiEnvelope } from '../../../core/api/models/api-envelope.model';
import { AuthorInfo, normalizeAuthor } from './club-post.types';

export interface ClubDiscussion {
  id: number;
  clubId: number;
  userId: number;
  title: string;
  description: string;
  author: AuthorInfo | null;
  createdAt: string;
  updatedAt: string;
  replyCount: number;
}

export type DiscussionReplySort = 'Newest' | 'Oldest';
export type DiscussionReplyReaction = 'Like' | 'Dislike';

export interface DiscussionReply {
  id: number;
  discussionId: number;
  parentReplyId: number | null;
  userId: number | null;
  content: string | null;
  author: AuthorInfo | null;
  isDeleted: boolean;
  createdAt: string;
  updatedAt: string;
  likeCount: number;
  dislikeCount: number;
  currentUserReaction: DiscussionReplyReaction | null;
  directReplyCount: number;
}

export interface DiscussionReplyPageData {
  items: DiscussionReply[];
  totalCount: number;
  nextCursor: string | null;
  hasMore: boolean;
}

export interface DiscussionReplyReactionData {
  replyId: number;
  likeCount: number;
  dislikeCount: number;
  currentUserReaction: DiscussionReplyReaction | null;
}

export type DiscussionReplyApiResponse = ApiEnvelope<DiscussionReply>;
export type DiscussionReplyPageApiResponse = ApiEnvelope<DiscussionReplyPageData>;
export type DiscussionReplyReactionApiResponse = ApiEnvelope<DiscussionReplyReactionData>;

export interface ClubDiscussionsPagedData {
  items: ClubDiscussion[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export type ClubDiscussionsApiResponse = ApiEnvelope<ClubDiscussionsPagedData>;
export type ClubDiscussionApiResponse = ApiEnvelope<ClubDiscussion>;

// Raw payload types to handle PascalCase from backend
type AuthorInfoPayload = Partial<AuthorInfo> & {
  Id?: number;
  Name?: string | null;
  Username?: string | null;
  UsernameDisplay?: string | null;
  Avatar?: string | null;
};

type ClubDiscussionPayload = Partial<ClubDiscussion> & {
  Id?: number;
  ClubId?: number;
  UserId?: number;
  Title?: string;
  Description?: string;
  Author?: AuthorInfoPayload | null;
  CreatedAt?: string;
  UpdatedAt?: string;
  ReplyCount?: number;
};

type DiscussionReplyPayload = Partial<DiscussionReply> & {
  Id?: number;
  DiscussionId?: number;
  ParentReplyId?: number | null;
  UserId?: number | null;
  Content?: string | null;
  Author?: AuthorInfoPayload | null;
  IsDeleted?: boolean;
  CreatedAt?: string;
  UpdatedAt?: string;
  LikeCount?: number;
  DislikeCount?: number;
  CurrentUserReaction?: DiscussionReplyReaction | null;
  DirectReplyCount?: number;
};

type DiscussionReplyPagePayload = {
  items?: DiscussionReplyPayload[];
  Items?: DiscussionReplyPayload[];
  totalCount?: number;
  TotalCount?: number;
  nextCursor?: string | null;
  NextCursor?: string | null;
  hasMore?: boolean;
  HasMore?: boolean;
};

type DiscussionReplyReactionPayload = Partial<DiscussionReplyReactionData> & {
  ReplyId?: number;
  LikeCount?: number;
  DislikeCount?: number;
  CurrentUserReaction?: DiscussionReplyReaction | null;
};

type PagedPayload<T> = {
  items?: T[];
  Items?: T[];
  totalCount?: number;
  TotalCount?: number;
  page?: number;
  Page?: number;
  pageSize?: number;
  PageSize?: number;
  totalPages?: number;
  TotalPages?: number;
};

export function normalizeClubDiscussion(raw: ClubDiscussionPayload): ClubDiscussion {
  return {
    id: raw.id ?? raw.Id ?? 0,
    clubId: raw.clubId ?? raw.ClubId ?? 0,
    userId: raw.userId ?? raw.UserId ?? 0,
    title: raw.title ?? raw.Title ?? '',
    description: raw.description ?? raw.Description ?? '',
    author: normalizeAuthor(raw.author ?? raw.Author),
    createdAt: raw.createdAt ?? raw.CreatedAt ?? '',
    updatedAt: raw.updatedAt ?? raw.UpdatedAt ?? '',
    replyCount: raw.replyCount ?? raw.ReplyCount ?? 0,
  };
}

export function normalizeDiscussionReply(raw: DiscussionReplyPayload): DiscussionReply {
  const reaction = raw.currentUserReaction ?? raw.CurrentUserReaction ?? null;
  return {
    id: raw.id ?? raw.Id ?? 0,
    discussionId: raw.discussionId ?? raw.DiscussionId ?? 0,
    parentReplyId: raw.parentReplyId ?? raw.ParentReplyId ?? null,
    userId: raw.userId ?? raw.UserId ?? null,
    content: raw.content ?? raw.Content ?? null,
    author: normalizeAuthor(raw.author ?? raw.Author),
    isDeleted: raw.isDeleted ?? raw.IsDeleted ?? false,
    createdAt: raw.createdAt ?? raw.CreatedAt ?? '',
    updatedAt: raw.updatedAt ?? raw.UpdatedAt ?? '',
    likeCount: raw.likeCount ?? raw.LikeCount ?? 0,
    dislikeCount: raw.dislikeCount ?? raw.DislikeCount ?? 0,
    currentUserReaction: reaction === 'Like' || reaction === 'Dislike' ? reaction : null,
    directReplyCount: raw.directReplyCount ?? raw.DirectReplyCount ?? 0,
  };
}

export function normalizeDiscussionReplyPage(
  raw: DiscussionReplyPagePayload,
): DiscussionReplyPageData {
  return {
    items: (raw.items ?? raw.Items ?? []).map(normalizeDiscussionReply),
    totalCount: raw.totalCount ?? raw.TotalCount ?? 0,
    nextCursor: raw.nextCursor ?? raw.NextCursor ?? null,
    hasMore: raw.hasMore ?? raw.HasMore ?? false,
  };
}

export function normalizeDiscussionReplyReaction(
  raw: DiscussionReplyReactionPayload,
): DiscussionReplyReactionData {
  const reaction = raw.currentUserReaction ?? raw.CurrentUserReaction ?? null;
  return {
    replyId: raw.replyId ?? raw.ReplyId ?? 0,
    likeCount: raw.likeCount ?? raw.LikeCount ?? 0,
    dislikeCount: raw.dislikeCount ?? raw.DislikeCount ?? 0,
    currentUserReaction: reaction === 'Like' || reaction === 'Dislike' ? reaction : null,
  };
}

export function normalizeClubDiscussionsPagedData(
  raw: PagedPayload<ClubDiscussionPayload>,
): ClubDiscussionsPagedData {
  return {
    items: (raw.items ?? raw.Items ?? []).map(normalizeClubDiscussion),
    totalCount: raw.totalCount ?? raw.TotalCount ?? 0,
    page: raw.page ?? raw.Page ?? 1,
    pageSize: raw.pageSize ?? raw.PageSize ?? 20,
    totalPages: raw.totalPages ?? raw.TotalPages ?? 0,
  };
}

/** Display name for a discussion's author, falling back through name → username → user id. */
export function discussionAuthorName(discussion: ClubDiscussion): string {
  return (
    discussion.author?.name ??
    discussion.author?.usernameDisplay ??
    discussion.author?.username ??
    `User #${discussion.userId}`
  );
}
