import { AuthorInfo } from '../../models/club-post.types';
import { ThreadItem, ThreadNode } from '../thread-tree/thread-tree-state';
import { ThreadReaction } from './thread-data-source';

/**
 * The fields the thread UI renders. Both `DiscussionReply` and `PostComment` satisfy this,
 * which is what lets one component render either.
 */
export interface ThreadDisplayItem extends ThreadItem {
  userId: number | null;
  content: string | null;
  author: AuthorInfo | null;
  isDeleted: boolean;
  updatedAt: string;
  likeCount: number;
  dislikeCount: number;
  currentUserReaction: ThreadReaction | null;
  directReplyCount: number;
}

export type ThreadDisplayNode = ThreadNode<ThreadDisplayItem>;

export type ThreadNodeAction =
  | { type: 'loadChildren'; node: ThreadDisplayNode; append: boolean }
  | { type: 'create'; node: ThreadDisplayNode; content: string }
  | { type: 'edit'; node: ThreadDisplayNode; content: string }
  | { type: 'delete'; node: ThreadDisplayNode }
  | { type: 'react'; node: ThreadDisplayNode; reaction: ThreadReaction }
  /** A nested composer gained or lost content; feeds the thread-wide typing indicator. */
  | { type: 'typing'; node: ThreadDisplayNode; active: boolean };

/** Consecutive messages from one author inside this window render as a single group. */
const GROUPING_WINDOW_MS = 5 * 60 * 1000;

/**
 * Whether a node should render without its own avatar and header because it continues the
 * previous author's run — the "chat grouping" behaviour.
 */
export function isGroupedWithPrevious(nodes: ThreadDisplayNode[], index: number): boolean {
  if (index <= 0) return false;

  const previous = nodes[index - 1];
  const current = nodes[index];
  if (!previous || !current) return false;
  if (previous.isDeleted || current.isDeleted) return false;
  if (current.userId === null || previous.userId !== current.userId) return false;

  const gap = Math.abs(
    new Date(current.createdAt).getTime() - new Date(previous.createdAt).getTime(),
  );
  return Number.isFinite(gap) && gap < GROUPING_WINDOW_MS;
}

/** Display name, falling back through name → username → user id. */
export function threadAuthorName(node: ThreadDisplayNode, deletedText: string): string {
  if (node.isDeleted) return deletedText;
  return (
    node.author?.name ??
    node.author?.usernameDisplay ??
    node.author?.username ??
    `User #${node.userId}`
  );
}

/** Short relative time, switching to an absolute date after a week. */
export function formatThreadTime(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';

  const minutes = Math.floor((Date.now() - date.getTime()) / 60000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes}m ago`;
  if (minutes < 1440) return `${Math.floor(minutes / 60)}h ago`;
  if (minutes < 10080) return `${Math.floor(minutes / 1440)}d ago`;
  return date.toLocaleDateString('en-CA', { month: 'short', day: 'numeric', year: 'numeric' });
}

/** Full timestamp for the `title` tooltip on a relative time. */
export function formatThreadTimeExact(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleString();
}
