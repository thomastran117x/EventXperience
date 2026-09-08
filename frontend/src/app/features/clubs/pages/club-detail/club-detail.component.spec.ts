import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { of, throwError } from 'rxjs';

import {
  envelope,
  fakeActivatedRoute,
  makeClub,
  makeClubDiscussion,
  makeClubMember,
  makeCurrentUser,
  makeEventItem,
  provideFeatureFlags,
  provideTestStore,
} from '@testing';

import { ClubDetailComponent } from './club-detail.component';
import { ClubsService } from '../../services/clubs.service';
import { ClubPostsService } from '../../services/club-posts.service';
import { ClubDiscussionsService } from '../../services/club-discussions.service';
import { ClubReviewsService } from '../../services/club-reviews.service';
import { ClubManagementService } from '../../services/club-management.service';
import { EventsService } from '../../../events/services/events.service';
import { ClubReview } from '../../models/club-review.types';
import { Club } from '../../models/club.types';
import { ClubPost } from '../../models/club-post.types';
import { ClubDiscussion } from '../../models/club-discussion.types';
import { ClubMember } from '../../models/club-management.types';
import { EventItem, EventsApiResponse } from '../../../events/models/event.types';
import { ApiClientClientError } from '../../../../core/api/models/api-client-error.model';
import { User } from '../../../../core/stores/user.model';

function makeReview(overrides: Partial<ClubReview> = {}): ClubReview {
  return {
    id: 1,
    userId: 1,
    clubId: 1,
    title: 'Great club',
    rating: 5,
    comment: 'Really welcoming',
    createdAt: '2026-01-01T00:00:00Z',
    name: 'Test Member',
    username: 'member',
    usernameDisplay: 'member',
    avatar: null,
    ...overrides,
  };
}

interface Paged<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

function paged<T>(items: T[], totalCount = items.length) {
  return envelope<Paged<T>>({ items, totalCount, page: 1, pageSize: 10, totalPages: 1 });
}

/** EventsApiResponse declares a non-nullable `meta`, so its pages need a source. */
function pagedEvents(items: EventItem[], totalCount = items.length): EventsApiResponse {
  return { ...paged(items, totalCount), meta: { source: 'database' } };
}

