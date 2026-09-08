import {
  normalizeClubAnalytics,
  normalizeClubMember,
  normalizeClubMembersPagedData,
  normalizeClubRollback,
  normalizeClubStaff,
  normalizeClubVersionDetail,
  normalizeClubVersionListItem,
  normalizeClubVersionsPagedData,
  toClubtypeAlias,
} from './club-management.types';

describe('toClubtypeAlias', () => {
  it('lowercases the club type for the backend alias', () => {
    expect(toClubtypeAlias('Academic')).toBe('academic');
    expect(toClubtypeAlias('Other')).toBe('other');
  });
});

describe('normalizeClubStaff', () => {
  it('reads PascalCase payloads', () => {
    expect(
      normalizeClubStaff({
        Id: 1,
        ClubId: 2,
        UserId: 3,
        Role: 'Volunteer',
        GrantedByUserId: 4,
        CreatedAt: '2026-01-01T00:00:00Z',
        UpdatedAt: '2026-01-02T00:00:00Z',
        Name: 'Jamie Rivers',
        Username: 'jrivers',
        Avatar: 'https://example.com/a.png',
      }),
    ).toEqual({
      id: 1,
      clubId: 2,
      userId: 3,
      role: 'Volunteer',
      grantedByUserId: 4,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-02T00:00:00Z',
      name: 'Jamie Rivers',
      username: 'jrivers',
      usernameDisplay: 'jrivers',
      avatar: 'https://example.com/a.png',
    });
  });

  it('defaults an empty payload to zeroes and nulls', () => {
    expect(normalizeClubStaff({})).toEqual({
      id: 0,
      clubId: 0,
      userId: 0,
      role: 'Manager',
      grantedByUserId: 0,
      createdAt: '',
      updatedAt: '',
      name: null,
      username: null,
      usernameDisplay: null,
      avatar: null,
    });
  });

  it('falls back to Manager for any role other than Volunteer', () => {
    expect(normalizeClubStaff({ Role: 'Owner' }).role).toBe('Manager');
    expect(normalizeClubStaff({ role: 'Volunteer' }).role).toBe('Volunteer');
  });
});

describe('camelCase precedence', () => {
  it('prefers the camelCase key on a member', () => {
    expect(
      normalizeClubMember({
        id: 1,
        Id: 99,
        userId: 2,
        clubId: 3,
        createdAt: '2026-01-01T00:00:00Z',
        name: 'Camel',
        username: 'camel',
        usernameDisplay: 'camel',
        avatar: 'a.png',
      }),
    ).toEqual({
      id: 1,
      userId: 2,
      clubId: 3,
      createdAt: '2026-01-01T00:00:00Z',
      name: 'Camel',
      username: 'camel',
      usernameDisplay: 'camel',
      avatar: 'a.png',
    });
  });

  it('prefers the camelCase key on a version list item', () => {
    const result = normalizeClubVersionListItem({
      versionNumber: 4,
      VersionNumber: 99,
      actionType: 'Update',
      createdAt: '2026-02-01T00:00:00Z',
      actorUserId: 9,
      actorRole: 'Owner',
      rollbackEligible: true,
      rollbackExpiresAt: '2026-02-08T00:00:00Z',
      rollbackSourceVersionNumber: 3,
      changedFields: [{ field: 'name', oldValue: 'Old', newValue: 'New' }],
      actorName: 'Jamie',
      actorUsername: 'jrivers',
      actorAvatar: 'a.png',
    });

    expect(result.versionNumber).toBe(4);
    expect(result.actorName).toBe('Jamie');
    expect(result.changedFields).toEqual([{ field: 'name', oldValue: 'Old', newValue: 'New' }]);
  });

  it('prefers the camelCase key on a snapshot and a rollback', () => {
    const detail = normalizeClubVersionDetail({
      snapshot: {
        name: 'Camel',
        description: 'd',
        clubtype: 'Academic',
        clubImage: 'c.png',
        phone: '555',
        email: 'c@example.com',
        websiteUrl: 'https://c',
        location: 'Ottawa',
        maxMemberCount: 80,
        isPrivate: true,
      },
    });

    expect(detail.snapshot).toEqual(
      jasmine.objectContaining({ name: 'Camel', phone: '555', location: 'Ottawa' }),
    );

    const rollback = normalizeClubRollback({
      club: { id: 5 },
      restoredFromVersionNumber: 2,
      newVersionNumber: 6,
    });

    expect(rollback.club.id).toBe(5);
    expect(rollback.restoredFromVersionNumber).toBe(2);
    expect(rollback.newVersionNumber).toBe(6);
  });

  it('prefers the camelCase key on paged version data', () => {
    expect(
      normalizeClubVersionsPagedData({
        items: [{ versionNumber: 1 }],
        totalCount: 5,
        page: 2,
        pageSize: 10,
        totalPages: 1,
      }),
    ).toEqual(jasmine.objectContaining({ totalCount: 5, page: 2, pageSize: 10, totalPages: 1 }));
  });

  it('prefers the camelCase key on analytics counters and lists', () => {
    const result = normalizeClubAnalytics({
      clubId: 3,
      totalEvents: 12,
      topEventsByRegistrations: [
        { id: 1, name: 'Kickoff', registrationCount: 40, fillRate: 0.9, revenue: 100 },
      ],
      registrationTrend: [{ date: '2026-01-01', count: 5 }],
      revenueTrend: [{ date: '2026-01-01', amount: 250 }],
    });

    expect(result.clubId).toBe(3);
    expect(result.topEventsByRegistrations[0]).toEqual({
      id: 1,
      name: 'Kickoff',
      registrationCount: 40,
      fillRate: 0.9,
      revenue: 100,
    });
    expect(result.registrationTrend).toEqual([{ date: '2026-01-01', value: 5 }]);
    expect(result.revenueTrend).toEqual([{ date: '2026-01-01', value: 250 }]);
  });

  it('defaults a trend point that carries neither a date nor a value', () => {
    const result = normalizeClubAnalytics({ registrationTrend: [{}], revenueTrend: [{}] });

    expect(result.registrationTrend).toEqual([{ date: '', value: 0 }]);
    expect(result.revenueTrend).toEqual([{ date: '', value: 0 }]);
  });

  it('defaults a top-event entry that carries nothing', () => {
    expect(normalizeClubAnalytics({ topEventsByRevenue: [{}] }).topEventsByRevenue).toEqual([
      { id: 0, name: '', registrationCount: 0, fillRate: 0, revenue: 0 },
    ]);
  });
});

