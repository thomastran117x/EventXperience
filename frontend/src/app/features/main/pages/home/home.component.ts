import { Component } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterModule } from '@angular/router';

import { AppButtonComponent } from '@common/button/button.component';
import { PillComponent } from '@common/pill/pill.component';
import { RecentlyViewedRailComponent } from '../../../events/components/recently-viewed-rail/recently-viewed-rail.component';
import { FeatureFlagsService } from '../../../../core/features/feature-flags.service';
import { FEATURE_KEYS } from '../../../../core/features/feature-flags.types';

type Category = { name: string; icon: string; count: string };
type Feature = { title: string; desc: string; icon: string };
type EventCard = {
  title: string;
  venue: string;
  city: string;
  date: string;
  price: string;
  badge?: string;
};
type Testimonial = { quote: string; name: string; role: string };

@Component({
  selector: 'app-home-page',
  standalone: true,
  imports: [
    FormsModule,
    RouterModule,
    AppButtonComponent,
    PillComponent,
    RecentlyViewedRailComponent,
  ],
  template: `
    <div class="app-page">
      <main class="pb-16 sm:pb-20">
        <section class="page-section pt-10 pb-12 sm:pt-14 sm:pb-14 lg:pt-16 lg:pb-16">
          <div
            class="grid gap-10 lg:grid-cols-[minmax(0,1.16fr)_24rem] lg:items-start xl:grid-cols-[minmax(0,1.18fr)_27rem] xl:gap-12"
          >
            <div class="max-w-3xl lg:pr-4">
              <pill tone="accent">Professional event discovery for teams and attendees</pill>
              <h1
                class="mt-6 max-w-[44rem] text-4xl font-extrabold tracking-tight text-content sm:text-5xl lg:text-[3.4rem] lg:leading-[1.02]"
              >
                A cleaner way to discover, organize, and launch memorable events.
              </h1>
              <p class="mt-5 max-w-2xl text-base leading-8 text-muted sm:text-lg">
                EventXperience helps organizers manage listings and communities while giving
                attendees a faster, more trustworthy browsing and registration experience.
              </p>

              <div class="mt-8 flex flex-wrap gap-3">
                @if (eventsEnabled) {
                  <app-button variant="primary" [href]="['/events']">Browse events</app-button>
                }
                @if (authEnabled) {
                  <app-button variant="secondary" [href]="['/auth/signup']"
                    >Create account</app-button
                  >
                }
              </div>

              <div
                class="mt-8 hidden flex-wrap gap-2 text-xs font-semibold uppercase tracking-[0.12em] text-subtle sm:flex"
              >
                <span class="rounded-full border border-line bg-surface px-3 py-1.5"
                  >Verified listings</span
                >
                <span class="rounded-full border border-line bg-surface px-3 py-1.5"
                  >Real-time availability</span
                >
                <span class="rounded-full border border-line bg-surface px-3 py-1.5"
                  >Fast checkout</span
                >
              </div>

              <div class="mt-8 grid grid-cols-3 gap-3 xl:max-w-2xl">
                <div class="surface-card rounded-2xl p-4 xl:p-5">
                  <div class="text-2xl font-bold text-content">4.9/5</div>
                  <div class="mt-1 text-[13px] leading-5 text-subtle">
                    average organizer satisfaction
                  </div>
                </div>
                <div class="surface-card rounded-2xl p-4 xl:p-5">
                  <div class="text-2xl font-bold text-content">2M+</div>
                  <div class="mt-1 text-[13px] leading-5 text-subtle">
                    tickets and registrations processed
                  </div>
                </div>
                <div class="surface-card rounded-2xl p-4 xl:p-5">
                  <div class="text-2xl font-bold text-content">60s</div>
                  <div class="mt-1 text-[13px] leading-5 text-subtle">
                    average checkout completion
                  </div>
                </div>
              </div>
            </div>

            <div class="surface-panel self-start rounded-[1.75rem] p-6 sm:p-7 lg:sticky lg:top-24">
              <div class="flex items-center justify-between gap-4">
                <div>
                  <p class="text-sm font-semibold text-content">Find the right event faster</p>
                  <p class="mt-1 text-sm leading-6 text-subtle">
                    Search live inventory, city, and category in one place.
                  </p>
                </div>
                <span
                  class="rounded-full border border-line bg-surface-muted px-3 py-1 text-xs font-semibold uppercase tracking-[0.12em] text-subtle"
                >
                  Live
                </span>
              </div>

              @if (eventsEnabled) {
                <div class="mt-6 grid gap-3">
                  <div class="space-y-2">
                    <label class="text-xs font-semibold uppercase tracking-[0.12em] text-subtle"
                      >Search</label
                    >
                    <input
                      class="form-field w-full px-4 py-3 text-sm"
                      placeholder="Artist, campus event, venue, organizer"
                      [(ngModel)]="heroSearch"
                      (keyup.enter)="explore()"
                    />
                  </div>
                  <div class="space-y-2">
                    <label class="text-xs font-semibold uppercase tracking-[0.12em] text-subtle"
                      >City</label
                    >
                    <input
                      class="form-field w-full px-4 py-3 text-sm"
                      placeholder="Ottawa, Toronto, Montreal"
                      [(ngModel)]="heroCity"
                      (keyup.enter)="explore()"
                    />
                  </div>
                  <app-button
                    variant="primary"
                    className="w-full justify-center"
                    (clicked)="explore()"
                  >
                    Explore events
                  </app-button>
                </div>
              }

              <div class="mt-5 rounded-2xl border border-line bg-surface-muted p-5">
                <div class="flex items-center justify-between gap-3">
                  <div class="text-xs font-semibold uppercase tracking-[0.12em] text-subtle">
                    Trending now
                  </div>
                  <div class="text-[11px] font-medium text-subtle">Updated live</div>
                </div>
                <div class="mt-4 space-y-3">
                  @for (ev of trending; track trackByTitle($index, ev)) {
                    <div class="rounded-2xl border border-line bg-surface px-4 py-4">
                      <div class="flex items-start justify-between gap-4">
                        <div>
                          <div class="flex items-center gap-2">
                            <p class="text-sm font-semibold text-content">{{ ev.title }}</p>
                            @if (ev.badge) {
                              <span
                                class="rounded-full border border-line bg-surface-muted px-2 py-0.5 text-[11px] text-subtle"
                              >
                                {{ ev.badge }}
                              </span>
                            }
                          </div>
                          <p class="mt-1 text-xs leading-5 text-subtle">
                            {{ ev.date }} / {{ ev.venue }} / {{ ev.city }}
                          </p>
                        </div>
                        <div class="text-right">
                          <div class="text-sm font-semibold text-content">{{ ev.price }}</div>
                          <div class="text-[11px] text-subtle">from</div>
                        </div>
                      </div>
                    </div>
                  }
                </div>
              </div>
            </div>
          </div>
        </section>

        <div class="page-section flex flex-col gap-10 sm:gap-12 lg:gap-14">
          @if (eventsEnabled) {
            <section>
              <div class="surface-card rounded-[1.75rem] p-6 sm:p-8">
                <div class="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
                  <div>
                    <p class="text-sm font-semibold uppercase tracking-[0.12em] text-subtle">
                      Categories
                    </p>
                    <h2 class="mt-2 text-2xl font-bold text-content">Browse by event type</h2>
                  </div>
                  <a
                    routerLink="/events"
                    class="text-sm font-semibold text-accent transition hover:text-content"
                  >
                    See all events
                  </a>
                </div>
                <div class="mt-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
                  @for (c of categories; track trackByName($index, c)) {
                    <a
                      routerLink="/events"
                      class="surface-muted rounded-2xl p-5 transition hover:border-line-strong hover:bg-surface"
                    >
                      <div class="flex items-center justify-between">
                        <span
                          class="text-xs font-semibold uppercase tracking-[0.12em] text-subtle"
                          >{{ c.icon }}</span
                        >
                        <span
                          class="rounded-full border border-line bg-surface px-2 py-1 text-[11px] text-subtle"
                          >{{ c.count }}</span
                        >
                      </div>
                      <div class="mt-4 text-base font-semibold text-content">{{ c.name }}</div>
                    </a>
                  }
                </div>
              </div>
            </section>
          }

          <!-- Renders nothing until there is a history to show, so a first-time visitor sees the
               page exactly as before. -->
          <app-recently-viewed-rail [limit]="4" />

          <section>
            <div class="grid gap-6 lg:grid-cols-[0.82fr_1.18fr] lg:items-start">
              <div class="max-w-lg">
                <p class="text-sm font-semibold uppercase tracking-[0.12em] text-subtle">
                  Why teams choose it
                </p>
                <h2 class="mt-2 text-2xl font-bold text-content">
                  Built for trust, speed, and operational clarity
                </h2>
                <p class="mt-4 text-sm leading-7 text-muted">
                  The redesigned experience favors legibility and control: better defaults, cleaner
                  forms, and fewer distractions around the actions that matter.
                </p>
              </div>
              <div class="grid gap-4 sm:grid-cols-2">
                @for (f of features; track trackByTitle($index, f)) {
                  <article class="surface-card rounded-2xl p-6">
                    <div class="flex items-center gap-3">
                      <div
                        class="flex h-11 w-11 items-center justify-center rounded-2xl border border-accent/20 bg-accent/10 text-sm font-bold text-accent"
                      >
                        {{ f.icon }}
                      </div>
                      <h3 class="text-base font-semibold text-content">{{ f.title }}</h3>
                    </div>
                    <p class="mt-4 text-sm leading-7 text-muted">{{ f.desc }}</p>
                  </article>
                }
              </div>
            </div>
          </section>

          @if (eventsEnabled) {
            <section>
              <div class="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
                <div>
                  <p class="text-sm font-semibold uppercase tracking-[0.12em] text-subtle">
                    Popular this week
                  </p>
                  <h2 class="mt-2 text-2xl font-bold text-content">
                    Highlights across your region
                  </h2>
                </div>
                <a
                  routerLink="/events"
                  class="text-sm font-semibold text-accent transition hover:text-content"
                >
                  Explore event listings
                </a>
              </div>
              <div class="mt-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
                @for (e of popular; track trackByTitle($index, e)) {
                  <a
                    routerLink="/events"
                    class="surface-card rounded-3xl p-6 transition hover:border-line-strong hover:bg-surface-muted"
                  >
                    <div class="flex items-center justify-between gap-3">
                      <span
                        class="rounded-full border border-line bg-surface-muted px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.12em] text-subtle"
                      >
                        {{ e.badge || 'Featured' }}
                      </span>
                      <span class="text-sm font-semibold text-content">{{ e.price }}</span>
                    </div>
                    <h3 class="mt-5 text-lg font-semibold text-content">{{ e.title }}</h3>
                    <p class="mt-2 text-sm text-subtle">{{ e.date }}</p>
                    <p class="mt-1 text-sm text-muted">{{ e.venue }} / {{ e.city }}</p>
                    <div class="mt-6 text-sm font-semibold text-accent">View details -></div>
                  </a>
                }
              </div>
            </section>
          }

          <section>
            <div class="section-muted rounded-[1.75rem] p-6 sm:p-8">
              <div class="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
                <div>
                  <p class="text-sm font-semibold uppercase tracking-[0.12em] text-subtle">
                    Customer signal
                  </p>
                  <h2 class="mt-2 text-2xl font-bold text-content">
                    Loved by organizers and attendees
                  </h2>
                </div>
                @if (authEnabled) {
                  <app-button variant="secondary" [href]="['/auth/signup']"
                    >Start with your team</app-button
                  >
                }
              </div>
              <div class="mt-6 grid gap-4 md:grid-cols-3">
                @for (t of testimonials; track trackByName($index, t)) {
                  <article class="surface-card rounded-2xl p-6">
                    <p class="text-sm leading-7 text-muted">"{{ t.quote }}"</p>
                    <div class="mt-5 text-sm font-semibold text-content">{{ t.name }}</div>
                    <div class="text-xs text-subtle">{{ t.role }}</div>
                  </article>
                }
              </div>
            </div>
          </section>

          <section>
            <div class="surface-panel rounded-[1.75rem] p-8 sm:p-10">
              <div class="grid gap-6 lg:grid-cols-[1.1fr_0.9fr] lg:items-center">
                <div class="max-w-2xl">
                  <p class="text-sm font-semibold uppercase tracking-[0.12em] text-subtle">
                    Ready to launch
                  </p>
                  <h2 class="mt-2 text-3xl font-bold tracking-tight text-content">
                    Bring a more polished event experience to your audience.
                  </h2>
                  <p class="mt-4 text-sm leading-7 text-muted">
                    Create listings, manage attendance, and publish updates from one platform
                    designed to feel professional in both light and dark mode.
                  </p>
                </div>
                <div class="flex flex-col gap-3 sm:flex-row lg:justify-end">
                  @if (authEnabled) {
                    <app-button variant="primary" [href]="['/auth/signup']"
                      >Create an account</app-button
                    >
                  }
                  @if (eventsEnabled) {
                    <app-button variant="ghost" [href]="['/events']">Browse events</app-button>
                  }
                </div>
              </div>
            </div>
          </section>
        </div>
      </main>
    </div>
  `,
})
export class HomeComponent {
  year = new Date().getFullYear();

