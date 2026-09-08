import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { finalize } from 'rxjs/operators';

import { getApiClientMessage } from '../../../../../core/api/models/api-client-error.model';
import { ClubReview } from '../../../models/club-review.types';
import { Club } from '../../../models/club.types';
import { ClubReviewsService } from '../../../services/club-reviews.service';
import { ClubsService } from '../../../services/clubs.service';

@Component({
  selector: 'app-reviews-tab',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './reviews-tab.component.html',
})
export class ReviewsTabComponent implements OnInit {
  private readonly destroyRef = inject(DestroyRef);

  clubId = 0;
  club: Club | null = null;
  reviews: ClubReview[] = [];
  loading = true;
  error = '';

  page = 1;
  readonly pageSize = 20;
  totalCount = 0;
  readonly skeletons = Array.from({ length: 5 });

  constructor(
    private route: ActivatedRoute,
    private reviewsService: ClubReviewsService,
    private clubsService: ClubsService,
  ) {}

  ngOnInit(): void {
    this.clubId =
      Number.parseInt(this.route.parent?.snapshot.paramMap.get('clubId') ?? '', 10) || 0;
    if (!this.clubId) {
      this.loading = false;
      this.error = 'A valid club ID is required.';
      return;
    }
    this.loadClub();
    this.loadReviews();
  }

  get totalPages(): number {
    return Math.max(1, Math.ceil(this.totalCount / this.pageSize));
  }

  reviewerName(review: ClubReview): string {
    return review.name || review.usernameDisplay || review.username || `User #${review.userId}`;
  }

  reviewerInitials(review: ClubReview): string {
    const source = review.name || review.username || '';
    return source ? source.slice(0, 2).toUpperCase() : `#${review.userId}`.slice(0, 2);
  }

  starsFor(rating: number): number[] {
    return [1, 2, 3, 4, 5].map((n) => (n <= Math.round(rating) ? 1 : 0));
  }

  goToPage(page: number): void {
    if (page < 1 || page > this.totalPages || page === this.page) return;
    this.page = page;
    this.loadReviews();
  }

  private loadClub(): void {
    this.clubsService
      .getClub(this.clubId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.club = response.data ?? null;
        },
        // The average-rating summary is best-effort.
        error: () => {
          /* no-op */
        },
      });
  }

  private loadReviews(): void {
    this.loading = true;
    this.error = '';
    this.reviewsService
      .getReviews(this.clubId, this.page, this.pageSize)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.loading = false)),
      )
      .subscribe({
        next: (response) => {
          this.reviews = response.data?.items ?? [];
          this.totalCount = response.data?.totalCount ?? 0;
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to load reviews.');
        },
      });
  }
}