describe('normalizeClubMember', () => {
  it('reads both casings', () => {
    expect(normalizeClubMember({ Id: 1, UserId: 2, ClubId: 3 })).toEqual({
      id: 1,
      userId: 2,
      clubId: 3,
      createdAt: '',
      name: null,
      username: null,
      usernameDisplay: null,
      avatar: null,
    });

    expect(normalizeClubMember({ id: 4, userId: 5, clubId: 6 }).id).toBe(4);
  });
});

describe('normalizeClubMembersPagedData', () => {
  it('maps items and reads PascalCase paging metadata', () => {
    expect(
      normalizeClubMembersPagedData({
        Items: [{ Id: 1, Name: 'Jamie Rivers' }],
        TotalCount: 42,
        Page: 3,
        PageSize: 5,
        TotalPages: 9,
      }),
    ).toEqual({
      items: [
        {
          id: 1,
          userId: 0,
          clubId: 0,
          createdAt: '',
          name: 'Jamie Rivers',
          username: null,
          usernameDisplay: null,
          avatar: null,
        },
      ],
      totalCount: 42,
      page: 3,
      pageSize: 5,
      totalPages: 9,
    });
  });

  it('defaults an empty page to page 1 with a page size of 20', () => {
    expect(normalizeClubMembersPagedData({})).toEqual({
      items: [],
      totalCount: 0,
      page: 1,
      pageSize: 20,
      totalPages: 0,
    });
  });
});

describe('normalizeClubVersionListItem', () => {
  it('maps nested changed fields', () => {
    const result = normalizeClubVersionListItem({
      VersionNumber: 4,
      ActionType: 'Update',
      CreatedAt: '2026-02-01T00:00:00Z',
      ActorUserId: 9,
      ActorRole: 'Owner',
      RollbackEligible: true,
      RollbackExpiresAt: '2026-02-08T00:00:00Z',
      RollbackSourceVersionNumber: 3,
      ChangedFields: [{ Field: 'name', OldValue: 'Old', NewValue: 'New' }],
      ActorName: 'Jamie Rivers',
      ActorUsername: 'jrivers',
      ActorAvatar: null,
    });

    expect(result.versionNumber).toBe(4);
    expect(result.rollbackEligible).toBeTrue();
    expect(result.rollbackSourceVersionNumber).toBe(3);
    expect(result.changedFields).toEqual([{ field: 'name', oldValue: 'Old', newValue: 'New' }]);
  });

  it('defaults an empty payload without throwing on missing changed fields', () => {
    expect(normalizeClubVersionListItem({})).toEqual({
      versionNumber: 0,
      actionType: '',
      createdAt: '',
      actorUserId: 0,
      actorRole: '',
      rollbackEligible: false,
      rollbackExpiresAt: '',
      rollbackSourceVersionNumber: null,
      changedFields: [],
      actorName: null,
      actorUsername: null,
      actorAvatar: null,
    });
  });
});

