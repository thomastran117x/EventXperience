import { createThreadNode } from '../thread-tree/thread-tree-state';
import {
  ThreadDisplayItem,
  ThreadDisplayNode,
  formatThreadTime,
  formatThreadTimeExact,
  isGroupedWithPrevious,
  threadAuthorName,
} from './thread-item';

function node(overrides: Partial<ThreadDisplayItem> = {}): ThreadDisplayNode {
  return createThreadNode<ThreadDisplayItem>({
    id: 1,
    createdAt: '2026-08-15T12:00:00Z',
    updatedAt: '2026-08-15T12:00:00Z',
    userId: 7,
    content: 'Hello',
    author: {
      id: 7,
      name: 'Taylor Rider',
      username: 'taylor',
      usernameDisplay: 'taylor',
      avatar: null,
    },
    isDeleted: false,
    likeCount: 0,
    dislikeCount: 0,
    currentUserReaction: null,
    directReplyCount: 0,
    ...overrides,
  });
}

describe('isGroupedWithPrevious', () => {
  it('never groups the first node', () => {
    expect(isGroupedWithPrevious([node()], 0)).toBeFalse();
    expect(isGroupedWithPrevious([node()], -1)).toBeFalse();
  });

  it('groups the same author inside the window', () => {
    const nodes = [
      node({ id: 1, createdAt: '2026-08-15T12:00:00Z' }),
      node({ id: 2, createdAt: '2026-08-15T12:03:00Z' }),
    ];

    expect(isGroupedWithPrevious(nodes, 1)).toBeTrue();
  });

  it('breaks the group once the window lapses', () => {
    const nodes = [
      node({ id: 1, createdAt: '2026-08-15T12:00:00Z' }),
      node({ id: 2, createdAt: '2026-08-15T12:06:00Z' }),
    ];

    expect(isGroupedWithPrevious(nodes, 1)).toBeFalse();
  });

  it('breaks the group when the author changes', () => {
    const nodes = [
      node({ id: 1, userId: 7 }),
      node({ id: 2, userId: 8, createdAt: '2026-08-15T12:01:00Z' }),
    ];

    expect(isGroupedWithPrevious(nodes, 1)).toBeFalse();
  });

  it('never groups across a deleted message', () => {
    const deletedFirst = [
      node({ id: 1, isDeleted: true }),
      node({ id: 2, createdAt: '2026-08-15T12:01:00Z' }),
    ];
    expect(isGroupedWithPrevious(deletedFirst, 1)).toBeFalse();

    const deletedSecond = [
      node({ id: 1 }),
      node({ id: 2, isDeleted: true, createdAt: '2026-08-15T12:01:00Z' }),
    ];
    expect(isGroupedWithPrevious(deletedSecond, 1)).toBeFalse();
  });

  it('never groups anonymous rows with a null author id', () => {
    const nodes = [
      node({ id: 1, userId: null }),
      node({ id: 2, userId: null, createdAt: '2026-08-15T12:01:00Z' }),
    ];

    expect(isGroupedWithPrevious(nodes, 1)).toBeFalse();
  });
});

describe('threadAuthorName', () => {
  it('prefers the name, then the username, then the id', () => {
    expect(threadAuthorName(node(), 'Reply deleted')).toBe('Taylor Rider');
    expect(
      threadAuthorName(
        node({
          author: {
            id: 7,
            name: null,
            username: 'taylor',
            usernameDisplay: 'taylor',
            avatar: null,
          },
        }),
        'x',
      ),
    ).toBe('taylor');
    expect(threadAuthorName(node({ author: null }), 'x')).toBe('User #7');
  });

  it('uses the deleted label for redacted rows', () => {
    expect(threadAuthorName(node({ isDeleted: true }), 'Reply deleted')).toBe('Reply deleted');
  });
});

describe('formatThreadTime', () => {
  it('describes recent times relatively', () => {
    const now = Date.now();
    expect(formatThreadTime(new Date(now - 10_000).toISOString())).toBe('just now');
    expect(formatThreadTime(new Date(now - 5 * 60_000).toISOString())).toBe('5m ago');
    expect(formatThreadTime(new Date(now - 3 * 3_600_000).toISOString())).toBe('3h ago');
    expect(formatThreadTime(new Date(now - 2 * 86_400_000).toISOString())).toBe('2d ago');
  });

  it('falls back to a date beyond a week', () => {
    const old = new Date(Date.now() - 30 * 86_400_000).toISOString();
    expect(formatThreadTime(old)).toMatch(/\d{4}/);
  });

  it('returns an empty string for an unparseable value', () => {
    expect(formatThreadTime('not-a-date')).toBe('');
    expect(formatThreadTimeExact('not-a-date')).toBe('');
  });

  it('renders an exact timestamp for the tooltip', () => {
    expect(formatThreadTimeExact('2026-08-15T12:00:00Z')).not.toBe('');
  });
});