describe('ClubDetailComponent', () => {
  let fixture: ComponentFixture<ClubDetailComponent>;
  let component: ClubDetailComponent;
  let route: ReturnType<typeof fakeActivatedRoute>;
  let router: jasmine.SpyObj<Router>;
  let clubs: jasmine.SpyObj<ClubsService>;
  let posts: jasmine.SpyObj<ClubPostsService>;
  let discussions: jasmine.SpyObj<ClubDiscussionsService>;
  let events: jasmine.SpyObj<EventsService>;
  let reviews: jasmine.SpyObj<ClubReviewsService>;
  let management: jasmine.SpyObj<ClubManagementService>;

  async function setup(
    user: User | null = makeCurrentUser({ Id: 1 }),
    discussionsEnabled = true,
  ): Promise<void> {
    route = fakeActivatedRoute({ params: { clubId: '3' } });

    router = jasmine.createSpyObj<Router>('Router', ['navigate'], { url: '/clubs/3' });
    router.navigate.and.resolveTo(true);

    clubs = jasmine.createSpyObj<ClubsService>('ClubsService', [
      'getClub',
      'joinClub',
      'leaveClub',
      'getMembershipStatus',
    ]);
    clubs.getClub.and.returnValue(of(envelope(makeClub({ id: 3, memberCount: 10 }))));
    clubs.getMembershipStatus.and.returnValue(of(false));

    posts = jasmine.createSpyObj<ClubPostsService>('ClubPostsService', ['getPosts']);
    posts.getPosts.and.returnValue(of(paged<ClubPost>([])));

    discussions = jasmine.createSpyObj<ClubDiscussionsService>('ClubDiscussionsService', [
      'getDiscussions',
    ]);
    discussions.getDiscussions.and.returnValue(of(paged<ClubDiscussion>([])));

    events = jasmine.createSpyObj<EventsService>('EventsService', ['getEventsByClub']);
    events.getEventsByClub.and.returnValue(of(pagedEvents([])));

    reviews = jasmine.createSpyObj<ClubReviewsService>('ClubReviewsService', [
      'getReviews',
      'createReview',
      'updateReview',
      'deleteReview',
    ]);
    reviews.getReviews.and.returnValue(of(paged<ClubReview>([])));

    management = jasmine.createSpyObj<ClubManagementService>('ClubManagementService', [
      'getMembers',
    ]);
    management.getMembers.and.returnValue(of(paged<ClubMember>([])));

    await TestBed.configureTestingModule({
      imports: [ClubDetailComponent],
      providers: [
        { provide: ActivatedRoute, useValue: route.route },
        { provide: Router, useValue: router },
        { provide: ClubsService, useValue: clubs },
        { provide: ClubPostsService, useValue: posts },
        { provide: ClubDiscussionsService, useValue: discussions },
        { provide: EventsService, useValue: events },
        { provide: ClubReviewsService, useValue: reviews },
        { provide: ClubManagementService, useValue: management },
        ...provideTestStore({ user }),
        provideFeatureFlags({ 'clubs.discussions': discussionsEnabled }),
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ClubDetailComponent);
    component = fixture.componentInstance;
  }

  beforeEach(async () => {
    await setup();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  describe('initial load', () => {
    it('loads the club, its recent posts and its upcoming events', () => {
      fixture.detectChanges();

      expect(component.clubId).toBe(3);
      expect(component.club?.id).toBe(3);
      expect(component.loading).toBeFalse();
      expect(posts.getPosts).toHaveBeenCalledOnceWith(3, {
        sortBy: 'Recent',
        page: 1,
        pageSize: 3,
      });
      expect(events.getEventsByClub).toHaveBeenCalledWith(3, {
        status: 'Upcoming',
        page: 1,
        pageSize: 3,
      });
    });

    it('loads the three newest discussions', () => {
      discussions.getDiscussions.and.returnValue(
        of(
          paged<ClubDiscussion>([
            makeClubDiscussion({ id: 2, title: 'Newer' }),
            makeClubDiscussion({ id: 1, title: 'Older' }),
          ]),
        ),
      );

      fixture.detectChanges();

      expect(discussions.getDiscussions).toHaveBeenCalledOnceWith(3, 1, 3);
      expect(component.recentDiscussions.map((d) => d.title)).toEqual(['Newer', 'Older']);
      expect(component.discussionsLoading).toBeFalse();
    });

    it('skips discussions entirely when the feature is disabled', async () => {
      TestBed.resetTestingModule();
      await setup(makeCurrentUser({ Id: 1 }), false);

      fixture.detectChanges();

      expect(component.discussionsEnabled).toBeFalse();
      expect(discussions.getDiscussions).not.toHaveBeenCalled();
      expect((fixture.nativeElement as HTMLElement).textContent).not.toContain(
        'What members are discussing',
      );
    });

    it('keeps the page usable when the discussions request fails', () => {
      discussions.getDiscussions.and.returnValue(
        throwError(() => new ApiClientClientError('Forbidden', 403)),
      );

      fixture.detectChanges();

      expect(component.recentDiscussions).toEqual([]);
      expect(component.discussionsLoading).toBeFalse();
      expect(component.error).toBe('');
    });

    it('checks membership for a signed-in visitor', () => {
      fixture.detectChanges();

      expect(clubs.getMembershipStatus).toHaveBeenCalledWith(3);
      expect(component.membershipLoading).toBeFalse();
    });

    it('skips the membership check for an anonymous visitor', async () => {
      TestBed.resetTestingModule();
      await setup(null);

      fixture.detectChanges();

      expect(clubs.getMembershipStatus).not.toHaveBeenCalled();
      expect(component.isMember).toBeFalse();
      expect(component.isLoggedIn).toBeFalse();
    });

    it('reports an invalid club URL without fetching', () => {
      route.setParams({ clubId: 'nonsense' });
      fixture.detectChanges();

      expect(component.error).toBe('Invalid club URL.');
      expect(component.loading).toBeFalse();
      expect(clubs.getClub).not.toHaveBeenCalled();
    });

    it('reports a club that could not be loaded', () => {
      clubs.getClub.and.returnValue(
        throwError(() => new ApiClientClientError('No such club.', 404, 'RESOURCE_NOT_FOUND')),
      );

      fixture.detectChanges();

      expect(component.error).toBe('No such club.');
      expect(component.club).toBeNull();
    });

    it('falls back to the envelope message when the club payload is empty', () => {
      clubs.getClub.and.returnValue(of(envelope<Club>(null, { message: 'Club not available.' })));

      fixture.detectChanges();

      expect(component.error).toBe('Club not available.');
    });

    it('tolerates posts and events failing without blocking the page', () => {
      posts.getPosts.and.returnValue(throwError(() => new Error('nope')));
      events.getEventsByClub.and.returnValue(throwError(() => new Error('nope')));

      fixture.detectChanges();

      expect(component.club?.id).toBe(3);
      expect(component.postsLoading).toBeFalse();
      expect(component.eventsLoading).toBeFalse();
    });
  });

  describe('join and leave', () => {
    beforeEach(() => fixture.detectChanges());

    it('joins and bumps the member count', () => {
      clubs.joinClub.and.returnValue(of(envelope(null)));

      component.joinClub();

      expect(clubs.joinClub).toHaveBeenCalledOnceWith(3);
      expect(component.isMember).toBeTrue();
      expect(component.club?.memberCount).toBe(11);
      expect(component.joinLeaveLoading).toBeFalse();
    });

    it('leaves and decrements the member count', () => {
      clubs.leaveClub.and.returnValue(of(envelope(null)));
      component.isMember = true;

      component.leaveClub();

      expect(component.isMember).toBeFalse();
      expect(component.club?.memberCount).toBe(9);
    });

    it('never drives the member count below zero', () => {
      clubs.leaveClub.and.returnValue(of(envelope(null)));
      component.club = makeClub({ id: 3, memberCount: 0 });

      component.leaveClub();

      expect(component.club?.memberCount).toBe(0);
    });

    it('sends an anonymous visitor to log in instead of joining', async () => {
      TestBed.resetTestingModule();
      await setup(null);
      fixture.detectChanges();

      component.joinClub();

      expect(clubs.joinClub).not.toHaveBeenCalled();
      expect(router.navigate).toHaveBeenCalledWith(['/auth/login'], {
        queryParams: { returnUrl: '/clubs/3' },
      });
    });

    it('reports a failed join without changing membership', () => {
      clubs.joinClub.and.returnValue(
        throwError(() => new ApiClientClientError('This club is full.', 409, 'CONFLICT')),
      );

      component.joinClub();

      expect(component.joinError).toBe('This club is full.');
      expect(component.isMember).toBeFalse();
      expect(component.club?.memberCount).toBe(10);
    });

    it('marks a private club as invite-only', () => {
      expect(component.canJoinDirectly).toBeTrue();

      component.club = makeClub({ isPrivate: true });

      expect(component.canJoinDirectly).toBeFalse();
    });
  });

  describe('drill-down panel', () => {
    beforeEach(() => fixture.detectChanges());

    it('loads reviews for the reviews panel', () => {
      reviews.getReviews.and.returnValue(of(paged<ClubReview>([makeReview()], 1)));

      component.openPanel('reviews');

      expect(reviews.getReviews).toHaveBeenCalledWith(3, 1, 10);
      expect(component.panelReviews.length).toBe(1);
      expect(component.panelTotalCount).toBe(1);
      expect(component.panelTitle).toBe('Reviews');
      expect(component.panelSupportsSearch).toBeFalse();
    });

    it('loads members for the members panel', () => {
      management.getMembers.and.returnValue(of(paged<ClubMember>([makeClubMember()], 1)));

      component.openPanel('members');

      expect(management.getMembers).toHaveBeenCalledWith(3, 1, 10, '');
      expect(component.panelMembers.length).toBe(1);
      expect(component.panelSupportsSearch).toBeTrue();
    });

    it('filters to upcoming events for the open-events panel only', () => {
      component.openPanel('events');
      expect(events.getEventsByClub).toHaveBeenCalledWith(3, {
        status: undefined,
        page: 1,
        pageSize: 10,
        search: undefined,
      });

      component.openPanel('openEvents');
      expect(events.getEventsByClub).toHaveBeenCalledWith(3, {
        status: 'Upcoming',
        page: 1,
        pageSize: 10,
        search: undefined,
      });
      expect(component.panelTitle).toBe('Open events');
    });

    it('clears previous panel contents when switching panels', () => {
      management.getMembers.and.returnValue(of(paged<ClubMember>([makeClubMember()], 1)));
      component.openPanel('members');
      expect(component.panelMembers.length).toBe(1);

      component.openPanel('reviews');

      expect(component.panelMembers).toEqual([]);
      expect(component.panelSearch).toBe('');
    });

    it('reports a panel load failure', () => {
      reviews.getReviews.and.returnValue(
        throwError(() => new ApiClientClientError('Reviews are off.', 403, 'FORBIDDEN')),
      );

      component.openPanel('reviews');

      expect(component.panelError).toBe('Reviews are off.');
      expect(component.panelLoading).toBeFalse();
    });

    it('closes on Escape only while a panel is open', () => {
      component.onEscape();
      expect(component.activePanel).toBeNull();

      component.openPanel('reviews');
      component.onEscape();

      expect(component.activePanel).toBeNull();
    });

    it('debounces panel search and resets to the first page', fakeAsync(() => {
      management.getMembers.and.returnValue(of(paged<ClubMember>([], 40)));
      component.openPanel('members');
      component.goToPanelPage(3);
      management.getMembers.calls.reset();

      component.onPanelSearch('ja');
      component.onPanelSearch('jam');
      tick(299);
      expect(management.getMembers).not.toHaveBeenCalled();

      tick(1);
      expect(management.getMembers).toHaveBeenCalledOnceWith(3, 1, 10, 'jam');
      expect(component.panelPage).toBe(1);
    }));

    it('paginates within range and ignores out-of-range or repeat pages', () => {
      reviews.getReviews.and.returnValue(of(paged<ClubReview>([], 25)));
      component.openPanel('reviews');
      expect(component.panelTotalPages).toBe(3);
      reviews.getReviews.calls.reset();

      component.goToPanelPage(0);
      component.goToPanelPage(4);
      component.goToPanelPage(1);
      expect(reviews.getReviews).not.toHaveBeenCalled();

      component.goToPanelPage(2);
      expect(reviews.getReviews).toHaveBeenCalledOnceWith(3, 2, 10);
    });

    it('reports at least one page even with no results', () => {
      expect(component.panelTotalPages).toBe(1);
    });
  });

  describe('reviews', () => {
    beforeEach(() => {
      fixture.detectChanges();
      reviews.getReviews.and.returnValue(of(paged<ClubReview>([makeReview()], 1)));
      component.openPanel('reviews');
    });

    it('opens an empty form for a new review', () => {
      component.openReviewForm();

      expect(component.reviewFormOpen).toBeTrue();
      expect(component.reviewEditingId).toBeNull();
      expect(component.reviewTitle).toBe('');
      expect(component.reviewRating).toBe(0);
    });

    it('pre-fills the form when editing', () => {
      component.openReviewForm(makeReview({ id: 4, title: 'Good', rating: 4, comment: 'Fine' }));

      expect(component.reviewEditingId).toBe(4);
      expect(component.reviewTitle).toBe('Good');
      expect(component.reviewRating).toBe(4);
      expect(component.reviewComment).toBe('Fine');
    });

    it('treats a null comment as an empty string', () => {
      component.openReviewForm(makeReview({ comment: null }));

      expect(component.reviewComment).toBe('');
    });

    it('requires a title', () => {
      component.openReviewForm();
      component.reviewTitle = '   ';
      component.reviewRating = 5;

      component.submitReview();

      expect(reviews.createReview).not.toHaveBeenCalled();
      expect(component.reviewError).toBe('Add a short title for your review.');
    });

    it('requires a star rating', () => {
      component.openReviewForm();
      component.reviewTitle = 'Great';

      component.submitReview();

      expect(reviews.createReview).not.toHaveBeenCalled();
      expect(component.reviewError).toBe('Choose a star rating.');
    });

    it('creates a review, reloads the panel and refreshes the club', () => {
      reviews.createReview.and.returnValue(of(envelope(makeReview())));
      clubs.getClub.calls.reset();
      component.openReviewForm();
      component.reviewTitle = '  Great club  ';
      component.setReviewRating(5);
      component.reviewComment = '  Welcoming  ';

      component.submitReview();

      expect(reviews.createReview).toHaveBeenCalledOnceWith(3, {
        title: 'Great club',
        rating: 5,
        comment: 'Welcoming',
      });
      expect(component.reviewFormOpen).toBeFalse();
      // The club's average rating may have shifted.
      expect(clubs.getClub).toHaveBeenCalledTimes(1);
    });

    it('sends a null comment when the body is blank', () => {
      reviews.createReview.and.returnValue(of(envelope(makeReview())));
      component.openReviewForm();
      component.reviewTitle = 'Great';
      component.setReviewRating(4);
      component.reviewComment = '   ';

      component.submitReview();

      expect(reviews.createReview).toHaveBeenCalledOnceWith(3, {
        title: 'Great',
        rating: 4,
        comment: null,
      });
    });

    it('updates rather than creates when editing', () => {
      reviews.updateReview.and.returnValue(of(envelope(makeReview())));
      component.openReviewForm(makeReview({ id: 4 }));
      component.reviewTitle = 'Edited';
      component.setReviewRating(3);

      component.submitReview();

      expect(reviews.updateReview).toHaveBeenCalledOnceWith(3, 4, {
        title: 'Edited',
        rating: 3,
        comment: 'Really welcoming',
      });
      expect(reviews.createReview).not.toHaveBeenCalled();
    });

    it('keeps the form open when submitting fails', () => {
      reviews.createReview.and.returnValue(
        throwError(() => new ApiClientClientError('Already reviewed.', 409, 'CONFLICT')),
      );
      component.openReviewForm();
      component.reviewTitle = 'Great';
      component.setReviewRating(5);

      component.submitReview();

      expect(component.reviewError).toBe('Already reviewed.');
      expect(component.reviewFormOpen).toBeTrue();
      expect(component.reviewSubmitting).toBeFalse();
    });

    it('deletes a review and refreshes the panel and the club', () => {
      reviews.deleteReview.and.returnValue(of(envelope(null)));
      clubs.getClub.calls.reset();
      reviews.getReviews.calls.reset();

      component.deleteReview(makeReview({ id: 4 }));

      expect(reviews.deleteReview).toHaveBeenCalledOnceWith(3, 4);
      expect(reviews.getReviews).toHaveBeenCalledTimes(1);
      expect(clubs.getClub).toHaveBeenCalledTimes(1);
      expect(component.reviewDeletingId).toBeNull();
    });

    it('closes the edit form when the review being edited is deleted', () => {
      reviews.deleteReview.and.returnValue(of(envelope(null)));
      component.openReviewForm(makeReview({ id: 4 }));

      component.deleteReview(makeReview({ id: 4 }));

      expect(component.reviewFormOpen).toBeFalse();
    });

    it('reports a failed delete', () => {
      reviews.deleteReview.and.returnValue(
        throwError(() => new ApiClientClientError('Not yours.', 403, 'FORBIDDEN')),
      );

      component.deleteReview(makeReview({ id: 4 }));

      expect(component.reviewError).toBe('Not yours.');
      expect(component.reviewDeletingId).toBeNull();
    });

    it('allows editing only the signed-in user’s own review', () => {
      expect(component.canEditReview(makeReview({ userId: 1 }))).toBeTrue();
      expect(component.canEditReview(makeReview({ userId: 5 }))).toBeFalse();
    });
  });

  describe('navigation', () => {
    beforeEach(() => fixture.detectChanges());

    it('routes back, to posts and to management', () => {
      component.goBack();
      expect(router.navigate).toHaveBeenCalledWith(['/clubs']);

      component.viewPosts();
      expect(router.navigate).toHaveBeenCalledWith(['/clubs', 3, 'posts']);

      component.manageClub();
      expect(router.navigate).toHaveBeenCalledWith(['/clubs', 3, 'manage']);
    });

    it('routes to a post and to an event', () => {
      component.navigateToPost({ id: 8 } as never);
      expect(router.navigate).toHaveBeenCalledWith(['/clubs', 3, 'posts', 8]);

      component.navigateToEvent(makeEventItem({ id: 12 }));
      expect(router.navigate).toHaveBeenCalledWith(['/events', 12]);
    });
  });

  describe('display helpers', () => {
    beforeEach(() => fixture.detectChanges());

    it('builds club initials from the first two words, or a fallback', () => {
      component.club = makeClub({ name: 'Robotics Club' });
      expect(component.clubInitials).toBe('RC');

      component.club = makeClub({ name: 'Chess' });
      expect(component.clubInitials).toBe('CH');

      component.club = makeClub({ name: '   ' });
      expect(component.clubInitials).toBe('?');
    });

    it('names members and reviewers, falling back to the user id', () => {
      expect(component.memberName(makeClubMember({ name: 'Jamie' }))).toBe('Jamie');
      expect(component.memberName(makeClubMember({ name: null, username: 'jr' }))).toBe('jr');
      expect(
        component.memberName(
          makeClubMember({ name: null, username: null, usernameDisplay: null, userId: 42 }),
        ),
      ).toBe('User #42');

      expect(
        component.reviewerName(
          makeReview({ name: null, username: null, usernameDisplay: null, userId: 7 }),
        ),
      ).toBe('User #7');
    });

    it('builds member initials', () => {
      expect(component.memberInitials(makeClubMember({ name: 'Jamie' }))).toBe('JA');
      expect(
        component.memberInitials(
          makeClubMember({ name: null, username: null, usernameDisplay: null, userId: 42 }),
        ),
      ).toBe('#4');
    });

    it('renders a five-star bar rounded to the nearest star', () => {
      expect(component.starsFor(3.4)).toEqual([1, 1, 1, 0, 0]);
      expect(component.starsFor(3.6)).toEqual([1, 1, 1, 1, 0]);
      expect(component.starsFor(0)).toEqual([0, 0, 0, 0, 0]);
    });

    it('reports member capacity as a clamped percentage', () => {
      component.club = makeClub({ memberCount: 25, maxMemberCount: 50 });
      expect(component.memberCapacityPercent()).toBe(50);

      component.club = makeClub({ memberCount: 80, maxMemberCount: 50 });
      expect(component.memberCapacityPercent()).toBe(100);

      component.club = makeClub({ maxMemberCount: 0 });
      expect(component.memberCapacityPercent()).toBe(0);

      component.club = null;
      expect(component.memberCapacityPercent()).toBe(0);
    });

    it('reports event registration as a clamped percentage', () => {
      expect(
        component.registrationPercent(
          makeEventItem({ registrationCount: 10, maxParticipants: 40 }),
        ),
      ).toBe(25);
      expect(
        component.registrationPercent(makeEventItem({ registrationCount: 10, maxParticipants: 0 })),
      ).toBe(0);
    });

    it('labels a zero cost as Free', () => {
      expect(component.formatCost(0)).toBe('Free');
      expect(component.formatCost(15)).toBe('$15');
    });

    it('names a post author, falling back to the user id', () => {
      expect(component.authorDisplay({ author: { name: 'Jamie' }, userId: 1 } as never)).toBe(
        'Jamie',
      );
      expect(component.authorDisplay({ author: null, userId: 42 } as never)).toBe('User #42');
    });

    it('formats dates and times without throwing', () => {
      expect(component.formatDate('2026-08-05T12:00:00Z')).toContain('2026');
      expect(component.formatEventDate('2026-08-05T12:00:00Z')).toBeTruthy();
      expect(component.formatEventTime('2026-08-05T12:00:00Z')).toMatch(/\d{2}:\d{2}/);
    });
  });

  describe('share', () => {
    beforeEach(() => fixture.detectChanges());

    it('copies the page URL and clears the confirmation after two seconds', fakeAsync(() => {
      const writeText = jasmine.createSpy('writeText').and.resolveTo();
      Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

      component.shareClub();
      tick();

      expect(writeText).toHaveBeenCalledOnceWith(window.location.href);
      expect(component.shareCopied).toBeTrue();

      tick(2000);
      expect(component.shareCopied).toBeFalse();
    }));

    it('does nothing when the clipboard API is unavailable', () => {
      Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true });

      expect(() => component.shareClub()).not.toThrow();
      expect(component.shareCopied).toBeFalse();
    });
  });
});
