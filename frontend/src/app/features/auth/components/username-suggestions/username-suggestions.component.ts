import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';

import { UsernameSuggestion } from '../../services/auth.service';

/**
 * The row of tappable username suggestions shown under a username field.
 *
 * Presentational only — it holds no state and does no fetching, so the three forms that use it keep
 * owning their own availability bookkeeping. Renders nothing at all when the list is empty, which is
 * what lets every host degrade to the pre-suggestion UI for free when a draw comes back short.
 */
@Component({
  selector: 'app-username-suggestions',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (suggestions.length || loading) {
      <div class="mt-2 flex flex-wrap items-center gap-2" [class]="className">
        <span class="text-xs text-subtle">{{ loading ? 'Finding names…' : 'Try:' }}</span>

        @for (suggestion of suggestions; track suggestion.username) {
          <button
            type="button"
            class="rounded-full border px-3 py-1 text-xs font-semibold transition"
            [class]="
              suggestion.username === selected
                ? 'border-accent bg-accent/10 text-accent'
                : 'border-line text-muted hover:border-line-strong hover:text-content'
            "
            [attr.aria-pressed]="suggestion.username === selected"
            (click)="pick.emit(suggestion)"
          >
            {{ suggestion.display }}
          </button>
        }

        @if (suggestions.length && !loading) {
          <button
            type="button"
            class="rounded-full px-2 py-1 text-xs font-semibold text-accent transition hover:underline"
            (click)="shuffle.emit()"
          >
            Shuffle
          </button>
        }
      </div>
    }
  `,
})
export class UsernameSuggestionsComponent {
  @Input() suggestions: UsernameSuggestion[] = [];
  @Input() loading = false;

  /** The normalised username currently in the field, so the matching chip can read as chosen. */
  @Input() selected: string | null = null;

  /**
   * Extra classes for the wrapper. The OAuth role page is the one screen still on hand-written
   * dark CSS rather than the Tailwind design tokens, so it needs a hook to blend in without
   * dragging a styling migration into this change.
   */
  @Input() className = '';

  @Output() pick = new EventEmitter<UsernameSuggestion>();
  @Output() shuffle = new EventEmitter<void>();
}