describe('normalizeClubVersionDetail', () => {
  it('spreads the list-item fields and adds the snapshot', () => {
    const result = normalizeClubVersionDetail({
      VersionNumber: 2,
      ActionType: 'Rollback',
      Snapshot: {
        Name: 'Robotics Club',
        Description: 'Build robots',
        Clubtype: 'Academic',
        ClubImage: 'https://example.com/c.png',
        MaxMemberCount: 80,
        IsPrivate: true,
      },
    });

    expect(result.versionNumber).toBe(2);
    expect(result.actionType).toBe('Rollback');
    expect(result.snapshot).toEqual({
      name: 'Robotics Club',
      description: 'Build robots',
      clubtype: 'Academic',
      clubImage: 'https://example.com/c.png',
      phone: null,
      email: null,
      websiteUrl: null,
      location: null,
      maxMemberCount: 80,
      isPrivate: true,
    });
  });

  it('substitutes an empty snapshot when none is supplied', () => {
    expect(normalizeClubVersionDetail({}).snapshot).toEqual({
      name: '',
      description: '',
      clubtype: '',
      clubImage: '',
      phone: null,
      email: null,
      websiteUrl: null,
      location: null,
      maxMemberCount: 0,
      isPrivate: false,
    });
  });
});

describe('normalizeClubVersionsPagedData', () => {
  it('maps items and applies the paging defaults', () => {
    const result = normalizeClubVersionsPagedData({ Items: [{ VersionNumber: 1 }] });

    expect(result.items.length).toBe(1);
    expect(result.items[0].versionNumber).toBe(1);
    expect(result.page).toBe(1);
    expect(result.pageSize).toBe(20);
  });
});

describe('normalizeClubRollback', () => {
  it('normalizes the nested club and the version numbers', () => {
    const result = normalizeClubRollback({
      Club: { Id: 5, Name: 'Robotics Club', Clubtype: 'Academic' },
      RestoredFromVersionNumber: 2,
      NewVersionNumber: 6,
    });

    expect(result.club.id).toBe(5);
    expect(result.club.name).toBe('Robotics Club');
    expect(result.club.clubType).toBe('Academic');
    expect(result.restoredFromVersionNumber).toBe(2);
    expect(result.newVersionNumber).toBe(6);
  });

  it('normalizes an empty club rather than returning undefined', () => {
    const result = normalizeClubRollback({});

    expect(result.club.id).toBe(0);
    expect(result.club.clubType).toBe('Other');
    expect(result.newVersionNumber).toBe(0);
  });
});

describe('normalizeClubAnalytics', () => {
  it('reads PascalCase counters and top-event lists', () => {
    const result = normalizeClubAnalytics({
      ClubId: 3,
      TotalEvents: 12,
      PublishedEvents: 8,
      TotalRevenue: 1500,
      AvgFillRate: 0.75,
      TopEventsByRegistrations: [{ Id: 1, Name: 'Kickoff', RegistrationCount: 40 }],
    });

    expect(result.clubId).toBe(3);
    expect(result.totalEvents).toBe(12);
    expect(result.publishedEvents).toBe(8);
    expect(result.totalRevenue).toBe(1500);
    expect(result.avgFillRate).toBe(0.75);
    expect(result.topEventsByRegistrations).toEqual([
      { id: 1, name: 'Kickoff', registrationCount: 40, fillRate: 0, revenue: 0 },
    ]);
  });

  it('reads the registration trend from Count and the revenue trend from Amount', () => {
    const result = normalizeClubAnalytics({
      RegistrationTrend: [{ Date: '2026-01-01', Count: 5 }],
      RevenueTrend: [{ Date: '2026-01-01', Amount: 250 }],
    });

    expect(result.registrationTrend).toEqual([{ date: '2026-01-01', value: 5 }]);
    expect(result.revenueTrend).toEqual([{ date: '2026-01-01', value: 250 }]);
  });

  it('zeroes every counter and empties every list for an empty payload', () => {
    const result = normalizeClubAnalytics({});

    expect(result.totalEvents).toBe(0);
    expect(result.uniqueAttendees).toBe(0);
    expect(result.topEventsByRevenue).toEqual([]);
    expect(result.topEventsByFillRate).toEqual([]);
    expect(result.registrationTrend).toEqual([]);
    expect(result.revenueTrend).toEqual([]);
  });
});
