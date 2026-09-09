import { Component, HostListener, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Store } from '@ngrx/store';
import { Subject, takeUntil, finalize, debounceTime, distinctUntilChanged } from 'rxjs';

import { selectUser } from '../../../../core/stores/user.selectors';
import { User } from '../../../../core/stores/user.model';
import { ClubsService } from '../../services/clubs.service';
import { Club, CLUB_TYPE_STYLES } from '../../models/club.types';
import { ClubPostsService } from '../../services/club-posts.service';
import { ClubPost, POST_TYPE_STYLES } from '../../models/club-post.types';
import { ClubDiscussionsService } from '../../services/club-discussions.service';
import { ClubDiscussion, discussionAuthorName } from '../../models/club-discussion.types';
import { ClubReview } from '../../models/club-review.types';
import { ClubReviewsService } from '../../services/club-reviews.service';
import { ClubMember } from '../../models/club-management.types';
import { ClubManagementService } from '../../services/club-management.service';
import { EventsService } from '../../../events/services/events.service';
import { getApiClientMessage } from '../../../../core/api/models/api-client-error.model';
import { FeatureFlagsService } from '../../../../core/features/feature-flags.service';
import { FEATURE_KEYS } from '../../../../core/features/feature-flags.types';
import { CATEGORY_STYLES, EventItem } from '../../../events/models/event.types';

export type DetailPanel = 'members' | 'events' | 'openEvents' | 'reviews';

@Component({
  selector: 'app-club-detail',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './club-detail.component.html',
})
export class ClubDetailComponent implements OnInit, OnDestroy {
  clubId = 0;
  club: Club | null = null;
  loading = true;
  error = '';

  currentUser: User | null = null;
  isMember = false;
  membershipLoading = false;
  joinLeaveLoading = false;
  joinError = '';

  shareCopied = false;
  private shareResetTimer: ReturnType<typeof setTimeout> | null = null;

  recentPosts: ClubPost[] = [];
  postsLoading = false;

  recentDiscussions: ClubDiscussion[] = [];
  discussionsLoading = false;
  /** Mirrors the route's `featureCanMatch` guard, so a disabled flag leaves no dead entry point. */
  readonly discussionsEnabled: boolean;

  upcomingEvents: EventItem[] = [];
  eventsLoading = false;

  // Drill-down modal state
  activePanel: DetailPanel | null = null;
  panelLoading = false;
  panelError = '';
  panelPage = 1;
  readonly panelPageSize = 10;
  panelTotalCount = 0;
  panelMembers: ClubMember[] = [];
  panelEvents: EventItem[] = [];
  panelReviews: ClubReview[] = [];

  // Search within the members/events panels (server-side, debounced)
  panelSearch = '';
  private readonly panelSearch$ = new Subject<string>();

  // Review compose/edit state (within the reviews panel)
  reviewFormOpen = false;
  reviewEditingId: number | null = null;
  reviewTitle = '';
  reviewRating = 0;
  reviewComment = '';
  reviewSubmitting = false;
  reviewError = '';
  reviewDeletingId: number | null = null;
  readonly reviewTitleMax = 100;
  readonly reviewCommentMax = 500;

  readonly clubTypeStyles = CLUB_TYPE_STYLES;
  readonly postTypeStyles = POST_TYPE_STYLES;
  readonly categoryStyles = CATEGORY_STYLES;

