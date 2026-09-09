import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged, takeUntil } from 'rxjs';

import { ClubPostsService, ClubPostsSearchParams } from '../../services/club-posts.service';
import {
  ClubPost,
  POST_TYPE_STYLES,
  PostSortBy,
  ALL_POST_SORTS,
} from '../../models/club-post.types';

@Component({
  selector: 'app-club-posts',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule],
  templateUrl: './club-posts.component.html',
})
export class ClubPostsComponent implements OnInit, OnDestroy {
  clubId = 0;
  posts: ClubPost[] = [];
  totalCount = 0;
  totalPages = 0;
  currentPage = 1;
  readonly pageSize = 20;

  searchQuery = '';
  selectedSort: PostSortBy = 'Recent';
  loading = false;
  loadingMore = false;
  error = '';

  readonly allSorts = ALL_POST_SORTS;
  readonly postTypeStyles = POST_TYPE_STYLES;

  private readonly destroy$ = new Subject<void>();
  private readonly searchInput$ = new Subject<string>();
  private requestVersion = 0;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private postsService: ClubPostsService,
  ) {}

  ngOnInit(): void {
    this.route.paramMap.pipe(takeUntil(this.destroy$)).subscribe((params) => {
      const id = Number(params.get('clubId'));
      this.clubId = id > 0 ? id : 0;
      this.resetAndFetch();
    });

    this.searchInput$
      .pipe(debounceTime(350), distinctUntilChanged(), takeUntil(this.destroy$))
      .subscribe(() => this.resetAndFetch());
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  onSearchChange(value: string): void {
    this.searchQuery = value;
    this.searchInput$.next(value);
  }

  setSort(sort: PostSortBy): void {
    if (this.selectedSort === sort) return;
    this.selectedSort = sort;
    this.resetAndFetch();
  }

  loadMore(): void {
    if (this.currentPage >= this.totalPages || this.loadingMore) return;
    this.currentPage++;
    this.fetch(true);
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

  goBack(): void {
    this.router.navigate(['/clubs', this.clubId]);
  }

  navigateToPost(post: ClubPost): void {
    this.router.navigate(['/clubs', this.clubId, 'posts', post.id]);
  }

  private resetAndFetch(): void {
    this.currentPage = 1;
    this.posts = [];
    this.fetch(false);
  }

  private fetch(append: boolean): void {
    if (!this.clubId) return;

    const version = ++this.requestVersion;
    if (append) {
      this.loadingMore = true;
    } else {
      this.loading = true;
      this.error = '';
    }

    const params: ClubPostsSearchParams = {
      search: this.searchQuery || undefined,
      sortBy: this.selectedSort,
      page: this.currentPage,
      pageSize: this.pageSize,
    };

    this.postsService
      .getPosts(this.clubId, params)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (response) => {
          if (version !== this.requestVersion) return;
          const data = response.data ?? null;
          if (data) {
            this.posts = append ? [...this.posts, ...data.items] : data.items;
            this.totalCount = data.totalCount;
            this.totalPages = data.totalPages || Math.ceil(data.totalCount / this.pageSize);
          }
          this.loading = false;
          this.loadingMore = false;
        },
        error: (err) => {
          if (version !== this.requestVersion) return;
          this.error = err?.error?.message || err?.error?.Message || 'Failed to load posts.';
          this.loading = false;
          this.loadingMore = false;
        },
      });
  }
}
