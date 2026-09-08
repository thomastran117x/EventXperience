import { ApiEnvelope } from '../../../core/api/models/api-envelope.model';

export type PostType = 'General' | 'Announcement' | 'Event' | 'Poll';

export const POST_TYPE_LABELS: Record<PostType, string> = {
  General: 'General',
  Announcement: 'Announcement',
  Event: 'Event',
  Poll: 'Poll',
};

export const POST_TYPE_STYLES: Record<PostType, string> = {
  General: 'bg-slate-500/10 text-slate-700 dark:text-slate-300 border-slate-500/20',
  Announcement: 'bg-amber-500/10 text-amber-700 dark:text-amber-300 border-amber-500/20',
  Event: 'bg-blue-500/10 text-blue-700 dark:text-blue-300 border-blue-500/20',
  Poll: 'bg-cyan-500/10 text-cyan-700 dark:text-cyan-300 border-cyan-500/20',
};

export const ALL_POST_SORTS = ['Recent', 'Popular'] as const;
export type PostSortBy = (typeof ALL_POST_SORTS)[number];

export interface AuthorInfo {
  id: number;
  name: string | null;
  username: string | null;
  /** The username as its owner wrote it. Render this; link by `username`. */
  usernameDisplay: string | null;
  avatar: string | null;
}

export interface ClubPost {
  id: number;
  clubId: number;
  userId: number;
  title: string;
  content: string;
  postType: PostType;
  likesCount: number;
  viewCount: number;
  commentCount: number;
  isPinned: boolean;
  author: AuthorInfo | null;
  createdAt: string;
  updatedAt: string;
}

