import {
  normalizeAuthor,
  normalizeClubPost,
  normalizeClubPostsPagedData,
  normalizePostComment,
  normalizePostCommentReaction,
  normalizePostCommentsPagedData,
  normalizePostType,
} from './club-post.types';

describe('normalizeAuthor', () => {
  it('reads both casings', () => {
    expect(normalizeAuthor({ Id: 3, Name: 'Jamie', Username: 'jamie', Avatar: null })).toEqual({
      id: 3,
      name: 'Jamie',
      username: 'jamie',
      usernameDisplay: 'jamie',
      avatar: null,
    });

    expect(normalizeAuthor({ id: 4 })?.id).toBe(4);
  });

  it('returns null for a missing author', () => {
    expect(normalizeAuthor(null)).toBeNull();
    expect(normalizeAuthor(undefined)).toBeNull();
  });

  it('prefers the camelCase key when both casings are present', () => {
    expect(
      normalizeAuthor({
        id: 1,
        Id: 99,
        name: 'Camel',
        Name: 'Pascal',
        username: 'c',
        usernameDisplay: 'c',
        avatar: 'a',
      }),
    ).toEqual({ id: 1, name: 'Camel', username: 'c', usernameDisplay: 'c', avatar: 'a' });
  });

  it('defaults an author object that carries nothing', () => {
    expect(normalizeAuthor({})).toEqual({
      id: 0,
      name: null,
      username: null,
      usernameDisplay: null,
      avatar: null,
    });
  });
});

describe('normalizePostType', () => {
  it('maps a numeric enum to its label', () => {
    expect(normalizePostType(0)).toBe('General');
    expect(normalizePostType(1)).toBe('Announcement');
    expect(normalizePostType(2)).toBe('Event');
    expect(normalizePostType(3)).toBe('Poll');
  });

  it('falls back to General for an out-of-range number', () => {
    expect(normalizePostType(99)).toBe('General');
  });

  it('passes a known string through and rejects an unknown one', () => {
    expect(normalizePostType('Poll')).toBe('Poll');
    expect(normalizePostType('Rumour')).toBe('General');
    expect(normalizePostType(undefined)).toBe('General');
  });
});

describe('normalizeClubPost', () => {
  it('reads a PascalCase payload including the nested author', () => {
    expect(
      normalizeClubPost({
        Id: 1,
        ClubId: 2,
        UserId: 3,
        Title: 'Kickoff',
        Content: 'Welcome',
        PostType: 1,
        LikesCount: 7,
        ViewCount: 42,
        CommentCount: 5,
        IsPinned: true,
        Author: { Id: 3, Name: 'Jamie' },
        CreatedAt: '2026-01-01T00:00:00Z',
        UpdatedAt: '2026-01-02T00:00:00Z',
      }),
    ).toEqual({
      id: 1,
      clubId: 2,
      userId: 3,
      title: 'Kickoff',
      content: 'Welcome',
      postType: 'Announcement',
      likesCount: 7,
      viewCount: 42,
      commentCount: 5,
      isPinned: true,
      author: { id: 3, name: 'Jamie', username: null, usernameDisplay: null, avatar: null },
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-02T00:00:00Z',
    });
  });

  it('defaults an empty payload and leaves the author null', () => {
    const result = normalizeClubPost({});

    expect(result.id).toBe(0);
    expect(result.title).toBe('');
    expect(result.postType).toBe('General');
    expect(result.isPinned).toBeFalse();
    expect(result.author).toBeNull();
  });

  it('prefers the camelCase key when both casings are present', () => {
    expect(
      normalizeClubPost({
        id: 1,
        Id: 99,
        clubId: 2,
        userId: 3,
        title: 'Camel',
        Title: 'Pascal',
        content: 'c',
        postType: 'Poll',
        likesCount: 7,
        viewCount: 42,
        isPinned: true,
        createdAt: 'a',
        updatedAt: 'b',
      }),
    ).toEqual(
      jasmine.objectContaining({ id: 1, title: 'Camel', postType: 'Poll', isPinned: true }),
    );
  });
});

describe('normalizePostComment', () => {
  it('reads both casings and normalizes the author', () => {
    expect(
      normalizePostComment({
        Id: 5,
        PostId: 6,
        UserId: 7,
        Content: 'Nice',
        Author: { Id: 7, Username: 'jamie' },
        CreatedAt: '2026-01-03T00:00:00Z',
        UpdatedAt: '2026-01-03T00:00:00Z',
      }),
    ).toEqual({
      id: 5,
      postId: 6,
      parentCommentId: null,
      userId: 7,
      content: 'Nice',
      author: { id: 7, name: null, username: 'jamie', usernameDisplay: 'jamie', avatar: null },
      isDeleted: false,
      createdAt: '2026-01-03T00:00:00Z',
      updatedAt: '2026-01-03T00:00:00Z',
      likeCount: 0,
      dislikeCount: 0,
      currentUserReaction: null,
      directReplyCount: 0,
    });
  });
});

describe('paged post normalizers', () => {
  it('maps posts and applies the paging defaults', () => {
    const result = normalizeClubPostsPagedData({ Items: [{ Id: 1 }], TotalCount: 1 });

    expect(result.items[0].id).toBe(1);
    expect(result.totalCount).toBe(1);
    expect(result.page).toBe(1);
    expect(result.pageSize).toBe(20);
    expect(result.totalPages).toBe(0);
  });

  it('maps comments and honours cursor metadata', () => {
    const result = normalizePostCommentsPagedData({
      items: [{ id: 9 }],
      totalCount: 11,
      nextCursor: 'next',
      hasMore: true,
    });

    expect(result.items[0].id).toBe(9);
    expect(result).toEqual(
      jasmine.objectContaining({ nextCursor: 'next', hasMore: true, totalCount: 11 }),
    );
  });

  it('normalizes the current viewer reaction', () => {
    expect(
      normalizePostCommentReaction({
        CommentId: 9,
        LikeCount: 3,
        DislikeCount: 1,
        CurrentUserReaction: 'Like',
      }),
    ).toEqual({ commentId: 9, likeCount: 3, dislikeCount: 1, currentUserReaction: 'Like' });
  });

  it('returns an empty list when neither items key is present', () => {
    expect(normalizeClubPostsPagedData({}).items).toEqual([]);
    expect(normalizePostCommentsPagedData({}).items).toEqual([]);
  });
});
