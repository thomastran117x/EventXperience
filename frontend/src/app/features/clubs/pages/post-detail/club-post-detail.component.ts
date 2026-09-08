import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { Subject, takeUntil } from 'rxjs';
import { Store } from '@ngrx/store';

import { ClubPostsService } from '../../services/club-posts.service';
import { ClubPost, POST_TYPE_STYLES } from '../../models/club-post.types';
import { ThreadComponent } from '../../components/thread/thread.component';
import {
  ThreadDataSource,
  createPostThreadSource,
} from '../../components/thread/thread-data-source';
import { ThreadDisplayItem } from '../../components/thread/thread-item';
import { PostCommentsService } from '../../services/post-comments.service';
import { User } from '../../../../core/stores/user.model';
import { selectUser } from '../../../../core/stores/user.selectors';

@Component({
  selector: 'app-club-post-detail',
  standalone: true,
  imports: [CommonModule, ThreadComponent],
  templateUrl: './club-post-detail.component.html',
})
export class ClubPostDetailComponent implements OnInit, OnDestroy {
  clubId = 0;
  postId = 0;
  post: ClubPost | null = null;
  loading = true;
  error = '';
  currentUser: User | null = null;

  readonly postTypeStyles = POST_TYPE_STYLES;

  /**
   * Built once per post. The thread component treats a new `source` reference as a
   * different thread, so this must not be rebuilt on every render.
   */
  threadSource: ThreadDataSource<ThreadDisplayItem> | null = null;

  private readonly destroy$ = new Subject<void>();

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private postsService: ClubPostsService,
    private commentsService: PostCommentsService,
    private store: Store,
  ) {}

  ngOnInit(): void {
    this.store
      .select(selectUser)
      .pipe(takeUntil(this.destroy$))
      .subscribe((user) => (this.currentUser = user));

    this.route.paramMap.pipe(takeUntil(this.destroy$)).subscribe((params) => {
      this.clubId = Number(params.get('clubId')) || 0;
      this.postId = Number(params.get('postId')) || 0;
      if (this.clubId && this.postId) {
        this.threadSource = createPostThreadSource(this.commentsService, this.clubId, this.postId);
        this.fetch();
      } else {
        this.loading = false;
        this.error = 'Invalid post URL.';
      }
    });
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  goBack(): void {
    this.router.navigate(['/clubs', this.clubId, 'posts']);
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
      weekday: 'short',
      month: 'short',
      day: 'numeric',
      year: 'numeric',
    });
  }

  updateCommentCount(count: number): void {
    if (this.post) this.post.commentCount = count;
  }

  private fetch(): void {
    this.loading = true;
    this.error = '';

    this.postsService
      .getPost(this.clubId, this.postId)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (response) => {
          const data = response.data ?? null;
          this.post = data;
          this.loading = false;
          if (!data) {
            this.error = response.message || 'Post not found.';
          }
        },
        error: (err) => {
          this.error = err?.error?.message || err?.error?.Message || 'Failed to load post.';
          this.loading = false;
        },
      });
  }
}
