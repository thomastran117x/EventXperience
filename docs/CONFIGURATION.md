# Configuration

## Feature flags

EventXperience uses a code-owned feature flag registry to control whether backend endpoints, backend services, hosted services, and frontend routes are exposed.

### Canonical keys

The supported keys are:

- `auth`
- `bloom`
- `clubs`
- `clubs.follow`
- `clubs.posts`
- `clubs.reviews`
- `clubs.versioning`
- `events`
- `events.analytics`
- `events.images`
- `events.invitations`
- `events.registration`
- `events.versioning`
- `payment`
- `profile`
- `profile.admin`
- `search`
- `search.reindex`

Unknown keys are intentionally rejected by backend parsing and tests so configuration drift fails fast.

### Inheritance and defaults

Flags use parent-child inheritance.

- Missing flags default to `true`.
- Setting a parent flag to `false` disables every descendant.
- Setting a child flag to `false` disables only that subfeature.

Examples:

- `events=false` disables all event routes, services, and frontend event routes.
- `events.invitations=false` keeps the rest of events enabled while removing invitation endpoints and UI.
- `search=true` with `search.reindex=false` keeps search online while hiding manual reindex operations.

### Environment variables

Deployments should use the flat environment variables defined in `.env.example`:

- `FEATURE_AUTH`
- `FEATURE_BLOOM`
- `FEATURE_CLUBS`
- `FEATURE_CLUBS_DISCUSSIONS`
- `FEATURE_CLUBS_FOLLOW`
- `FEATURE_CLUBS_POSTS`
- `FEATURE_CLUBS_REVIEWS`
- `FEATURE_CLUBS_VERSIONING`
- `FEATURE_EVENTS`
- `FEATURE_EVENTS_ANALYTICS`
- `FEATURE_EVENTS_FAVOURITES`
- `FEATURE_EVENTS_IMAGES`
- `FEATURE_EVENTS_INVITATIONS`
- `FEATURE_EVENTS_REGISTRATION`
- `FEATURE_EVENTS_VERSIONING`
- `FEATURE_EVENTS_WAITLIST`
- `FEATURE_PAYMENT`
- `FEATURE_PROFILE`
- `FEATURE_PROFILE_ADMIN`
- `FEATURE_SEARCH`
- `FEATURE_SEARCH_REINDEX`

The backend also supports a `FeatureFlags` configuration section, but the flat env vars are the canonical deploy-time format because they are shared with the frontend environment generation step.

### Backend behavior

Backend feature flags affect three layers:

- MVC discovery: `[FeatureGate("...")]` removes disabled controllers and actions before endpoint mapping, so disabled endpoints return the standard JSON 404 payload and disappear from OpenAPI.
- Dependency injection: feature-specific services are registered only when their feature is enabled, with disabled fallbacks where needed.
- Hosted services: background workers such as search initialization, club version cleanup, and invitation status consumption are registered only when their feature is enabled.

### Frontend behavior

Frontend flags are generated at build time into `src/environments/environment.ts` by `frontend/scripts/generate-env.mjs`.

- Route trees use `canMatch` guards so disabled lazy features are never loaded.
- Subfeature routes use the same inheritance rules as the backend.
- Hidden features should also have their UI entry points removed from navigation and landing pages.
- Disabled URLs should fall through to the client-side not-found route.

### Deployment rule

Backend and frontend flags must be configured together for each deploy.

If the backend disables a feature but the frontend build still exposes it, users will see broken entry points. If the frontend disables a feature but the backend leaves it enabled, the feature may still be reachable directly. Treat the env vars as a single deploy-time contract across both applications.

## Bloom filters

Probabilistic membership filters that front uniqueness checks, so an availability probe can
answer "free" without querying the database. Gated by the `bloom` feature flag; with it off every
lookup reports "unavailable" and callers query the database exactly as they did before.

Each filter is two-tier: a process-local bitmap answers lookups, and a Redis bitmap carries
writes between instances and across restarts. The database stays the sole authority — a filter
can prevent a read, never authorise a write.

Bound from the `BloomFilters` section of `appsettings.json`:

