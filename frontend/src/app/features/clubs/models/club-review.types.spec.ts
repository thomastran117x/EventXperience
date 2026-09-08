import { normalizeClubReview, normalizeClubReviewsPagedData } from './club-review.types';

describe('normalizeClubReview', () => {
  it('reads a PascalCase payload', () => {
    expect(
      normalizeClubReview({
        Id: 1,
        UserId: 2,
        ClubId: 3,
        Title: 'Great club',
        Rating: 5,
        Comment: 'Really welcoming',
        CreatedAt: '2026-01-01T00:00:00Z',
        Name: 'Jamie Rivers',
        Username: 'jrivers',
        Avatar: null,
      }),
    ).toEqual({
      id: 1,
      userId: 2,
      clubId: 3,
      title: 'Great club',
      rating: 5,
      comment: 'Really welcoming',
      createdAt: '2026-01-01T00:00:00Z',
      name: 'Jamie Rivers',
      username: 'jrivers',
      usernameDisplay: 'jrivers',
      avatar: null,
    });
  });

  it('defaults an empty payload to a zero rating and a null comment', () => {
    const result = normalizeClubReview({});

    expect(result.rating).toBe(0);
    expect(result.comment).toBeNull();
    expect(result.title).toBe('');
  });

  it('prefers camelCase over PascalCase when both are present', () => {
    expect(normalizeClubReview({ rating: 4, Rating: 1 }).rating).toBe(4);
  });
});

describe('normalizeClubReviewsPagedData', () => {
  it('maps items and reads PascalCase paging metadata', () => {
    const result = normalizeClubReviewsPagedData({
      Items: [{ Id: 1, Rating: 5 }],
      TotalCount: 1,
      Page: 2,
      PageSize: 10,
      TotalPages: 1,
    });

    expect(result.items[0].rating).toBe(5);
    expect(result).toEqual(
      jasmine.objectContaining({ totalCount: 1, page: 2, pageSize: 10, totalPages: 1 }),
    );
  });

  it('applies the paging defaults for an empty payload', () => {
    expect(normalizeClubReviewsPagedData({})).toEqual({
      items: [],
      totalCount: 0,
      page: 1,
      pageSize: 20,
      totalPages: 0,
    });
  });
});
