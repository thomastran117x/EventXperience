import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { getApiClientMessage } from '../../../../../../core/api/models/api-client-error.model';
import { ToggleSwitchComponent } from '../../../../../../shared/common/toggle-switch/toggle-switch.component';
import {
  RecentlyViewedMaxItems,
  RecentlyViewedRetentionDays,
} from '../../../../../events/models/recently-viewed.types';
import { RecentlyViewedStore } from '../../../../../events/services/recently-viewed-store.service';

/**
 * Privacy preferences. Currently just view tracking, but named for the concern rather than the one
 * setting so it has somewhere to grow.
 *
 * No MFA gate: this is a low-stakes preference, not a sensitive account operation.
 */
@Component({
  selector: 'app-privacy-tab',
  standalone: true,
  imports: [CommonModule, RouterLink, ToggleSwitchComponent],
  templateUrl: './privacy-tab.component.html',
})
export class PrivacyTabComponent implements OnInit {
  trackingEnabled = true;
  loading = true;
  saving = false;
  clearing = false;
  error = '';
  success = '';

  readonly retentionDays = RecentlyViewedRetentionDays;
  readonly maxItems = RecentlyViewedMaxItems;

  private readonly destroyRef = inject(DestroyRef);
  private readonly recentlyViewedStore = inject(RecentlyViewedStore);

  ngOnInit(): void {
    this.recentlyViewedStore
      .loadSettings()
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.loading = false)),
      )
      .subscribe({
        next: (enabled) => (this.trackingEnabled = enabled),
        error: (err: unknown) => {
          this.error = getApiClientMessage(err, 'Unable to load your privacy settings.');
        },
      });
  }

  setTracking(enabled: boolean): void {
    this.error = '';
    this.success = '';
    this.saving = true;

    const previous = this.trackingEnabled;
    this.trackingEnabled = enabled;

    this.recentlyViewedStore
      .setEnabled(enabled)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.saving = false)),
      )
      .subscribe({
        next: () => {
          this.success = enabled
            ? 'View tracking is on.'
            : 'View tracking is off. Your existing history has been kept.';
        },
        error: (err: unknown) => {
          this.trackingEnabled = previous;
          this.error = getApiClientMessage(err, 'Unable to update your view tracking setting.');
        },
      });
  }

  clearHistory(): void {
    const confirmed =
      typeof window === 'undefined' ||
      window.confirm('Clear your entire recently viewed history? This cannot be undone.');

    if (!confirmed) {
      return;
    }

    this.error = '';
    this.success = '';
    this.clearing = true;

    this.recentlyViewedStore
      .clear()
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.clearing = false)),
      )
      .subscribe({
        next: () => (this.success = 'Your recently viewed history has been cleared.'),
        error: (err: unknown) => {
          this.error = getApiClientMessage(err, 'Unable to clear your recently viewed history.');
        },
      });
  }
}
