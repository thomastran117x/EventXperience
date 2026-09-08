import {
  discussionAuthorName,
  normalizeClubDiscussion,
  normalizeClubDiscussionsPagedData,
  normalizeDiscussionReply,
  normalizeDiscussionReplyPage,
} from './club-discussion.types';

describe('club-discussion.types', () => {
  describe('normalizeClubDiscussion', () => {
    it('passes through a camelCase payload', () => {
      const result = normalizeClubDiscussion({
        id: 7,
        clubId: 3,
        userId: 9,
        title: 'Weekend ride',
        description: 'Where should we go?',
        author: {
          id: 9,
          name: 'Jamie',
          username: 'jrivers',
          usernameDisplay: 'jrivers',
          avatar: 'a.png',
        },
        createdAt: '2026-08-01T00:00:00Z',
        updatedAt: '2026-08-02T00:00:00Z',
        replyCount: 0,
      });

      expect(result).toEqual({
        id: 7,
        clubId: 3,
        userId: 9,
        title: 'Weekend ride',
        description: 'Where should we go?',
        author: {
          id: 9,
          name: 'Jamie',
          username: 'jrivers',
          usernameDisplay: 'jrivers',
          avatar: 'a.png',
        },
        createdAt: '2026-08-01T00:00:00Z',
        updatedAt: '2026-08-02T00:00:00Z',
        replyCount: 0,
      });
    });

    it('normalizes a PascalCase payload', () => {
      const result = normalizeClubDiscussion({
        Id: 7,
        ClubId: 3,
        UserId: 9,
        Title: 'Trail conditions',
        Description: 'North ridge?',
        Author: { Id: 9, Name: 'Jamie', Username: 'jrivers', Avatar: null },
        CreatedAt: '2026-08-01T00:00:00Z',
        UpdatedAt: '2026-08-01T00:00:00Z',
      });

      expect(result.id).toBe(7);
      expect(result.title).toBe('Trail conditions');
      expect(result.description).toBe('North ridge?');
      expect(result.author).toEqual({
        id: 9,
        name: 'Jamie',
        username: 'jrivers',
        usernameDisplay: 'jrivers',
        avatar: null,
      });
    });

    it('falls back to defaults for a sparse payload', () => {
      const result = normalizeClubDiscussion({});

      expect(result).toEqual({
        id: 0,
        clubId: 0,
        userId: 0,
        title: '',
        description: '',
        author: null,
        createdAt: '',
        updatedAt: '',
        replyCount: 0,
      });
    });
  });

  describe('normalizeClubDiscussionsPagedData', () => {
    it('normalizes a camelCase page', () => {
      const result = normalizeClubDiscussionsPagedData({
        items: [{ id: 1, title: 'A' }],
        totalCount: 1,
        page: 2,
        pageSize: 5,
        totalPages: 1,
      });

      expect(result.items).toEqual([jasmine.objectContaining({ id: 1, title: 'A' })]);
      expect(result).toEqual(
        jasmine.objectContaining({ totalCount: 1, page: 2, pageSize: 5, totalPages: 1 }),
      );
    });

    it('normalizes a PascalCase page', () => {
      const result = normalizeClubDiscussionsPagedData({
        Items: [{ Id: 2, Title: 'B' }],
        TotalCount: 3,
        Page: 1,
        PageSize: 20,
        TotalPages: 1,
      });

      expect(result.items).toEqual([jasmine.objectContaining({ id: 2, title: 'B' })]);
      expect(result.totalCount).toBe(3);
    });

    it('falls back to an empty page', () => {
      expect(normalizeClubDiscussionsPagedData({})).toEqual({
        items: [],
        totalCount: 0,
        page: 1,
        pageSize: 20,
        totalPages: 0,
      });
    });
  });

  describe('discussionAuthorName', () => {
    it('prefers the name, then the username, then the user id', () => {
      const base = normalizeClubDiscussion({ userId: 9 });

      expect(
        discussionAuthorName({
          ...base,
          author: {
            id: 9,
            name: 'Jamie',
            username: 'jrivers',
            usernameDisplay: 'jrivers',
            avatar: null,
          },
        }),
      ).toBe('Jamie');

      expect(
        discussionAuthorName({
          ...base,
          author: {
            id: 9,
            name: null,
            username: 'jrivers',
            usernameDisplay: 'jrivers',
            avatar: null,
          },
        }),
      ).toBe('jrivers');

      expect(discussionAuthorName({ ...base, author: null })).toBe('User #9');
    });
  });

  describe('discussion reply normalization', () => {
    it('normalizes nested reply state and reactions', () => {
      const reply = normalizeDiscussionReply({
        Id: 8,
        DiscussionId: 3,
        ParentReplyId: 4,
        UserId: 9,
        Content: 'Nested',
        IsDeleted: false,
        LikeCount: 2,
        DislikeCount: 1,
        CurrentUserReaction: 'Like',
        DirectReplyCount: 5,
      });

      expect(reply).toEqual(
        jasmine.objectContaining({
          id: 8,
          discussionId: 3,
          parentReplyId: 4,
          content: 'Nested',
          likeCount: 2,
          dislikeCount: 1,
          currentUserReaction: 'Like',
          directReplyCount: 5,
        }),
      );
    });

    it('normalizes a cursor page and deleted placeholder', () => {
      const page = normalizeDiscussionReplyPage({
        Items: [{ Id: 8, IsDeleted: true, Content: null, UserId: null }],
        TotalCount: 1,
        NextCursor: 'cursor',
        HasMore: true,
      });

      expect(page.totalCount).toBe(1);
      expect(page.nextCursor).toBe('cursor');
      expect(page.hasMore).toBeTrue();
      expect(page.items[0]).toEqual(
        jasmine.objectContaining({ isDeleted: true, content: null, userId: null }),
      );
    });
  });
});
