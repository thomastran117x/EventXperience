import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { finalize } from 'rxjs/operators';

import { getApiClientMessage } from '../../../../../core/api/models/api-client-error.model';
import { CATEGORY_STYLES, EventItem } from '../../../../events/models/event.types';
import { EventsService } from '../../../../events/services/events.service';
import { ClubAnalytics } from '../../../models/club-management.types';
import { ClubPost, POST_TYPE_STYLES } from '../../../models/club-post.types';
import { Club, CLUB_TYPE_STYLES } from '../../../models/club.types';
import { ClubManagementService } from '../../../services/club-management.service';
import { ClubPostsService } from '../../../services/club-posts.service';
import { ClubsService } from '../../../services/clubs.service';

@Component({
  selector: 'app-overview-tab',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './overview-tab.component.html',
})
export class OverviewTabComponent implements OnInit {
  private readonly destroyRef = inject(DestroyRef);

  clubId = 0;
  club: Club | null = null;
  loading = true;
  error = '';

  recentPosts: ClubPost[] = [];
  postsLoading = true;

  upcomingEvents: EventItem[] = [];
  eventsLoading = true;

  analytics: ClubAnalytics | null = null;
  analyticsLoading = true;

  readonly clubTypeStyles = CLUB_TYPE_STYLES;
  readonly postTypeStyles = POST_TYPE_STYLES;
  readonly categoryStyles = CATEGORY_STYLES;

  constructor(
    private route: ActivatedRoute,
    private clubsService: ClubsService,
    private postsService: ClubPostsService,
    private eventsService: EventsService,
    private management: ClubManagementService,
  ) {}

  ngOnInit(): void {
    this.clubId =
      Number.parseInt(this.route.parent?.snapshot.paramMap.get('clubId') ?? '', 10) || 0;
    if (!this.clubId) {
      this.loading = false;
      this.postsLoading = false;
      this.eventsLoading = false;
      this.analyticsLoading = false;
      this.error = 'A valid club ID is required.';
      return;
    }
    this.load();
    this.loadRecentPosts();
    this.loadUpcomingEvents();
    this.loadAnalytics();
  }

  memberCapacityPercent(): number {
    if (!this.club || this.club.maxMemberCount <= 0) return 0;
    return Math.min(100, (this.club.memberCount / this.club.maxMemberCount) * 100);
  }

  /** Revenue values are stored in cents. */
  toDollars(cents: number): number {
    return cents / 100;
  }

  private loadAnalytics(): void {
    this.analyticsLoading = true;
    this.management
      .getAnalytics(this.clubId)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.analyticsLoading = false)),
      )
      .subscribe({
        next: (response) => {
          this.analytics = response.data ?? null;
        },
        // Health indicators are best-effort; keep the overview usable if analytics fail.
        error: () => {
          this.analytics = null;
        },
      });
  }

  authorDisplay(post: ClubPost): string {
    return (
      post.author?.name ??
      post.author?.usernameDisplay ??
      post.author?.username ??
      `User #${post.userId}`
    );
  }

  private load(): void {
    this.loading = true;
    this.error = '';
    this.clubsService
      .getClub(this.clubId)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.loading = false)),
      )
      .subscribe({
        next: (response) => {
          this.club = response.data ?? null;
          if (!this.club) this.error = response.message || 'Club not found.';
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to load this club.');
        },
      });
  }

  private loadRecentPosts(): void {
    this.postsLoading = true;
    this.postsService
      .getPosts(this.clubId, { sortBy: 'Recent', page: 1, pageSize: 3 })
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.postsLoading = false)),
      )
      .subscribe({
        next: (response) => {
          this.recentPosts = response.data?.items ?? [];
        },
        error: () => {
          this.recentPosts = [];
        },
      });
  }

  private loadUpcomingEvents(): void {
    this.eventsLoading = true;
    this.eventsService
      .getEventsByClub(this.clubId, { status: 'Upcoming', page: 1, pageSize: 3 })
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.eventsLoading = false)),
      )
      .subscribe({
        next: (response) => {
          this.upcomingEvents = response.data?.items ?? [];
        },
        error: () => {
          this.upcomingEvents = [];
        },
      });
  }
}