  heroSearch = '';
  heroCity = '';
  readonly authEnabled: boolean;
  readonly eventsEnabled: boolean;

  constructor(
    private router: Router,
    private featureFlags: FeatureFlagsService,
  ) {
    this.authEnabled = this.featureFlags.isEnabled(FEATURE_KEYS.auth);
    this.eventsEnabled = this.featureFlags.isEnabled(FEATURE_KEYS.events);
  }

  explore(): void {
    if (!this.eventsEnabled) {
      return;
    }

    const queryParams: Record<string, string> = {};

    if (this.heroSearch.trim()) {
      queryParams['search'] = this.heroSearch.trim();
    }

    if (this.heroCity.trim()) {
      queryParams['city'] = this.heroCity.trim();
    }

    this.router.navigate(['/events'], { queryParams });
  }

  categories: Category[] = [
    { name: 'Concerts', icon: 'MU', count: '1,240 events' },
    { name: 'Sports', icon: 'SP', count: '540 events' },
    { name: 'Theatre', icon: 'TH', count: '320 events' },
    { name: 'Campus', icon: 'CA', count: '180 events' },
    { name: 'Festivals', icon: 'FE', count: '95 events' },
  ];

  features: Feature[] = [
    {
      icon: 'OK',
      title: 'Verified tickets',
      desc: 'Reduce fraud with identity and inventory checks and protected transfers.',
    },
    {
      icon: 'GO',
      title: 'Fast checkout',
      desc: 'Designed for conversion with saved payment methods and smart defaults.',
    },
    {
      icon: 'RT',
      title: 'Real-time seats',
      desc: 'Live availability and pricing without stale seat maps or surprise changes.',
    },
    {
      icon: 'MO',
      title: 'Mobile delivery',
      desc: 'Wallet-ready tickets with instant delivery and reliable last-minute access.',
    },
  ];

