import { isPlatformBrowser } from '@angular/common';
import { Inject, Injectable, NgZone, PLATFORM_ID } from '@angular/core';
import {
  HttpTransportType,
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { BehaviorSubject, EMPTY, Observable, Subject } from 'rxjs';
import { filter, map } from 'rxjs/operators';

import { environment } from '@environments/environment';
import { AuthTokenService } from '../../../core/api/services/auth-token.service';
import { DiscussionReply, normalizeDiscussionReply } from '../models/club-discussion.types';
import { PostComment, normalizePostComment } from '../models/club-post.types';

/** What the connection pill renders. */
export type RealtimeConnectionState = 'connecting' | 'live' | 'reconnecting' | 'offline';

/** Someone visible in a club roster or a typing indicator. */
export interface RealtimePresenceUser {
  userId: number;
  name: string | null;
  username: string | null;
  /** The username as its owner wrote it. Render this; link by `username`. */
  usernameDisplay?: string | null;
  avatar: string | null;
}

export interface RealtimePresence {
  users: RealtimePresenceUser[];
  totalOnline: number;
}

export type ThreadKind = 'discussion' | 'post';

export type ClubRealtimeEvent =
  /** Emitted after every (re)connect so a thread can reconcile what it missed. */
  | { type: 'Connected' }
  | { type: 'ReplyCreated'; reply: DiscussionReply }
  | { type: 'ReplyUpdated'; reply: DiscussionReply }
  | { type: 'ReplyDeleted'; reply: DiscussionReply }
  | {
      type: 'ReplyReactionChanged';
      discussionId: number;
      replyId: number;
      likeCount: number;
      dislikeCount: number;
    }
  | { type: 'CommentCreated'; comment: PostComment }
  | { type: 'CommentUpdated'; comment: PostComment }
  | { type: 'CommentDeleted'; comment: PostComment }
  | {
      type: 'CommentReactionChanged';
      postId: number;
      commentId: number;
      likeCount: number;
      dislikeCount: number;
    };

type RawRecord = Record<string, unknown>;

/** Reads a value that the backend may send in either camelCase or PascalCase. */
function pick<T>(raw: RawRecord, key: string, fallback: T): T {
  const camel = raw[key];
  if (camel !== undefined && camel !== null) return camel as T;
  const pascal = raw[key.charAt(0).toUpperCase() + key.slice(1)];
  return pascal === undefined || pascal === null ? fallback : (pascal as T);
}

function normalizePresenceUser(raw: RawRecord): RealtimePresenceUser {
  return {
    userId: pick(raw, 'userId', 0),
    name: pick<string | null>(raw, 'name', null),
    username: pick<string | null>(raw, 'username', null),
    avatar: pick<string | null>(raw, 'avatar', null),
  };
}

/**
 * The single realtime connection for club content: discussion replies, post comments,
 * club-wide presence, and per-thread typing.
 *
 * Replaces the two hand-rolled SSE clients. SignalR negotiates WebSockets first and falls
 * back to Server-Sent Events and then long polling on its own, so restrictive networks are
 * still covered without a second code path.
 */
@Injectable({ providedIn: 'root' })
export class ClubRealtimeService {
  /** Matches the server-side typing TTL of 5s with room for one missed refresh. */
  private static readonly TypingRefreshMs = 2000;

  /**
   * How long an unused connection lingers before it is stopped. Swapping between two threads
   * in the same club releases and re-retains synchronously, so tearing the socket down the
   * instant the count hits zero would rebuild it immediately.
   */
  private static readonly IdleShutdownMs = 3000;

  private readonly connections = new Map<number, ClubConnection>();

  constructor(
    private zone: NgZone,
    private authToken: AuthTokenService,
    @Inject(PLATFORM_ID) private platformId: object,
  ) {
    if (!isPlatformBrowser(this.platformId)) return;

    // A hub fixes its principal at handshake time, so any change of token has to rebuild the
    // live connections: signing out leaves the old identity in the roster, signing in leaves
    // typing refused, and a reconnect that landed on an expired token comes back anonymous
    // and stays that way once the refresh arrives. Each connection compares against the token
    // it actually handshook with, so this costs one rebuild per real change.
    this.authToken.accessToken$.subscribe((token) => {
      for (const connection of this.connections.values()) connection.reauthenticate(token);
    });
  }

  /** Club-wide events: discussion replies plus the `Connected` reconciliation signal. */
  events(clubId: number): Observable<ClubRealtimeEvent> {
    const connection = this.acquire(clubId);
    return connection ? connection.events$ : EMPTY;
  }

  connectionState(clubId: number): Observable<RealtimeConnectionState> {
    const connection = this.acquire(clubId);
    return connection ? connection.state$ : EMPTY;
  }

  presence(clubId: number): Observable<RealtimePresence> {
    const connection = this.acquire(clubId);
    return connection ? connection.presence$ : EMPTY;
  }

  /** Everyone currently typing in one thread. Emits an empty list to clear the indicator. */
  typing(clubId: number, kind: ThreadKind, threadId: number): Observable<RealtimePresenceUser[]> {
    const connection = this.acquire(clubId);
    if (!connection) return EMPTY;
    const threadKey = `thread:${kind}:${threadId}`;
    return connection.typing$.pipe(
      filter((entry) => entry.threadKey === threadKey),
      map((entry) => entry.users),
    );
  }

  /**
   * Subscribes to a thread. Returns a teardown that leaves the thread and releases the
   * connection when the last subscriber goes away.
   */
  joinThread(clubId: number, kind: ThreadKind, threadId: number): () => void {
    const connection = this.acquire(clubId);
    if (!connection) return () => undefined;
    connection.retain();
    void connection.joinThread(kind, threadId);
    return () => {
      void connection.leaveThread(kind, threadId);
      connection.release();
    };
  }

  /**
   * Marks the current user as typing. Refreshes on a timer while `true` so the server-side
   * TTL never expires mid-compose, and sends a single explicit stop on `false`.
   */
  setTyping(clubId: number, kind: ThreadKind, threadId: number, isTyping: boolean): void {
    this.connections.get(clubId)?.setTyping(kind, threadId, isTyping);
  }

  private acquire(clubId: number): ClubConnection | null {
    if (!isPlatformBrowser(this.platformId)) return null;

    let connection = this.connections.get(clubId);
    if (!connection) {
      connection = new ClubConnection(
        clubId,
        this.zone,
        this.authToken,
        ClubRealtimeService.TypingRefreshMs,
        ClubRealtimeService.IdleShutdownMs,
        // Dropped once idle, so a session that browses many clubs does not accumulate a
        // connection (and its handlers) per club to rebuild on every token change.
        () => this.connections.delete(clubId),
      );
      this.connections.set(clubId, connection);
    }
    return connection;
  }
}

/** One hub connection, shared by every thread open for the same club. */
class ClubConnection {
  private readonly eventsSubject = new Subject<ClubRealtimeEvent>();
  private readonly presenceSubject = new BehaviorSubject<RealtimePresence>({
    users: [],
    totalOnline: 0,
  });
  private readonly typingSubject = new Subject<{
    threadKey: string;
    users: RealtimePresenceUser[];
  }>();
  private readonly stateSubject = new BehaviorSubject<RealtimeConnectionState>('connecting');

  /** Backoff for a failed *initial* connect; automatic reconnect only covers later drops. */
  private static readonly RetryDelaysMs = [1000, 2000, 5000, 10000, 30000];

  /** Mirrors the server-side roster cap in ClubPresenceStore. */
  private static readonly MaxRosterUsers = 50;

  private hub: HubConnection;
  /** Threads the UI wants joined. Survives restarts. */
  private readonly desiredThreads = new Set<string>();
  /** Threads the server has confirmed. Cleared whenever the socket stops. */
  private readonly activeThreads = new Set<string>();
  /**
   * Threads this connection already tried and had refused. Keeps a refusal from being retried
   * within the same connection, while still letting the next one try again.
   */
  private readonly refusedThreads = new Set<string>();
  private readonly typingTimers = new Map<string, ReturnType<typeof setInterval>>();

  private refCount = 0;
  private started: Promise<void> | null = null;
  private idleTimer: ReturnType<typeof setTimeout> | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private retryAttempt = 0;
  private presenceRefreshPending = false;
  /** The token the live socket handshook with, so a stale principal can be detected. */
  private connectedToken: string | null = null;

  readonly events$ = this.eventsSubject.asObservable();
  readonly presence$ = this.presenceSubject.asObservable();
  readonly typing$ = this.typingSubject.asObservable();
  readonly state$ = this.stateSubject.asObservable();

  constructor(
    private readonly clubId: number,
    private readonly zone: NgZone,
    private readonly authToken: AuthTokenService,
    private readonly typingRefreshMs: number,
    private readonly idleShutdownMs: number,
    private readonly onIdle: () => void,
  ) {
    this.hub = this.build();
    this.registerHandlers();
  }

  retain(): void {
    this.cancelIdleShutdown();
    this.refCount += 1;
    void this.ensureStarted();
  }

  /**
   * Releases one thread's hold. The socket is stopped once nothing is using it, so browsing
   * through clubs does not leave the viewer listed as online in every club they visited.
   */
  release(): void {
    this.refCount = Math.max(0, this.refCount - 1);
    if (this.refCount > 0) return;

    this.cancelIdleShutdown();
    this.zone.runOutsideAngular(() => {
      this.idleTimer = setTimeout(() => {
        this.idleTimer = null;
        if (this.refCount === 0) void this.shutdown();
      }, this.idleShutdownMs);
    });
  }

  /**
   * Rebuilds the socket under a new identity. The subjects are deliberately kept, so existing
   * subscribers stay attached across the swap.
   */
  reauthenticate(token: string | null): void {
    // The live socket already carries this token, so there is nothing to re-do. This is what
    // keeps an unrelated store emission from churning the connection.
    if (this.started !== null && token === this.connectedToken) return;

    if (this.started === null) {
      // Nothing is connected; the next start picks the new token up on its own.
      this.hub = this.build();
      this.registerHandlers();
      return;
    }

    void this.restart();
  }

  private async restart(): Promise<void> {
    // `desiredThreads` is intentionally not snapshotted: a thread swap that lands while the
    // stop below is still pending has to survive the rebuild, and a snapshot taken here would
    // overwrite it with the pre-swap set.
    await this.stopHub();

    this.hub = this.build();
    this.registerHandlers();

    if (this.refCount > 0) void this.ensureStarted();
  }

  private async shutdown(): Promise<void> {
    // Cleared before the await, not after: a thread reopened while the stop is still in
    // flight records its intent during that window and must not be wiped by this teardown.
    this.desiredThreads.clear();
    await this.stopHub();

    // A retain landing mid-stop revives the connection, so leave its state alone.
    if (this.refCount > 0) return;

    // Stopping triggers OnDisconnectedAsync server-side, which drops presence and typing.
    this.presenceSubject.next({ users: [], totalOnline: 0 });
    this.onIdle();
  }

  private async stopHub(): Promise<void> {
    this.cancelRetry();
    for (const key of [...this.typingTimers.keys()]) this.stopTypingTimer(key);
    // Only per-connection state is cleared; the intent survives so a rebuild restores it.
    this.activeThreads.clear();
    this.refusedThreads.clear();
    this.started = null;

    try {
      await this.hub.stop();
    } catch {
      // Already closed, or closing while the transport was mid-flight.
    }
  }

  private cancelIdleShutdown(): void {
    if (this.idleTimer === null) return;
    clearTimeout(this.idleTimer);
    this.idleTimer = null;
  }

  private cancelRetry(): void {
    if (this.retryTimer === null) return;
    clearTimeout(this.retryTimer);
    this.retryTimer = null;
  }

  async joinThread(kind: ThreadKind, threadId: number): Promise<void> {
    const key = `${kind}:${threadId}`;
    // Recorded before the round trip, so a join issued while the socket is down or rebuilding
    // is still replayed by afterConnect.
    this.desiredThreads.add(key);

    await this.ensureStarted();
    if (this.hub.state !== HubConnectionState.Connected) return;
    // afterConnect replays the desired set, so a join that raced the handshake is already
    // settled: confirmed, refused by this connection, or dropped by a leave in that window.
    if (
      this.activeThreads.has(key) ||
      this.refusedThreads.has(key) ||
      !this.desiredThreads.has(key)
    ) {
      return;
    }

    await this.invokeJoin(kind, threadId);
  }

  private async invokeJoin(kind: ThreadKind, threadId: number): Promise<void> {
    const key = `${kind}:${threadId}`;
    const method = kind === 'discussion' ? 'JoinDiscussion' : 'JoinPost';
    try {
      await this.hub.invoke(method, this.clubId, threadId);

      // A leave that landed while this was pending already dropped the intent and queued the
      // server-side leave, so recording it active here would leave a stale key that makes a
      // later reopen skip its join.
      if (this.desiredThreads.has(key)) this.activeThreads.add(key);
      this.refusedThreads.delete(key);
    } catch {
      // Guarded like the success path: a leave that landed during the await already cleared
      // this key, and re-recording it would make a later reopen skip its join.
      if (this.desiredThreads.has(key)) this.refusedThreads.add(key);

      // Only the confirmation is dropped, never the intent. A refusal is not reliably
      // authoritative: a reconnect that lands on an expired token is Connected but anonymous,
      // so a private thread is refused under a principal that the pending refresh is about to
      // replace. Intent belongs to the UI and is cleared only by leaveThread or shutdown, so
      // the worst case here is one wasted invoke per reconnect on a genuinely forbidden thread.
      this.activeThreads.delete(key);
    }
  }

  async leaveThread(kind: ThreadKind, threadId: number): Promise<void> {
    const key = `${kind}:${threadId}`;
    this.stopTypingTimer(key);
    this.desiredThreads.delete(key);
    this.activeThreads.delete(key);
    this.refusedThreads.delete(key);
    if (this.hub.state !== HubConnectionState.Connected) return;

    const method = kind === 'discussion' ? 'LeaveDiscussion' : 'LeavePost';
    try {
      await (kind === 'discussion'
        ? this.hub.invoke(method, threadId)
        : this.hub.invoke(method, this.clubId, threadId));
    } catch {
      // Leaving is best-effort; a dropped connection cleans up server-side anyway.
    }
  }

  setTyping(kind: ThreadKind, threadId: number, isTyping: boolean): void {
    const key = `${kind}:${threadId}`;
    if (!isTyping) {
      this.stopTypingTimer(key);
      void this.sendTyping(kind, threadId, false);
      return;
    }

    if (this.typingTimers.has(key)) return;

    void this.sendTyping(kind, threadId, true);
    // Outside Angular's zone: a 2s heartbeat must not schedule change detection.
    this.zone.runOutsideAngular(() => {
      this.typingTimers.set(
        key,
        setInterval(() => void this.sendTyping(kind, threadId, true), this.typingRefreshMs),
      );
    });
  }

  private async sendTyping(kind: ThreadKind, threadId: number, isTyping: boolean): Promise<void> {
    if (this.hub.state !== HubConnectionState.Connected) return;
    // The server rejects typing for a thread it has not seen joined, so gate on the
    // confirmed set rather than on intent.
    if (!this.activeThreads.has(`${kind}:${threadId}`)) return;
    try {
      await this.hub.invoke('Typing', kind, threadId, isTyping);
    } catch {
      // Anonymous viewers are refused by the server; nothing to surface in the UI.
    }
  }

  private stopTypingTimer(key: string): void {
    const timer = this.typingTimers.get(key);
    if (timer === undefined) return;
    clearInterval(timer);
    this.typingTimers.delete(key);
  }

  private build(): HubConnection {
    // environment.backendUrl already ends in `/api`, which is also where the hub lives.
    const url = `${environment.backendUrl}/hubs/clubs`;

    return new HubConnectionBuilder()
      .withUrl(url, {
        // Re-read on every (re)connect, so signing in mid-session upgrades the connection
        // instead of leaving it stuck as anonymous.
        accessTokenFactory: () => {
          // Recorded so a later token change can be compared against what this socket used.
          this.connectedToken = this.authToken.accessToken ?? null;
          return this.connectedToken ?? '';
        },
        withCredentials: true,
        transport:
          HttpTransportType.WebSockets |
          HttpTransportType.ServerSentEvents |
          HttpTransportType.LongPolling,
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(environment.production ? LogLevel.Error : LogLevel.Warning)
      .build();
  }

  private registerHandlers(): void {
    this.hub.on('ReplyCreated', (raw: RawRecord) =>
      this.emit({ type: 'ReplyCreated', reply: normalizeDiscussionReply(raw as never) }),
    );
    this.hub.on('ReplyUpdated', (raw: RawRecord) =>
      this.emit({ type: 'ReplyUpdated', reply: normalizeDiscussionReply(raw as never) }),
    );
    this.hub.on('ReplyDeleted', (raw: RawRecord) =>
      this.emit({ type: 'ReplyDeleted', reply: normalizeDiscussionReply(raw as never) }),
    );
    this.hub.on('ReplyReactionChanged', (raw: RawRecord) =>
      this.emit({
        type: 'ReplyReactionChanged',
        discussionId: pick(raw, 'discussionId', 0),
        replyId: pick(raw, 'replyId', 0),
        likeCount: pick(raw, 'likeCount', 0),
        dislikeCount: pick(raw, 'dislikeCount', 0),
      }),
    );

    this.hub.on('CommentCreated', (raw: RawRecord) =>
      this.emit({ type: 'CommentCreated', comment: normalizePostComment(raw as never) }),
    );
    this.hub.on('CommentUpdated', (raw: RawRecord) =>
      this.emit({ type: 'CommentUpdated', comment: normalizePostComment(raw as never) }),
    );
    this.hub.on('CommentDeleted', (raw: RawRecord) =>
      this.emit({ type: 'CommentDeleted', comment: normalizePostComment(raw as never) }),
    );
    this.hub.on('CommentReactionChanged', (raw: RawRecord) =>
      this.emit({
        type: 'CommentReactionChanged',
        postId: pick(raw, 'postId', 0),
        commentId: pick(raw, 'commentId', 0),
        likeCount: pick(raw, 'likeCount', 0),
        dislikeCount: pick(raw, 'dislikeCount', 0),
      }),
    );

    this.hub.on('PresenceSnapshot', (raw: RawRecord) =>
      this.zone.run(() =>
        this.presenceSubject.next({
          users: pick<RawRecord[]>(raw, 'users', []).map(normalizePresenceUser),
          totalOnline: pick(raw, 'totalOnline', 0),
        }),
      ),
    );

    this.hub.on('PresenceChanged', (raw: RawRecord) => this.applyPresenceDiff(raw));

    this.hub.on('TypingChanged', (raw: RawRecord) =>
      this.zone.run(() =>
        this.typingSubject.next({
          threadKey: pick(raw, 'threadKey', ''),
          users: pick<RawRecord[]>(raw, 'users', []).map(normalizePresenceUser),
        }),
      ),
    );

    this.hub.onreconnecting(() => this.zone.run(() => this.stateSubject.next('reconnecting')));
    this.hub.onreconnected(() => void this.afterConnect());
    this.hub.onclose(() => {
      this.zone.run(() => this.stateSubject.next('offline'));

      // Reached either because we stopped the socket ourselves, or because automatic
      // reconnect exhausted its delays. In the latter case the resolved start promise would
      // otherwise keep ensureStarted() short-circuiting, stranding the thread offline for
      // good, so clear it and fall back to the same retry path a failed first connect uses.
      this.started = null;
      this.activeThreads.clear();
      this.refusedThreads.clear();
      if (this.refCount > 0) this.scheduleRetry();
    });
  }

  private applyPresenceDiff(raw: RawRecord): void {
    const joined = pick<RawRecord | null>(raw, 'joined', null);
    const leftUserId = pick<number | null>(raw, 'leftUserId', null);
    const totalOnline = pick(raw, 'totalOnline', 0);

    this.zone.run(() => {
      const current = this.presenceSubject.value.users;
      let users = current;

      if (joined) {
        const user = normalizePresenceUser(joined);
        users = current.some((existing) => existing.userId === user.userId)
          ? current
          : // Diffs are uncapped, so hold the client list to the same limit the snapshot uses.
            [...current, user].slice(0, ClubConnection.MaxRosterUsers);
      } else if (leftUserId !== null) {
        users = current.filter((existing) => existing.userId !== leftUserId);
      }

      this.presenceSubject.next({ users, totalOnline });
      this.replenishRosterIfDrained(users.length, totalOnline);
    });
  }

  /**
   * Members beyond the roster cap are never named by a diff, so once enough visible members
   * leave, the list can drain while the count stays high. Ask for a fresh snapshot instead.
   */
  private replenishRosterIfDrained(visible: number, totalOnline: number): void {
    const expected = Math.min(totalOnline, ClubConnection.MaxRosterUsers);
    if (visible >= expected) return;
    if (this.hub.state !== HubConnectionState.Connected) return;
    if (this.presenceRefreshPending) return;

    this.presenceRefreshPending = true;
    void this.hub
      .invoke('RequestPresence', this.clubId)
      .catch(() => undefined)
      .finally(() => {
        this.presenceRefreshPending = false;
      });
  }

  private emit(event: ClubRealtimeEvent): void {
    this.zone.run(() => this.eventsSubject.next(event));
  }

  private ensureStarted(): Promise<void> {
    if (this.started) return this.started;

    this.cancelRetry();
    // Reached from the retry timer, which runs outside Angular, so this needs the zone or
    // the pill stays on its previous value for the whole retry cycle.
    this.zone.run(() => this.stateSubject.next('connecting'));
    // SignalR's own timers and transport callbacks do not need to churn change detection.
    this.started = this.zone
      .runOutsideAngular(() => this.hub.start())
      .then(() => {
        this.retryAttempt = 0;
        return this.afterConnect();
      })
      .catch(() => {
        this.zone.run(() => this.stateSubject.next('offline'));
        this.started = null;
        // withAutomaticReconnect only covers drops after a successful handshake, so a failed
        // first connect has to be retried here. The mounted thread already retained this
        // connection and will not retain it again.
        this.scheduleRetry();
      });

    return this.started;
  }

  private scheduleRetry(): void {
    if (this.refCount === 0 || this.retryTimer !== null) return;

    const delays = ClubConnection.RetryDelaysMs;
    const delay = delays[Math.min(this.retryAttempt, delays.length - 1)];
    this.retryAttempt += 1;

    this.zone.runOutsideAngular(() => {
      this.retryTimer = setTimeout(() => {
        this.retryTimer = null;
        if (this.refCount > 0 && this.started === null) void this.ensureStarted();
      }, delay);
    });
  }

  /**
   * Runs after both the first connect and every automatic reconnect: re-join the club and
   * every thread the UI still wants open, then tell subscribers to reconcile.
   */
  private async afterConnect(): Promise<void> {
    // A fresh connection may carry a different principal, so past refusals do not bind it.
    this.refusedThreads.clear();

    try {
      await this.hub.invoke('JoinClub', this.clubId);
    } catch {
      // A private club the viewer cannot read still leaves the REST data usable.
    }

    for (const key of [...this.desiredThreads]) {
      const [kind, id] = key.split(':');
      await this.invokeJoin(kind as ThreadKind, Number(id));
    }

    this.zone.run(() => {
      this.stateSubject.next('live');
      this.eventsSubject.next({ type: 'Connected' });
    });
  }
}