| Key | Default | Purpose |
| --- | --- | --- |
| `RefreshIntervalSeconds` | `30` | How often a filter merges the shared bitmap and checks for a generation flip. |
| `RebuildIntervalHours` | `6` | How often a filter is rebuilt from the database. |
| `RetiredGenerationTtlMinutes` | `60` | How long a superseded bitmap survives after a flip, for instances that have not noticed it yet. |
| `LocalReplayWindowMinutes` | `30` | How long a locally-added value is replayed onto a newly adopted generation. |
| `ForcedRebuildCooldownMinutes` | `15` | Minimum gap between rebuilds triggered by a failed shared write. |
| `Targets:<name>:ExpectedItems` | `250000` | Number of distinct values the filter is sized for. |
| `Targets:<name>:FalsePositiveRate` | `0.01` | Target false-positive rate. A false positive costs one database query. |

### Targets

`username` and `email` are registered. `club-name` is a reserved name — adding it means a
`BloomFilters:Targets:<name>` entry plus an `IBloomFilterSource` implementation registered in
`Container.AddApplicationServices`; nothing in the registry or the rebuild service changes. Club
names have no uniqueness constraint or index today, so a filter over them could only warn about
duplicates rather than report availability, which is why the target stays unregistered.

The `email` filter is exposed anonymously through `GET /api/auth/email/availability`, so the signup
form can tell a returning user their address is already registered instead of failing them after
submission. That is a deliberate tradeoff, not an oversight, and it is worth stating
precisely. The probe is *cheaper to script than signing up*: `POST /api/auth/signup` requires an
antiforgery token and a passing captcha, while this endpoint requires neither and answers with a
boolean. Account-existence testing that used to sit behind a captcha is therefore bounded only by
`RateLimiter:EmailAvailabilityPermitLimit`, which defaults to 15 per minute per IP — half the
username budget, because a list of real addresses is far more minable than a username namespace
that has to be walked. Lower it, or put the endpoint behind a session or form nonce, if account
enumeration matters more than signup ergonomics in your deployment.

Filter membership is never authoritative for a write. Every path that creates or authenticates an
account queries the database regardless of what the filter says; only read-only probes and the
pre-flight signup check are allowed to trust a "definitely absent" answer.

### Rebuilds

A bloom filter has no delete, so bits left by deleted users and lapsed username reservations
accumulate and slowly inflate the false-positive rate. A rebuild reads the authoritative source,
publishes a fresh bitmap under a new generation key, then moves the pointer — which is the only
operation that clears a bit. Values written while the rebuild was reading are replayed from a
pending set, so a signup that commits mid-rebuild cannot be lost.

## Email changes

`POST /api/profile/email` starts a change of the address on an account. The address is a sign-in
identity and an access token claim, so the endpoint is gated by MFA step-up and, for accounts that
have one, the current password — and the change only lands once a confirmation sent to the *new*
address comes back. The address being replaced gets a heads-up carrying no token, so a change
started from a hijacked session is visible in the inbox that still belongs to the owner without
being actionable from it.

Unlike the availability probes, this endpoint *sends mail to an address the caller chose*, which
makes it a way to have EventXperience deliver unsolicited mail to a third party.
`RateLimiter:EmailChangePermitLimit` bounds that, defaulting to 3 per hour and partitioned by
account rather than by IP, so the budget cannot be widened by changing network. Raise it only if you
have a reason; the flow is sized for a person correcting a typo.

Confirming revokes every session: `AuthVersion` is incremented in the same commit as the address —
`JwtConfiguration.OnTokenValidated` rejects tokens whose claim no longer matches — and every
refresh session for the account is dropped. Users sign in again with the new address.

Two consequences are worth knowing about. The address left behind stays set in the `email` bloom
filter until the next rebuild, because a filter has no delete; this is safe, since every write
re-checks the database authoritatively and only read-only probes trust the filter. And pending event
invitations addressed to either address are bound to the account's id on confirmation, because an
unclaimed invitation is otherwise matched by `RecipientEmailNormalized` alone and would drop out of
the recipient's list.