  trending: EventCard[] = [
    {
      title: 'Neon Nights Tour',
      date: 'Fri - 7:30 PM',
      venue: 'Riverside Arena',
      city: 'Ottawa',
      price: '$49',
      badge: 'Hot',
    },
    {
      title: 'Capital City Hockey',
      date: 'Sat - 6:00 PM',
      venue: 'North Dome',
      city: 'Ottawa',
      price: '$35',
      badge: 'Few left',
    },
    {
      title: 'Indie Theatre Showcase',
      date: 'Sun - 8:00 PM',
      venue: 'Grand Hall',
      city: 'Toronto',
      price: '$28',
    },
  ];

  popular: EventCard[] = [
    {
      title: 'Skyline Music Festival',
      date: 'Jul 18-20',
      venue: 'Harbour Grounds',
      city: 'Toronto',
      price: '$89',
      badge: 'Weekend pass',
    },
    {
      title: 'UOttawa Winter Formal',
      date: 'Feb 22',
      venue: 'Student Union',
      city: 'Ottawa',
      price: '$15',
      badge: 'Campus',
    },
    {
      title: 'Championship Night',
      date: 'Mar 03 - 7:00 PM',
      venue: 'City Stadium',
      city: 'Montreal',
      price: '$55',
      badge: 'Trending',
    },
  ];

  testimonials: Testimonial[] = [
    {
      quote:
        'The checkout is smooth and dependable. I had my tickets in my wallet in under a minute.',
      name: 'Ayesha K.',
      role: 'Fan - Concerts',
    },
    {
      quote:
        'Our team sold out faster and support tickets dropped noticeably once we moved organizers onto the platform.',
      name: 'Marco D.',
      role: 'Organizer - Campus events',
    },
    {
      quote:
        'Transparent pricing and a cleaner flow made the whole experience feel much more trustworthy.',
      name: 'Jules P.',
      role: 'Fan - Sports',
    },
  ];

  trackByTitle = (_: number, item: { title: string }) => item.title;
  trackByName = (_: number, item: { name: string }) => item.name;
}
