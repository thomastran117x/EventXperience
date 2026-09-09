import { CommonModule, isPlatformBrowser } from '@angular/common';
import { Component, Inject, Input, OnInit, PLATFORM_ID } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Observable, map } from 'rxjs';

import { FeatureFlagsService } from '../../../../core/features/feature-flags.service';
import { FEATURE_KEYS } from '../../../../core/features/feature-flags.types';
import { RecentlyViewedEntry } from '../../models/recently-viewed.types';
import { RecentlyViewedStore } from '../../services/recently-viewed-store.service';
import { RecentlyViewedCardComponent } from '../recently-viewed-card/recently-viewed-card.component';

/**
 * A compact strip of the most recent events, for the home page and the search page.
 *
 * Reads the shared store rather than fetching for itself, so however many rails a page shows they
 * cost one request between them.
 *
 * Renders nothing during server-side rendering: the history is per-user and, when signed out,
 * lives in localStorage, so there is nothing meaningful to paint on the server and a fetch there
 * would only slow the first byte.
 */
@Component({
  selector: 'app-recently-viewed-rail',
  standalone: true,
  imports: [CommonModule, RouterLink, RecentlyViewedCardComponent],
  templateUrl: './recently-viewed-rail.component.html',
})
export class RecentlyViewedRailComponent implements OnInit {
  @Input() limit = 8;
  @Input() heading = 'Recently viewed';

  readonly featureEnabled: boolean;
  readonly favouritesEnabled: boolean;
  readonly isBrowser: boolean;

  entries$!: Observable<RecentlyViewedEntry[]>;

  constructor(
    private recentlyViewedStore: RecentlyViewedStore,
    features: FeatureFlagsService,
    @Inject(PLATFORM_ID) platformId: object,
  ) {
    this.featureEnabled =
      features.isEnabled(FEATURE_KEYS.eventsRecentlyViewed) &&
      features.isEnabled(FEATURE_KEYS.events);
    this.favouritesEnabled = features.isEnabled(FEATURE_KEYS.eventsFavourites);
    this.isBrowser = isPlatformBrowser(platformId);
  }

  ngOnInit(): void {
    this.entries$ = this.recentlyViewedStore.items$.pipe(
      map((entries) => entries.slice(0, this.limit)),
    );

    if (this.featureEnabled && this.isBrowser) {
      this.recentlyViewedStore.ensureLoaded();
    }
  }
}
