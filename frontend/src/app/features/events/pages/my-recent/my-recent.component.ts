import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subject, takeUntil } from 'rxjs';

import { getApiClientMessage } from '../../../../core/api/models/api-client-error.model';
import { FeatureFlagsService } from '../../../../core/features/feature-flags.service';
import { FEATURE_KEYS } from '../../../../core/features/feature-flags.types';
import { ToggleSwitchComponent } from '../../../../shared/common/toggle-switch/toggle-switch.component';
import {
  RecentlyViewedEntry,
  RecentlyViewedRetentionDays,
  RecentlyViewedMaxItems,
} from '../../models/recently-viewed.types';
import { RecentlyViewedStore } from '../../services/recently-viewed-store.service';
import { RecentlyViewedCardComponent } from '../../components/recently-viewed-card/recently-viewed-card.component';

/**
 * The management surface for the history: the full list, plus all three ways to delete from it.
 *
 * Deliberately reachable signed out, because the browser-held history is a first-class part of the
 * feature and a visitor needs somewhere to see and clear it - they cannot reach /account.
 */
@Component({
  selector: 'app-my-recent',
  standalone: true,
  imports: [CommonModule, RouterLink, RecentlyViewedCardComponent, ToggleSwitchComponent],
  templateUrl: './my-recent.component.html',
})
export class MyRecentComponent implements OnInit, OnDestroy {
  entries: RecentlyViewedEntry[] = [];
  loading = true;
  error = '';

  selectMode = false;
  readonly selectedIds = new Set<number>();

  trackingEnabled = true;
  isSignedIn = false;

  readonly favouritesEnabled: boolean;
  readonly retentionDays = RecentlyViewedRetentionDays;
  readonly maxItems = RecentlyViewedMaxItems;

  private readonly destroy$ = new Subject<void>();

  /**
   * Set while this component is the one changing the list, so its own optimistic removal does not
   * look like an external refresh and wipe the selection mid-interaction.
   */
  private removingLocally = false;

  constructor(
    private recentlyViewedStore: RecentlyViewedStore,
    features: FeatureFlagsService,
  ) {
    this.favouritesEnabled = features.isEnabled(FEATURE_KEYS.eventsFavourites);
  }

  ngOnInit(): void {
    this.isSignedIn = this.recentlyViewedStore.isSignedIn;
    this.trackingEnabled = !this.recentlyViewedStore.optedOut;

    this.recentlyViewedStore.items$.pipe(takeUntil(this.destroy$)).subscribe((entries) => {
      this.entries = entries;
      this.loading = false;

      if (!this.removingLocally) {
        // A refresh from anywhere else can retire ids that are still ticked; dropping the
        // selection is safer than acting on entries that are no longer on screen.
        this.clearSelection();
      }
    });

    // Signing out does not navigate, so the page has to notice for itself.
    this.recentlyViewedStore.session$.pipe(takeUntil(this.destroy$)).subscribe(() => {
      this.isSignedIn = this.recentlyViewedStore.isSignedIn;
      this.trackingEnabled = !this.recentlyViewedStore.optedOut;
      this.clearSelection();
    });

    this.recentlyViewedStore.ensureLoaded();
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  get selectedCount(): number {
    return this.selectedIds.size;
  }

  get allSelected(): boolean {
    return this.entries.length > 0 && this.selectedIds.size === this.entries.length;
  }

  toggleSelectMode(): void {
    this.selectMode = !this.selectMode;
    this.clearSelection();
  }

  isSelected(eventId: number): boolean {
    return this.selectedIds.has(eventId);
  }

  setSelected(eventId: number, selected: boolean): void {
    if (selected) {
      this.selectedIds.add(eventId);
    } else {
      this.selectedIds.delete(eventId);
    }
  }

  toggleSelectAll(): void {
    if (this.allSelected) {
      this.clearSelection();
      return;
    }

    this.entries.forEach((entry) => this.selectedIds.add(entry.eventId));
  }

  removeOne(eventId: number): void {
    this.error = '';
    this.runRemoval(this.recentlyViewedStore.remove(eventId));
  }

  removeSelected(): void {
    if (this.selectedIds.size === 0) {
      return;
    }

    this.error = '';
    const ids = [...this.selectedIds];
    this.runRemoval(this.recentlyViewedStore.removeMany(ids), () => this.clearSelection());
  }

  clearAll(): void {
    if (this.entries.length === 0) {
      return;
    }

    // Not recoverable, so it asks first - unlike the per-card remove, which is one entry.
    const confirmed =
      typeof window === 'undefined' ||
      window.confirm('Clear your entire recently viewed history? This cannot be undone.');

    if (!confirmed) {
      return;
    }

    this.error = '';
    this.runRemoval(this.recentlyViewedStore.clear(), () => this.clearSelection());
  }

  setTracking(enabled: boolean): void {
    this.error = '';
    this.trackingEnabled = enabled;

    this.recentlyViewedStore
      .setEnabled(enabled)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        error: (err: unknown) => {
          this.trackingEnabled = !enabled;
          this.error = getApiClientMessage(err, 'Unable to update your view tracking setting.');
        },
      });
  }

  onFavouriteFailed(response: unknown): void {
    this.error = getApiClientMessage(response, 'We could not update your saved events.');
  }

  private runRemoval(request: ReturnType<RecentlyViewedStore['clear']>, onDone?: () => void): void {
    this.removingLocally = true;

    request.pipe(takeUntil(this.destroy$)).subscribe({
      next: () => {
        this.removingLocally = false;
        onDone?.();
      },
      error: (err: unknown) => {
        this.removingLocally = false;
        this.error = getApiClientMessage(err, 'Unable to update your recently viewed events.');
      },
    });
  }

  private clearSelection(): void {
    this.selectedIds.clear();
  }
}
