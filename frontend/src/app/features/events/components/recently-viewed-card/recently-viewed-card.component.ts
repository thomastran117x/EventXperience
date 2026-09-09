import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { RouterLink } from '@angular/router';

import { CATEGORY_STYLES } from '../../models/event.types';
import { RecentlyViewedEntry } from '../../models/recently-viewed.types';
import { EventFavouriteToggleComponent } from '../event-favourite-toggle/event-favourite-toggle.component';

/**
 * One event in the history.
 *
 * The same card serves the read-only rails and the manageable page, so selection is opt-in via
 * {@link selectable} rather than being two near-identical components that drift apart.
 */
@Component({
  selector: 'app-recently-viewed-card',
  standalone: true,
  imports: [CommonModule, RouterLink, EventFavouriteToggleComponent],
  templateUrl: './recently-viewed-card.component.html',
})
export class RecentlyViewedCardComponent {
  @Input({ required: true }) entry!: RecentlyViewedEntry;

  /** Turns on the checkbox and suppresses navigation, for the page's multi-select mode. */
  @Input() selectable = false;
  @Input() selected = false;

  /** Hidden on the rails, where there is nothing to manage. */
  @Input() showRemove = false;

  @Input() favouritesEnabled = false;

  @Output() selectedChange = new EventEmitter<boolean>();
  @Output() removed = new EventEmitter<number>();
  @Output() favouriteFailed = new EventEmitter<unknown>();

  readonly categoryStyles = CATEGORY_STYLES;

  toggleSelected(): void {
    this.selectedChange.emit(!this.selected);
  }

  remove(): void {
    this.removed.emit(this.entry.eventId);
  }

  formatViewedAt(value: string): string {
    const viewedAt = Date.parse(value);
    if (!Number.isFinite(viewedAt)) {
      return '';
    }

    const minutes = Math.floor((Date.now() - viewedAt) / 60000);

    if (minutes < 1) return 'Just now';
    if (minutes < 60) return `${minutes}m ago`;

    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;

    const days = Math.floor(hours / 24);
    if (days < 7) return `${days}d ago`;

    return new Date(viewedAt).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  }

  formatDate(value: string): string {
    const parsed = Date.parse(value);
    return Number.isFinite(parsed)
      ? new Date(parsed).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
      : 'Date TBD';
  }

  formatCost(cost: number): string {
    return cost > 0 ? `$${cost}` : 'Free';
  }
}