  private readonly destroy$ = new Subject<void>();

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private clubsService: ClubsService,
    private postsService: ClubPostsService,
    private discussionsService: ClubDiscussionsService,
    private eventsService: EventsService,
    private reviewsService: ClubReviewsService,
    private managementService: ClubManagementService,
    private store: Store,
    featureFlags: FeatureFlagsService,
  ) {
    this.discussionsEnabled = featureFlags.isEnabled(FEATURE_KEYS.clubsDiscussions);
  }

  ngOnInit(): void {
    this.store
      .select(selectUser)
      .pipe(takeUntil(this.destroy$))
      .subscribe((user) => {
        this.currentUser = user;
        if (this.clubId && user) {
          this.fetchMembership();
        } else {
          this.isMember = false;
        }
      });

    this.route.paramMap.pipe(takeUntil(this.destroy$)).subscribe((params) => {
      this.clubId = Number(params.get('clubId')) || 0;
      if (this.clubId) {
        this.fetchClub();
        this.fetchRecentPosts();
        this.fetchRecentDiscussions();
        this.fetchUpcomingEvents();
        if (this.currentUser) {
          this.fetchMembership();
        }
      } else {
        this.loading = false;
        this.error = 'Invalid club URL.';
      }
    });

    this.panelSearch$
      .pipe(debounceTime(300), distinctUntilChanged(), takeUntil(this.destroy$))
      .subscribe(() => {
        this.panelPage = 1;
        this.loadPanel();
      });
  }

  /** Members and events panels support text search; reviews has its own compose UI. */
  get panelSupportsSearch(): boolean {
    return (
      this.activePanel === 'members' ||
      this.activePanel === 'events' ||
      this.activePanel === 'openEvents'
    );
  }

  onPanelSearch(value: string): void {
    this.panelSearch = value;
    this.panelSearch$.next(value);
  }

  get isLoggedIn(): boolean {
    return this.currentUser !== null;
  }

  /** Two-letter fallback badge for the club logo when no image is set. */
  get clubInitials(): string {
    const name = this.club?.name?.trim();
    if (!name) return '?';
    const words = name.split(/\s+/).filter(Boolean);
    const letters = words.length > 1 ? words[0][0] + words[1][0] : name.slice(0, 2);
    return letters.toUpperCase();
  }

  /** Copies the current club URL to the clipboard, with a transient "Copied" state. */
  shareClub(): void {
    if (typeof navigator === 'undefined' || !navigator.clipboard) {
      return;
    }

    void navigator.clipboard.writeText(window.location.href).then(() => {
      this.shareCopied = true;
      if (this.shareResetTimer) {
        clearTimeout(this.shareResetTimer);
      }
      this.shareResetTimer = setTimeout(() => {
        this.shareCopied = false;
        this.shareResetTimer = null;
      }, 2000);
    });
  }

  /** A public club can be joined directly; a private one is invite-only. */
  get canJoinDirectly(): boolean {
    return !!this.club && !this.club.isPrivate;
  }

  joinClub(): void {
    if (!this.club || this.joinLeaveLoading) {
      return;
    }
    if (!this.isLoggedIn) {
      void this.router.navigate(['/auth/login'], {
        queryParams: { returnUrl: this.router.url },
      });
      return;
    }

    this.joinLeaveLoading = true;
    this.joinError = '';
    this.clubsService
      .joinClub(this.clubId)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.joinLeaveLoading = false;
          this.isMember = true;
          if (this.club) {
            this.club = { ...this.club, memberCount: this.club.memberCount + 1 };
          }
        },
        error: (err) => {
          this.joinLeaveLoading = false;
          this.joinError = getApiClientMessage(err, 'Unable to join this club.');
        },
      });
  }

  leaveClub(): void {
    if (!this.club || this.joinLeaveLoading) {
      return;
    }

    this.joinLeaveLoading = true;
    this.joinError = '';
    this.clubsService
      .leaveClub(this.clubId)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.joinLeaveLoading = false;
          this.isMember = false;
          if (this.club) {
            this.club = {
              ...this.club,
              memberCount: Math.max(0, this.club.memberCount - 1),
            };
          }
        },
        error: (err) => {
          this.joinLeaveLoading = false;
          this.joinError = getApiClientMessage(err, 'Unable to leave this club.');
        },
      });
  }

  private fetchMembership(): void {
    if (!this.clubId) {
      return;
    }
    this.membershipLoading = true;
    this.clubsService
      .getMembershipStatus(this.clubId)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (isMember) => {
          this.isMember = isMember;
          this.membershipLoading = false;
        },
        // Membership state is best-effort; a failure just hides the "joined" state.
        error: () => {
          this.membershipLoading = false;
        },
      });
  }

  ngOnDestroy(): void {
    if (this.shareResetTimer) {
      clearTimeout(this.shareResetTimer);
    }
    this.destroy$.next();
    this.destroy$.complete();
  }

  goBack(): void {
    this.router.navigate(['/clubs']);
  }

  viewPosts(): void {
    this.router.navigate(['/clubs', this.clubId, 'posts']);
  }

  viewDiscussions(): void {
    this.router.navigate(['/clubs', this.clubId, 'discussions']);
  }

  discussionAuthor(discussion: ClubDiscussion): string {
    return discussionAuthorName(discussion);
  }

  manageClub(): void {
    this.router.navigate(['/clubs', this.clubId, 'manage']);
  }

  // ---- Drill-down modal ----------------------------------------------------

  openPanel(panel: DetailPanel): void {
    this.activePanel = panel;
    this.panelPage = 1;
    this.panelError = '';
    this.panelSearch = '';
    this.closeReviewForm();
    this.panelMembers = [];
    this.panelEvents = [];
    this.panelReviews = [];
    this.panelTotalCount = 0;
    this.loadPanel();
  }

  closePanel(): void {
    this.activePanel = null;
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.activePanel) this.closePanel();
  }

  get panelTotalPages(): number {
    return Math.max(1, Math.ceil(this.panelTotalCount / this.panelPageSize));
  }

  get panelTitle(): string {
    switch (this.activePanel) {
      case 'members':
        return 'Members';
      case 'events':
        return 'All events';
      case 'openEvents':
        return 'Open events';
      case 'reviews':
        return 'Reviews';
      default:
        return '';
    }
  }

  goToPanelPage(page: number): void {
    if (page < 1 || page > this.panelTotalPages || page === this.panelPage) return;
    this.panelPage = page;
    this.loadPanel();
  }

  memberName(member: ClubMember): string {
    return member.name || member.username || `User #${member.userId}`;
  }

  memberInitials(member: ClubMember): string {
    const source = member.name || member.username || '';
    return source ? source.slice(0, 2).toUpperCase() : `#${member.userId}`.slice(0, 2);
  }

  reviewerName(review: ClubReview): string {
    return review.name || review.usernameDisplay || review.username || `User #${review.userId}`;
  }

  starsFor(rating: number): number[] {
    return [1, 2, 3, 4, 5].map((n) => (n <= Math.round(rating) ? 1 : 0));
  }

  // ---- Review compose / edit / delete -------------------------------------

  canEditReview(review: ClubReview): boolean {
    return this.currentUser != null && review.userId === this.currentUser.Id;
  }

  openReviewForm(existing?: ClubReview): void {
    this.reviewError = '';
    if (existing) {
      this.reviewEditingId = existing.id;
      this.reviewTitle = existing.title;
      this.reviewRating = existing.rating;
      this.reviewComment = existing.comment ?? '';
    } else {
      this.reviewEditingId = null;
      this.reviewTitle = '';
      this.reviewRating = 0;
      this.reviewComment = '';
    }
    this.reviewFormOpen = true;
  }

  closeReviewForm(): void {
    this.reviewFormOpen = false;
    this.reviewEditingId = null;
    this.reviewTitle = '';
    this.reviewRating = 0;
    this.reviewComment = '';
    this.reviewError = '';
  }

  setReviewRating(value: number): void {
    this.reviewRating = value;
  }

  submitReview(): void {
    const title = this.reviewTitle.trim();
    if (!title) {
      this.reviewError = 'Add a short title for your review.';
      return;
    }
    if (this.reviewRating < 1 || this.reviewRating > 5) {
      this.reviewError = 'Choose a star rating.';
      return;
    }

    const payload = {
      title,
      rating: this.reviewRating,
      comment: this.reviewComment.trim() || null,
    };

    this.reviewSubmitting = true;
    this.reviewError = '';

    const request$ = this.reviewEditingId
      ? this.reviewsService.updateReview(this.clubId, this.reviewEditingId, payload)
      : this.reviewsService.createReview(this.clubId, payload);

    request$
      .pipe(
        takeUntil(this.destroy$),
        finalize(() => (this.reviewSubmitting = false)),
      )
      .subscribe({
        next: () => {
          this.closeReviewForm();
          this.panelPage = 1;
          this.loadPanel();
          // The average rating shown in the hero/stat tile may have shifted.
          this.fetchClub();
        },
        error: (err) => {
          this.reviewError = getApiClientMessage(err, 'Unable to submit your review.');
        },
      });
  }

  deleteReview(review: ClubReview): void {
    this.reviewDeletingId = review.id;
    this.reviewError = '';

    this.reviewsService
      .deleteReview(this.clubId, review.id)
      .pipe(
        takeUntil(this.destroy$),
        finalize(() => (this.reviewDeletingId = null)),
      )
      .subscribe({
        next: () => {
          if (this.reviewEditingId === review.id) {
            this.closeReviewForm();
          }
          this.loadPanel();
          this.fetchClub();
        },
        error: (err) => {
          this.reviewError = getApiClientMessage(err, 'Unable to delete this review.');
        },
      });
  }

  private loadPanel(): void {
    const panel = this.activePanel;
    if (!panel) return;

    this.panelLoading = true;
    this.panelError = '';

    if (panel === 'reviews') {
      this.reviewsService
        .getReviews(this.clubId, this.panelPage, this.panelPageSize)
        .pipe(takeUntil(this.destroy$))
        .subscribe({
          next: (response) => {
            this.panelReviews = response.data?.items ?? [];
            this.panelTotalCount = response.data?.totalCount ?? 0;
            this.panelLoading = false;
          },
          error: (err) => {
            this.panelError = getApiClientMessage(err, 'Unable to load reviews.');
            this.panelLoading = false;
          },
        });
      return;
    }

    if (panel === 'members') {
      this.managementService
        .getMembers(this.clubId, this.panelPage, this.panelPageSize, this.panelSearch)
        .pipe(takeUntil(this.destroy$))
        .subscribe({
          next: (response) => {
            this.panelMembers = response.data?.items ?? [];
            this.panelTotalCount = response.data?.totalCount ?? 0;
            this.panelLoading = false;
          },
          error: (err) => {
            this.panelError = getApiClientMessage(err, 'Unable to load members.');
            this.panelLoading = false;
          },
        });
      return;
    }

    // events | openEvents
    this.eventsService
      .getEventsByClub(this.clubId, {
        status: panel === 'openEvents' ? 'Upcoming' : undefined,
        page: this.panelPage,
        pageSize: this.panelPageSize,
        search: this.panelSearch || undefined,
      })
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (response) => {
          this.panelEvents = response.data?.items ?? [];
          this.panelTotalCount = response.data?.totalCount ?? 0;
          this.panelLoading = false;
        },
        error: (err) => {
          this.panelError = getApiClientMessage(err, 'Unable to load events.');
          this.panelLoading = false;
        },
      });
  }

  navigateToPost(post: ClubPost): void {
    this.router.navigate(['/clubs', this.clubId, 'posts', post.id]);
  }

  navigateToEvent(event: EventItem): void {
    this.router.navigate(['/events', event.id]);
  }

  memberCapacityPercent(): number {
    if (!this.club || this.club.maxMemberCount <= 0) return 0;
    return Math.min(100, (this.club.memberCount / this.club.maxMemberCount) * 100);
  }

  registrationPercent(event: EventItem): number {
    if (event.maxParticipants <= 0) return 0;
    return Math.min(100, (event.registrationCount / event.maxParticipants) * 100);
  }

  authorDisplay(post: ClubPost): string {
    return (
      post.author?.name ??
      post.author?.usernameDisplay ??
      post.author?.username ??
      `User #${post.userId}`
    );
  }

  formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString('en-CA', {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
    });
  }

  formatEventDate(iso: string): string {
    return new Date(iso).toLocaleDateString('en-CA', {
      weekday: 'short',
      month: 'short',
      day: 'numeric',
    });
  }

  formatEventTime(iso: string): string {
    return new Date(iso).toLocaleTimeString('en-CA', { hour: '2-digit', minute: '2-digit' });
  }

  formatCost(cost: number): string {
    return cost === 0 ? 'Free' : `$${cost}`;
  }

  private fetchClub(): void {
    this.loading = true;
    this.error = '';

    this.clubsService
      .getClub(this.clubId)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (response) => {
          this.club = response.data ?? null;
          this.loading = false;
          if (!this.club) this.error = response.message || 'Club not found.';
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Failed to load club.');
          this.loading = false;
        },
      });
  }

  private fetchRecentPosts(): void {
    this.postsLoading = true;

    this.postsService
      .getPosts(this.clubId, { sortBy: 'Recent', page: 1, pageSize: 3 })
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (response) => {
          this.recentPosts = response.data?.items ?? [];
          this.postsLoading = false;
        },
        error: () => {
          this.postsLoading = false;
        },
      });
  }

  private fetchRecentDiscussions(): void {
    if (!this.discussionsEnabled) return;

    this.discussionsLoading = true;

    // Errors are swallowed on purpose: a disabled feature flag or a private-club 403
    // should hide the section, not break the whole club page.
    this.discussionsService
      .getDiscussions(this.clubId, 1, 3)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (response) => {
          this.recentDiscussions = response.data?.items ?? [];
          this.discussionsLoading = false;
        },
        error: () => {
          this.discussionsLoading = false;
        },
      });
  }

  private fetchUpcomingEvents(): void {
    this.eventsLoading = true;

    this.eventsService
      .getEventsByClub(this.clubId, { status: 'Upcoming', page: 1, pageSize: 3 })
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (response) => {
          this.upcomingEvents = response.data?.items ?? [];
          this.eventsLoading = false;
        },
        error: () => {
          this.eventsLoading = false;
        },
      });
  }
}
