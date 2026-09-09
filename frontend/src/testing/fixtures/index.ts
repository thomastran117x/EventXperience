import { User } from '../../app/core/stores/user.model';
import { Session } from '../../app/core/stores/session.model';
import { Club } from '../../app/features/clubs/models/club.types';
import { ClubMember } from '../../app/features/clubs/models/club-management.types';
import { ClubDiscussion } from '../../app/features/clubs/models/club-discussion.types';
import { EventItem } from '../../app/features/events/models/event.types';

/**
 * Fully-populated domain objects with partial overrides, so specs only spell
 * out the fields the assertion is actually about.
 */

export function makeCurrentUser(overrides: Partial<User> = {}): User {
  return {
    Id: 1,
    Email: 'member@example.com',
    Username: 'member',
    Name: 'Test Member',
    Avatar: null,
    Usertype: 'User',
    ...overrides,
  };
}

export function makeSession(overrides: Partial<Session> = {}): Session {
  return {
    AccessToken: 'access-token',
    ExpiresAtUtc: '2099-01-01T00:00:00Z',
    ...overrides,
  };
}

export function makeClub(overrides: Partial<Club> = {}): Club {
  return {
    id: 1,
    ownerId: 1,
    name: 'Robotics Club',
    description: 'Build robots together',
    clubType: 'Academic',
    clubImage: 'https://example.com/club.png',
    bannerImage: null,
    galleryImages: [],
    memberCount: 10,
    eventCount: 2,
    availableEventCount: 1,
    maxMemberCount: 50,
    isPrivate: false,
    rating: 4.5,
    location: 'Ottawa',
    phone: null,
    email: null,
    websiteUrl: null,
    currentVersionNumber: 1,
    isOwner: false,
    isManager: false,
    isVolunteer: false,
    canManage: false,
    ...overrides,
  };
}

export function makeClubMember(overrides: Partial<ClubMember> = {}): ClubMember {
  return {
    id: 1,
    userId: 2,
    clubId: 1,
    createdAt: '2026-01-01T00:00:00Z',
    name: 'Jamie Rivers',
    username: 'jrivers',
    usernameDisplay: 'jrivers',
    avatar: null,
    ...overrides,
  };
}

export function makeClubDiscussion(overrides: Partial<ClubDiscussion> = {}): ClubDiscussion {
  return {
    id: 1,
    clubId: 1,
    userId: 2,
    title: 'Weekend ride',
    description: 'Where should we go this Saturday?',
    author: {
      id: 2,
      name: 'Jamie Rivers',
      username: 'jrivers',
      usernameDisplay: 'jrivers',
      avatar: null,
    },
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
    replyCount: 0,
    ...overrides,
  };
}

export function makeEventItem(overrides: Partial<EventItem> = {}): EventItem {
  return {
    id: 1,
    name: 'Robotics Night',
    description: 'Hands-on build session',
    location: '123 Main St',
    imageUrls: [],
    isPrivate: false,
    maxParticipants: 40,
    registerCost: 0,
    startTime: '2026-09-01T18:00:00Z',
    endTime: '2026-09-01T21:00:00Z',
    clubId: 1,
    createdAt: '2026-08-01T00:00:00Z',
    lifecycleState: 'Published',
    status: 'Upcoming',
    category: 'Workshop',
    tags: [],
    registrationCount: 5,
    waitlistEnabled: false,
    waitlistCount: 0,
    ...overrides,
  };
}
