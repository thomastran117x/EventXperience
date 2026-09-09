import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { finalize } from 'rxjs/operators';

import { getApiClientMessage } from '../../../../../core/api/models/api-client-error.model';
import { ClubPost, POST_TYPE_STYLES, PostType } from '../../../models/club-post.types';
import { ClubPostsService } from '../../../services/club-posts.service';

const TITLE_MAX = 150;
const CONTENT_MAX = 2000;

@Component({
  selector: 'app-posts-tab',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './posts-tab.component.html',
})
export class PostsTabComponent implements OnInit {
  private readonly destroyRef = inject(DestroyRef);

  clubId = 0;
  posts: ClubPost[] = [];
  loading = true;
  error = '';
  success = '';

  page = 1;
  readonly pageSize = 10;
  totalCount = 0;

  postSearch = '';
  private readonly searchInput$ = new Subject<string>();

  readonly postTypeStyles = POST_TYPE_STYLES;
  readonly postTypes: PostType[] = ['General', 'Announcement', 'Event', 'Poll'];
  readonly titleMax = TITLE_MAX;
  readonly contentMax = CONTENT_MAX;
  readonly skeletons = Array.from({ length: 3 });

  // Editor state (editingId null => creating)
  showEditor = false;
  editingId: number | null = null;
  saving = false;
  form = { title: '', content: '', postType: 'General' as PostType, isPinned: false };

  deletingId: number | null = null;
  pinningId: number | null = null;

  constructor(
    private route: ActivatedRoute,
    private postsService: ClubPostsService,
  ) {}

  ngOnInit(): void {
    this.clubId =
      Number.parseInt(this.route.parent?.snapshot.paramMap.get('clubId') ?? '', 10) || 0;
    if (!this.clubId) {
      this.loading = false;
      this.error = 'A valid club ID is required.';
      return;
    }
    this.load();

    this.searchInput$
      .pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.page = 1;
        this.load();
      });
  }

  onPostSearch(value: string): void {
    this.postSearch = value;
    this.searchInput$.next(value);
  }

  get totalPages(): number {
    return Math.max(1, Math.ceil(this.totalCount / this.pageSize));
  }

  openCreate(): void {
    this.editingId = null;
    this.form = { title: '', content: '', postType: 'General', isPinned: false };
    this.showEditor = true;
    this.error = '';
    this.success = '';
  }

  openEdit(post: ClubPost): void {
    this.editingId = post.id;
    this.form = {
      title: post.title,
      content: post.content,
      postType: post.postType,
      isPinned: post.isPinned,
    };
    this.showEditor = true;
    this.error = '';
    this.success = '';
  }

  cancelEditor(): void {
    this.showEditor = false;
    this.editingId = null;
  }

  savePost(): void {
    const title = this.form.title.trim();
    const content = this.form.content.trim();
    if (!title || !content) {
      this.error = 'Title and content are required.';
      return;
    }

    this.saving = true;
    this.error = '';
    this.success = '';

    const payload = {
      title,
      content,
      postType: this.form.postType,
      isPinned: this.form.isPinned,
    };

    const request$ =
      this.editingId != null
        ? this.postsService.updatePost(this.clubId, this.editingId, payload)
        : this.postsService.createPost(this.clubId, payload);

    request$
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.saving = false)),
      )
      .subscribe({
        next: () => {
          this.success = this.editingId != null ? 'Post updated.' : 'Post published.';
          this.showEditor = false;
          this.editingId = null;
          this.load();
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to save the post.');
        },
      });
  }

  togglePin(post: ClubPost): void {
    this.pinningId = post.id;
    this.error = '';
    this.success = '';

    this.postsService
      .updatePost(this.clubId, post.id, {
        title: post.title,
        content: post.content,
        postType: post.postType,
        isPinned: !post.isPinned,
      })
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.pinningId = null)),
      )
      .subscribe({
        next: (response) => {
          const updated = response.data;
          if (updated) {
            this.posts = this.posts.map((p) => (p.id === post.id ? updated : p));
          }
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to update the post.');
        },
      });
  }

  deletePost(post: ClubPost): void {
    this.deletingId = post.id;
    this.error = '';
    this.success = '';

    this.postsService
      .deletePost(this.clubId, post.id)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.deletingId = null)),
      )
      .subscribe({
        next: () => {
          this.success = 'Post deleted.';
          this.posts = this.posts.filter((p) => p.id !== post.id);
          this.totalCount = Math.max(0, this.totalCount - 1);
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to delete the post.');
        },
      });
  }

  goToPage(page: number): void {
    if (page < 1 || page > this.totalPages || page === this.page) return;
    this.page = page;
    this.load();
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
    this.postsService
      .getPosts(this.clubId, {
        search: this.postSearch || undefined,
        sortBy: 'Recent',
        page: this.page,
        pageSize: this.pageSize,
      })
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.loading = false)),
      )
      .subscribe({
        next: (response) => {
          this.posts = response.data?.items ?? [];
          this.totalCount = response.data?.totalCount ?? 0;
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to load posts.');
        },
      });
  }
}
