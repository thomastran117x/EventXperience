import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { FeatureFlagsService } from '../../../../core/features/feature-flags.service';
import { FEATURE_KEYS } from '../../../../core/features/feature-flags.types';

@Component({
  selector: 'app-account-page',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './account-page.component.html',
})
export class AccountPageComponent {
  /**
   * The privacy tab only has anything to show while the recently-viewed feature is on, so the tab
   * is hidden rather than leading to an empty page when the flag is off.
   */
  readonly privacyTabEnabled: boolean;

  constructor(features: FeatureFlagsService) {
    this.privacyTabEnabled = features.isEnabled(FEATURE_KEYS.eventsRecentlyViewed);
  }
}