export interface ClubPostsPagedData {
  items: ClubPost[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export type ClubPostsApiResponse = ApiEnvelope<ClubPostsPagedData>;

export interface PostComment {
  id: number;
  postId: number;
  parentCommentId: number | null;
  userId: number | null;
  content: string | null;
  author: AuthorInfo | null;
  isDeleted: boolean;
  createdAt: string;
  updatedAt: string;
  likeCount: number;
  dislikeCount: number;
  currentUserReaction: PostCommentReaction | null;
  directReplyCount: number;
}

export type PostCommentSort = 'Newest' | 'Oldest';
export type PostCommentReaction = 'Like' | 'Dislike';

export interface PostCommentsPageData {
  items: PostComment[];
  totalCount: number;
  nextCursor: string | null;
  hasMore: boolean;
}

export interface PostCommentReactionData {
  commentId: number;
  likeCount: number;
  dislikeCount: number;
  currentUserReaction: PostCommentReaction | null;
}

export type PostCommentsApiResponse = ApiEnvelope<PostCommentsPageData>;
export type PostCommentApiResponse = ApiEnvelope<PostComment>;
export type PostCommentReactionApiResponse = ApiEnvelope<PostCommentReactionData>;

// Raw payload types to handle PascalCase from backend
type AuthorInfoPayload = Partial<AuthorInfo> & {
  Id?: number;
  Name?: string | null;
  Username?: string | null;
  usernameDisplay?: string | null;
  UsernameDisplay?: string | null;
  Avatar?: string | null;
};

type ClubPostPayload = Partial<ClubPost> & {
  Id?: number;
  ClubId?: number;
  UserId?: number;
  Title?: string;
  Content?: string;
  PostType?: string | number;
  LikesCount?: number;
  ViewCount?: number;
  CommentCount?: number;
  IsPinned?: boolean;
  Author?: AuthorInfoPayload | null;
  CreatedAt?: string;
  UpdatedAt?: string;
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

type PostCommentPayload = Partial<PostComment> & {
  Id?: number;
  PostId?: number;
  ParentCommentId?: number | null;
  UserId?: number | null;
  Content?: string | null;
  Author?: AuthorInfoPayload | null;
  IsDeleted?: boolean;
  CreatedAt?: string;
  UpdatedAt?: string;
  LikeCount?: number;
  DislikeCount?: number;
  CurrentUserReaction?: PostCommentReaction | null;
  DirectReplyCount?: number;
};

type PostCommentPagePayload = {
  items?: PostCommentPayload[];
  Items?: PostCommentPayload[];
  totalCount?: number;
  TotalCount?: number;
  nextCursor?: string | null;
  NextCursor?: string | null;
  hasMore?: boolean;
  HasMore?: boolean;
};

type PostCommentReactionPayload = Partial<PostCommentReactionData> & {
  CommentId?: number;
  LikeCount?: number;
  DislikeCount?: number;
  CurrentUserReaction?: PostCommentReaction | null;
};

const POST_TYPES: PostType[] = ['General', 'Announcement', 'Event', 'Poll'];

export function normalizeAuthor(raw: AuthorInfoPayload | null | undefined): AuthorInfo | null {
  if (!raw) return null;
  return {
    id: raw.id ?? raw.Id ?? 0,
    name: raw.name ?? raw.Name ?? null,
    username: raw.username ?? raw.Username ?? null,
    usernameDisplay:
      raw.usernameDisplay ?? raw.UsernameDisplay ?? raw.username ?? raw.Username ?? null,
    avatar: raw.avatar ?? raw.Avatar ?? null,
  };
}

export function normalizePostType(value: string | number | undefined): PostType {
  if (typeof value === 'number') return POST_TYPES[value] ?? 'General';
  return POST_TYPES.includes(value as PostType) ? (value as PostType) : 'General';
}

export function normalizeClubPost(raw: ClubPostPayload): ClubPost {
  return {
    id: raw.id ?? raw.Id ?? 0,
    clubId: raw.clubId ?? raw.ClubId ?? 0,
    userId: raw.userId ?? raw.UserId ?? 0,
    title: raw.title ?? raw.Title ?? '',
    content: raw.content ?? raw.Content ?? '',
    postType: normalizePostType(raw.postType ?? raw.PostType),
    likesCount: raw.likesCount ?? raw.LikesCount ?? 0,
    viewCount: raw.viewCount ?? raw.ViewCount ?? 0,
    commentCount: raw.commentCount ?? raw.CommentCount ?? 0,
    isPinned: raw.isPinned ?? raw.IsPinned ?? false,
    author: normalizeAuthor(raw.author ?? raw.Author),
    createdAt: raw.createdAt ?? raw.CreatedAt ?? '',
    updatedAt: raw.updatedAt ?? raw.UpdatedAt ?? '',
  };
}

export function normalizePostComment(raw: PostCommentPayload): PostComment {
  const reaction = raw.currentUserReaction ?? raw.CurrentUserReaction ?? null;
  return {
    id: raw.id ?? raw.Id ?? 0,
    postId: raw.postId ?? raw.PostId ?? 0,
    parentCommentId: raw.parentCommentId ?? raw.ParentCommentId ?? null,
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

export function normalizeClubPostsPagedData(
  raw: PagedPayload<ClubPostPayload>,
): ClubPostsPagedData {
  return {
    items: (raw.items ?? raw.Items ?? []).map(normalizeClubPost),
    totalCount: raw.totalCount ?? raw.TotalCount ?? 0,
    page: raw.page ?? raw.Page ?? 1,
    pageSize: raw.pageSize ?? raw.PageSize ?? 20,
    totalPages: raw.totalPages ?? raw.TotalPages ?? 0,
  };
}

export function normalizePostCommentsPagedData(raw: PostCommentPagePayload): PostCommentsPageData {
  return {
    items: (raw.items ?? raw.Items ?? []).map(normalizePostComment),
    totalCount: raw.totalCount ?? raw.TotalCount ?? 0,
    nextCursor: raw.nextCursor ?? raw.NextCursor ?? null,
    hasMore: raw.hasMore ?? raw.HasMore ?? false,
  };
}

export function normalizePostCommentReaction(
  raw: PostCommentReactionPayload,
): PostCommentReactionData {
  const reaction = raw.currentUserReaction ?? raw.CurrentUserReaction ?? null;
  return {
    commentId: raw.commentId ?? raw.CommentId ?? 0,
    likeCount: raw.likeCount ?? raw.LikeCount ?? 0,
    dislikeCount: raw.dislikeCount ?? raw.DislikeCount ?? 0,
    currentUserReaction: reaction === 'Like' || reaction === 'Dislike' ? reaction : null,
  };
}
