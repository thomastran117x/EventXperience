import { Subject, of, throwError } from 'rxjs';

import { User } from '../../../../core/stores/user.model';
import {
  ClubRealtimeEvent,
  RealtimeConnectionState,
  RealtimePresence,
  RealtimePresenceUser,
} from '../../services/club-realtime.service';
import { ThreadDataSource, ThreadLabels, ThreadPage } from './thread-data-source';
import { ThreadDisplayItem } from './thread-item';
import { ThreadComponent } from './thread.component';

const LABELS: ThreadLabels = {
  heading: 'Replies',
  singular: 'reply',
  plural: 'replies',
  composerPlaceholder: 'Join the discussion...',
  replyPlaceholder: 'Write a reply...',
  emptyTitle: 'No replies yet',
  emptyBody: 'Start the conversation.',
  deletedText: 'Reply deleted',
  deleteConfirm: 'Delete this reply?',
  signInPrompt: 'to reply or react.',
};

function makeItem(overrides: Partial<ThreadDisplayItem> = {}): ThreadDisplayItem {
  return {
    id: 1,
    createdAt: '2026-08-15T12:00:00Z',
    updatedAt: '2026-08-15T12:00:00Z',
    userId: 7,
    content: 'Hello',
    author: { id: 7, name: 'Taylor', username: 'taylor', usernameDisplay: 'taylor', avatar: null },
    isDeleted: false,
    likeCount: 0,
    dislikeCount: 0,
    currentUserReaction: null,
    directReplyCount: 0,
    ...overrides,
  };
}

function makePage(
  items: ThreadDisplayItem[],
  overrides: Partial<ThreadPage<ThreadDisplayItem>> = {},
): ThreadPage<ThreadDisplayItem> {
  return {
    items,
    totalCount: items.length,
    nextCursor: null,
    hasMore: false,
    ...overrides,
  };
}

describe('ThreadComponent', () => {
  let source: jasmine.SpyObj<ThreadDataSource<ThreadDisplayItem>>;
  let realtime: jasmine.SpyObj<{
    events: unknown;
    connectionState: unknown;
    presence: unknown;
    typing: unknown;
    joinThread: unknown;
    setTyping: unknown;
  }>;
  let events$: Subject<ClubRealtimeEvent>;
  let state$: Subject<RealtimeConnectionState>;
  let presence$: Subject<RealtimePresence>;
  let typing$: Subject<RealtimePresenceUser[]>;
  let leaveThread: jasmine.Spy;
  let component: ThreadComponent;

  const currentUser = { Id: 7 } as User;

  beforeEach(() => {
    events$ = new Subject();
    state$ = new Subject();
    presence$ = new Subject();
    typing$ = new Subject();
    leaveThread = jasmine.createSpy('leaveThread');

    source = jasmine.createSpyObj<ThreadDataSource<ThreadDisplayItem>>('source', [
      'list',
      'create',
      'update',
      'remove',
      'react',
      'clearReaction',
      'parentIdOf',
      'matchLiveEvent',
    ]);
    Object.assign(source, {
      kind: 'discussion' as const,
      clubId: 1,
      threadId: 9,
      labels: LABELS,
    });
    source.parentIdOf.and.returnValue(null);
    source.matchLiveEvent.and.returnValue(null);
    source.list.and.returnValue(of(makePage([])));

    realtime = jasmine.createSpyObj('realtime', [
      'events',
      'connectionState',
      'presence',
      'typing',
      'joinThread',
      'setTyping',
    ]);
    (realtime.events as jasmine.Spy).and.returnValue(events$);
    (realtime.connectionState as jasmine.Spy).and.returnValue(state$);
    (realtime.presence as jasmine.Spy).and.returnValue(presence$);
    (realtime.typing as jasmine.Spy).and.returnValue(typing$);
    (realtime.joinThread as jasmine.Spy).and.returnValue(leaveThread);

    component = new ThreadComponent(realtime as never);
    component.source = source;
    component.currentUser = currentUser;
  });

  // ── Loading ──────────────────────────────────────────────────────────────

  it('loads roots and joins the realtime thread on init', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })], { totalCount: 4 })));

    component.ngOnInit();

    expect(component.roots.length).toBe(1);
    expect(component.totalRoots).toBe(4);
    expect(component.loading).toBeFalse();
    expect(realtime.joinThread).toHaveBeenCalledWith(1, 'discussion', 9);
  });

  it('surfaces a load failure without leaving the spinner up', () => {
    source.list.and.returnValue(throwError(() => new Error('boom')));

    component.ngOnInit();

    expect(component.error).toBe('boom');
    expect(component.loading).toBeFalse();
  });

  it('appends a page without duplicating rows already loaded', () => {
    source.list.and.returnValue(
      of(makePage([makeItem({ id: 1 })], { nextCursor: 'c1', hasMore: true, totalCount: 2 })),
    );
    component.ngOnInit();

    source.list.and.returnValue(of(makePage([makeItem({ id: 1 }), makeItem({ id: 2 })])));
    component.loadRoots(true);

    expect(component.roots.map((node) => node.id)).toEqual([2, 1]);
  });

  it('clears loaded state when the sort flips', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();

    component.changeSort('Oldest');

    expect(component.sort).toBe('Oldest');
    expect(source.list).toHaveBeenCalledWith(null, 'Oldest', null);
  });

  it('ignores a sort change to the current sort', () => {
    component.ngOnInit();
    source.list.calls.reset();

    component.changeSort('Newest');

    expect(source.list).not.toHaveBeenCalled();
  });

  // ── Composing ────────────────────────────────────────────────────────────

  it('posts a root item, clears the composer, and stops the typing indicator', () => {
    component.ngOnInit();
    source.create.and.returnValue(of(makeItem({ id: 20 })));
    component.newText = '  New reply  ';
    component.onComposerInput();

    component.submitRoot();

    expect(source.create).toHaveBeenCalledWith('New reply', null);
    expect(component.newText).toBe('');
    expect(component.roots.map((node) => node.id)).toContain(20);
    expect(realtime.setTyping).toHaveBeenCalledWith(1, 'discussion', 9, false);
  });

  it('refuses to post whitespace', () => {
    component.ngOnInit();
    component.newText = '   ';

    component.submitRoot();

    expect(source.create).not.toHaveBeenCalled();
  });

  it('reports a failed post and re-enables the button', () => {
    component.ngOnInit();
    source.create.and.returnValue(throwError(() => new Error('nope')));
    component.newText = 'Hi';

    component.submitRoot();

    expect(component.error).toBe('nope');
    expect(component.submitting).toBeFalse();
  });

  it('only shows the character counter near the limit', () => {
    component.newText = 'short';
    expect(component.showCharCount).toBeFalse();

    component.newText = 'x'.repeat(component.maxLength - 10);
    expect(component.showCharCount).toBeTrue();
    expect(component.remainingChars).toBe(10);
  });

  it('sends on Enter and inserts a newline on Shift+Enter', () => {
    component.ngOnInit();
    source.create.and.returnValue(of(makeItem({ id: 21 })));
    component.newText = 'Send me';

    const shiftEnter = new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true });
    component.onComposerKeydown(shiftEnter);
    expect(source.create).not.toHaveBeenCalled();

    const enter = new KeyboardEvent('keydown', { key: 'Enter' });
    component.onComposerKeydown(enter);
    expect(source.create).toHaveBeenCalled();
  });

  it('starts and stops typing as the composer fills and empties', () => {
    component.ngOnInit();

    component.newText = 'a';
    component.onComposerInput();
    expect(realtime.setTyping).toHaveBeenCalledWith(1, 'discussion', 9, true);

    (realtime.setTyping as jasmine.Spy).calls.reset();
    component.newText = 'ab';
    component.onComposerInput();
    expect(realtime.setTyping).not.toHaveBeenCalled();

    component.newText = '';
    component.onComposerInput();
    expect(realtime.setTyping).toHaveBeenCalledWith(1, 'discussion', 9, false);
  });

  // ── Live events ──────────────────────────────────────────────────────────

  it('inserts an item that arrives live', () => {
    component.ngOnInit();
    const item = makeItem({ id: 31 });
    source.matchLiveEvent.and.returnValue({ kind: 'created', item });

    events$.next({ type: 'ReplyCreated', reply: item as never });

    expect(component.roots.map((node) => node.id)).toContain(31);
    expect(component.totalItems).toBe(1);
  });

  it('collapses the optimistic insert and the echoed live event into one row', () => {
    component.ngOnInit();
    const item = makeItem({ id: 40 });
    source.create.and.returnValue(of(item));
    component.newText = 'Once';
    component.submitRoot();

    source.matchLiveEvent.and.returnValue({ kind: 'created', item });
    events$.next({ type: 'ReplyCreated', reply: item as never });

    expect(component.roots.filter((node) => node.id === 40).length).toBe(1);
    expect(component.totalItems).toBe(1);
  });

  it('keeps the viewer own reaction when an update arrives live', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1, currentUserReaction: 'Like' })])));
    component.ngOnInit();

    const updated = makeItem({ id: 1, content: 'Edited', currentUserReaction: null });
    source.matchLiveEvent.and.returnValue({ kind: 'updated', item: updated });
    events$.next({ type: 'ReplyUpdated', reply: updated as never });

    expect(component.roots[0].content).toBe('Edited');
    expect(component.roots[0].currentUserReaction).toBe('Like');
  });

  it('applies live reaction counts to the matching node', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();

    source.matchLiveEvent.and.returnValue({
      kind: 'reaction',
      itemId: 1,
      likeCount: 9,
      dislikeCount: 2,
    });
    events$.next({
      type: 'ReplyReactionChanged',
      discussionId: 9,
      replyId: 1,
      likeCount: 9,
      dislikeCount: 2,
    });

    expect(component.roots[0].likeCount).toBe(9);
    expect(component.roots[0].dislikeCount).toBe(2);
  });

  it('ignores events the data source says belong to another thread', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();
    source.matchLiveEvent.and.returnValue(null);

    events$.next({ type: 'ReplyCreated', reply: makeItem({ id: 99 }) as never });

    expect(component.roots.length).toBe(1);
  });

  it('reconciles on reconnect', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();
    source.list.calls.reset();
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 }), makeItem({ id: 2 })])));

    events$.next({ type: 'Connected' });

    expect(source.list).toHaveBeenCalled();
    expect(component.roots.map((node) => node.id)).toEqual([2, 1]);
  });

  it('defers reconciliation while a load is still in flight', () => {
    const pending = new Subject<ThreadPage<ThreadDisplayItem> | null>();
    source.list.and.returnValue(pending);
    component.ngOnInit();

    events$.next({ type: 'Connected' });
    expect(source.list.calls.count()).toBe(1);

    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    pending.next(makePage([]));
    pending.complete();

    expect(source.list.calls.count()).toBeGreaterThan(1);
  });

  it('tracks connection state, presence, and typing from the realtime service', () => {
    component.ngOnInit();

    state$.next('reconnecting');
    expect(component.connectionState).toBe('reconnecting');

    presence$.next({
      users: [
        { userId: 8, name: 'Robin', username: 'robin', usernameDisplay: 'robin', avatar: null },
      ],
      totalOnline: 3,
    });
    expect(component.totalOnline).toBe(3);
    expect(component.presenceUsers.length).toBe(1);

    typing$.next([
      { userId: 8, name: 'Robin', username: 'robin', usernameDisplay: 'robin', avatar: null },
    ]);
    expect(component.typingLabel).toBe('Robin is typing');
  });

  it('never lists the viewer in the typing label', () => {
    component.ngOnInit();

    typing$.next([
      { userId: 7, name: 'Taylor', username: 'taylor', usernameDisplay: 'taylor', avatar: null },
    ]);
    expect(component.typingLabel).toBe('');

    typing$.next([
      { userId: 7, name: 'Taylor', username: 'taylor', usernameDisplay: 'taylor', avatar: null },
      { userId: 8, name: 'Robin', username: 'robin', usernameDisplay: 'robin', avatar: null },
    ]);
    expect(component.typingLabel).toBe('Robin is typing');
  });

  it('names two typists and summarizes three or more', () => {
    component.ngOnInit();

    typing$.next([
      { userId: 8, name: 'Robin', username: 'robin', usernameDisplay: 'robin', avatar: null },
      { userId: 9, name: 'Sam', username: 'sam', usernameDisplay: 'sam', avatar: null },
    ]);
    expect(component.typingLabel).toBe('Robin and Sam are typing');

    typing$.next([
      { userId: 8, name: 'Robin', username: 'robin', usernameDisplay: 'robin', avatar: null },
      { userId: 9, name: 'Sam', username: 'sam', usernameDisplay: 'sam', avatar: null },
      { userId: 10, name: 'Alex', username: 'alex', usernameDisplay: 'alex', avatar: null },
    ]);
    expect(component.typingLabel).toBe('Several people are typing');
  });

  // ── Node actions ─────────────────────────────────────────────────────────

  it('loads children under a node', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1, directReplyCount: 2 })])));
    component.ngOnInit();
    const parent = component.roots[0];

    source.list.and.returnValue(
      of(makePage([makeItem({ id: 2 }), makeItem({ id: 3 })], { totalCount: 2 })),
    );
    component.handleNodeAction({ type: 'loadChildren', node: parent, append: false });

    expect(parent.childrenLoaded).toBeTrue();
    expect(parent.children.length).toBe(2);
    expect(parent.loadingChildren).toBeFalse();
  });

  it('nests a created child under a parent whose children are loaded', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1, directReplyCount: 0 })])));
    component.ngOnInit();
    const parent = component.roots[0];
    parent.childrenLoaded = true;

    const child = makeItem({ id: 50 });
    source.parentIdOf.and.returnValue(1);
    source.matchLiveEvent.and.returnValue({ kind: 'created', item: child });
    events$.next({ type: 'ReplyCreated', reply: child as never });

    expect(parent.children.map((node) => node.id)).toEqual([50]);
    expect(parent.directReplyCount).toBe(1);
  });

  it('only bumps the count when the parent children are not loaded yet', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1, directReplyCount: 3 })])));
    component.ngOnInit();
    const parent = component.roots[0];

    const child = makeItem({ id: 51 });
    source.parentIdOf.and.returnValue(1);
    source.matchLiveEvent.and.returnValue({ kind: 'created', item: child });
    events$.next({ type: 'ReplyCreated', reply: child as never });

    expect(parent.children.length).toBe(0);
    expect(parent.directReplyCount).toBe(4);
  });

  it('edits an item through the data source', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();
    const node = component.roots[0];
    source.update.and.returnValue(of(makeItem({ id: 1, content: 'Edited' })));

    component.handleNodeAction({ type: 'edit', node, content: 'Edited' });

    expect(source.update).toHaveBeenCalledWith(1, 'Edited');
    expect(node.content).toBe('Edited');
    expect(node.editOpen).toBeFalse();
  });

  it('replaces a deleted item with its redacted placeholder', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();
    const node = component.roots[0];
    source.remove.and.returnValue(
      of(makeItem({ id: 1, isDeleted: true, content: null, author: null })),
    );

    component.handleNodeAction({ type: 'delete', node });

    expect(node.isDeleted).toBeTrue();
    expect(node.deleteConfirm).toBeFalse();
  });

  it('applies a reaction optimistically and keeps the server totals', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1, likeCount: 2 })])));
    component.ngOnInit();
    const node = component.roots[0];
    source.react.and.returnValue(
      of({ likeCount: 3, dislikeCount: 0, currentUserReaction: 'Like' as const }),
    );

    component.handleNodeAction({ type: 'react', node, reaction: 'Like' });

    expect(node.likeCount).toBe(3);
    expect(node.currentUserReaction).toBe('Like');
  });

  it('clears a reaction when the same one is tapped again', () => {
    source.list.and.returnValue(
      of(makePage([makeItem({ id: 1, likeCount: 3, currentUserReaction: 'Like' })])),
    );
    component.ngOnInit();
    const node = component.roots[0];
    source.clearReaction.and.returnValue(
      of({ likeCount: 2, dislikeCount: 0, currentUserReaction: null }),
    );

    component.handleNodeAction({ type: 'react', node, reaction: 'Like' });

    expect(source.clearReaction).toHaveBeenCalledWith(1);
    expect(node.currentUserReaction).toBeNull();
  });

  it('rolls the optimistic reaction back when the request fails', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1, likeCount: 2 })])));
    component.ngOnInit();
    const node = component.roots[0];
    source.react.and.returnValue(throwError(() => new Error('nope')));

    component.handleNodeAction({ type: 'react', node, reaction: 'Like' });

    expect(node.likeCount).toBe(2);
    expect(node.currentUserReaction).toBeNull();
    expect(node.error).toBe('nope');
  });

  it('asks anonymous viewers to sign in before reacting', () => {
    component.currentUser = null;
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();
    const node = component.roots[0];

    component.handleNodeAction({ type: 'react', node, reaction: 'Like' });

    expect(source.react).not.toHaveBeenCalled();
    expect(node.error).toContain('Sign in');
  });

  // ── Lifecycle ────────────────────────────────────────────────────────────

  it('groups consecutive messages from the same author', () => {
    source.list.and.returnValue(
      of(
        makePage([
          makeItem({ id: 1, userId: 7, createdAt: '2026-08-15T12:00:00Z' }),
          makeItem({ id: 2, userId: 7, createdAt: '2026-08-15T12:01:00Z' }),
          makeItem({ id: 3, userId: 8, createdAt: '2026-08-15T12:02:00Z' }),
        ]),
      ),
    );
    component.sort = 'Oldest';
    component.ngOnInit();

    expect(component.isRootGrouped(0)).toBeFalse();
    expect(component.isRootGrouped(1)).toBeTrue();
    expect(component.isRootGrouped(2)).toBeFalse();
  });

  it('reports failures from every node action on the node itself', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1, directReplyCount: 1 })])));
    component.ngOnInit();
    const node = component.roots[0];

    source.list.and.returnValue(throwError(() => new Error('children failed')));
    component.handleNodeAction({ type: 'loadChildren', node, append: false });
    expect(node.error).toBe('children failed');
    expect(node.loadingChildren).toBeFalse();

    source.create.and.returnValue(throwError(() => new Error('child failed')));
    component.handleNodeAction({ type: 'create', node, content: 'Nested' });
    expect(node.error).toBe('child failed');
    expect(node.busy).toBeFalse();

    source.update.and.returnValue(throwError(() => new Error('edit failed')));
    component.handleNodeAction({ type: 'edit', node, content: 'Edited' });
    expect(node.error).toBe('edit failed');

    source.remove.and.returnValue(throwError(() => new Error('delete failed')));
    component.handleNodeAction({ type: 'delete', node });
    expect(node.error).toBe('delete failed');
    expect(node.deleteConfirm).toBeFalse();
  });

  it('creates a child through a node and clears that node composer', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();
    const parent = component.roots[0];
    parent.replyOpen = true;
    parent.replyText = 'Nested';
    source.create.and.returnValue(of(makeItem({ id: 60 })));
    source.parentIdOf.and.returnValue(1);
    parent.childrenLoaded = true;

    component.handleNodeAction({ type: 'create', node: parent, content: 'Nested' });

    expect(source.create).toHaveBeenCalledWith('Nested', 1);
    expect(parent.replyOpen).toBeFalse();
    expect(parent.replyText).toBe('');
    expect(parent.children.map((node) => node.id)).toEqual([60]);
  });

  it('appends a further page of children', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1, directReplyCount: 2 })])));
    component.ngOnInit();
    const parent = component.roots[0];

    source.list.and.returnValue(
      of(makePage([makeItem({ id: 2 })], { nextCursor: 'c1', hasMore: true, totalCount: 2 })),
    );
    component.handleNodeAction({ type: 'loadChildren', node: parent, append: false });
    expect(parent.hasMoreChildren).toBeTrue();

    source.list.and.returnValue(of(makePage([makeItem({ id: 3 })], { totalCount: 2 })));
    component.handleNodeAction({ type: 'loadChildren', node: parent, append: true });

    expect(parent.children.map((node) => node.id).sort()).toEqual([2, 3]);
  });

  it('ignores a live event naming a parent that is not loaded', () => {
    source.list.and.returnValue(of(makePage([])));
    component.ngOnInit();

    const orphan = makeItem({ id: 70 });
    source.parentIdOf.and.returnValue(999);
    source.matchLiveEvent.and.returnValue({ kind: 'created', item: orphan });
    events$.next({ type: 'ReplyCreated', reply: orphan as never });

    expect(component.roots.length).toBe(0);
  });

  it('ignores a live update for an item it has not loaded', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();

    const unknown = makeItem({ id: 555, content: 'Elsewhere' });
    source.matchLiveEvent.and.returnValue({ kind: 'updated', item: unknown });
    events$.next({ type: 'ReplyUpdated', reply: unknown as never });

    expect(component.roots.length).toBe(1);
    expect(component.roots[0].content).toBe('Hello');
  });

  it('ignores a live reaction for an item it has not loaded', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1, likeCount: 0 })])));
    component.ngOnInit();

    source.matchLiveEvent.and.returnValue({
      kind: 'reaction',
      itemId: 999,
      likeCount: 5,
      dislikeCount: 5,
    });
    events$.next({
      type: 'ReplyReactionChanged',
      discussionId: 9,
      replyId: 999,
      likeCount: 5,
      dislikeCount: 5,
    });

    expect(component.roots[0].likeCount).toBe(0);
  });

  it('handles an empty page body without touching loaded rows', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();

    source.list.and.returnValue(of(null));
    component.loadRoots(false);

    expect(component.roots.length).toBe(1);
    expect(component.loading).toBeFalse();
  });

  it('keeps the reaction optimistic result when the server body is missing', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1, likeCount: 2 })])));
    component.ngOnInit();
    const node = component.roots[0];
    source.react.and.returnValue(of(null));

    component.handleNodeAction({ type: 'react', node, reaction: 'Like' });

    expect(node.likeCount).toBe(3);
    expect(node.currentUserReaction).toBe('Like');
  });

  it('swaps a like for a dislike in one tap', () => {
    source.list.and.returnValue(
      of(
        makePage([makeItem({ id: 1, likeCount: 3, dislikeCount: 1, currentUserReaction: 'Like' })]),
      ),
    );
    component.ngOnInit();
    const node = component.roots[0];
    source.react.and.returnValue(
      of({ likeCount: 2, dislikeCount: 2, currentUserReaction: 'Dislike' as const }),
    );

    component.handleNodeAction({ type: 'react', node, reaction: 'Dislike' });

    expect(node.currentUserReaction).toBe('Dislike');
    expect(node.dislikeCount).toBe(2);
  });

  it('emits a corrected total when reconciliation finds a different count', () => {
    const counts: number[] = [];
    component.countChange.subscribe((value) => counts.push(value));
    component.initialCount = 5;
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })], { totalCount: 1 })));
    component.ngOnInit();

    source.list.and.returnValue(
      of(makePage([makeItem({ id: 1 }), makeItem({ id: 2 })], { totalCount: 2 })),
    );
    events$.next({ type: 'Connected' });

    expect(counts[counts.length - 1]).toBe(6);
  });

  it('reports typing from a nested composer as well as the root one', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();
    const node = component.roots[0];

    component.handleNodeAction({ type: 'typing', node, active: true });
    expect(realtime.setTyping).toHaveBeenCalledWith(1, 'discussion', 9, true);

    (realtime.setTyping as jasmine.Spy).calls.reset();
    component.handleNodeAction({ type: 'typing', node, active: false });
    expect(realtime.setTyping).toHaveBeenCalledWith(1, 'discussion', 9, false);
  });

  it('keeps reporting typing while any composer still holds content', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();
    const node = component.roots[0];

    component.newText = 'draft';
    component.onComposerInput();
    component.handleNodeAction({ type: 'typing', node, active: true });
    (realtime.setTyping as jasmine.Spy).calls.reset();

    // The nested composer emptying must not cancel typing while the root still has content.
    component.handleNodeAction({ type: 'typing', node, active: false });
    expect(realtime.setTyping).not.toHaveBeenCalledWith(1, 'discussion', 9, false);

    component.newText = '';
    component.onComposerInput();
    expect(realtime.setTyping).toHaveBeenCalledWith(1, 'discussion', 9, false);
  });

  it('clears the nested composer typing flag once its post lands', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();
    const parent = component.roots[0];
    component.handleNodeAction({ type: 'typing', node: parent, active: true });
    (realtime.setTyping as jasmine.Spy).calls.reset();

    source.create.and.returnValue(of(makeItem({ id: 61 })));
    component.handleNodeAction({ type: 'create', node: parent, content: 'Nested' });

    expect(realtime.setTyping).toHaveBeenCalledWith(1, 'discussion', 9, false);
  });

  it('re-asserts typing after a reconnect rebuilds the socket', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();
    component.newText = 'still drafting';
    component.onComposerInput();
    (realtime.setTyping as jasmine.Spy).calls.reset();

    events$.next({ type: 'Connected' });

    // The rebuilt socket dropped its heartbeat, so the composer's state has to be re-sent.
    expect(realtime.setTyping).toHaveBeenCalledWith(1, 'discussion', 9, true);
  });

  it('does not re-assert typing after a reconnect when every composer is empty', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();
    (realtime.setTyping as jasmine.Spy).calls.reset();

    events$.next({ type: 'Connected' });

    expect(realtime.setTyping).not.toHaveBeenCalledWith(1, 'discussion', 9, true);
  });

  it('restores the root typing indicator when a post fails and the draft survives', () => {
    component.ngOnInit();
    source.create.and.returnValue(throwError(() => new Error('nope')));
    component.newText = 'kept draft';
    component.onComposerInput();
    (realtime.setTyping as jasmine.Spy).calls.reset();

    component.submitRoot();

    // Cleared for the attempt, then restored because the text is still in the box.
    expect(realtime.setTyping).toHaveBeenCalledWith(1, 'discussion', 9, false);
    expect(realtime.setTyping).toHaveBeenCalledWith(1, 'discussion', 9, true);
  });

  it('stops typing when a deletion hides an open nested composer', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();
    const node = component.roots[0];
    node.replyOpen = true;
    node.replyText = 'half written';
    component.handleNodeAction({ type: 'typing', node, active: true });
    (realtime.setTyping as jasmine.Spy).calls.reset();

    const deleted = makeItem({ id: 1, isDeleted: true, content: null, author: null });
    source.matchLiveEvent.and.returnValue({ kind: 'deleted', item: deleted });
    events$.next({ type: 'ReplyDeleted', reply: deleted as never });

    expect(node.replyOpen).toBeFalse();
    expect(node.replyText).toBe('');
    expect(realtime.setTyping).toHaveBeenCalledWith(1, 'discussion', 9, false);
  });

  it('re-enables the composer when the host swaps threads mid-post', () => {
    const pending = new Subject<ThreadDisplayItem | null>();
    source.list.and.returnValue(of(makePage([])));
    component.ngOnInit();
    source.create.and.returnValue(pending);
    component.newText = 'in flight';
    component.submitRoot();
    expect(component.submitting).toBeTrue();

    // teardown cancels the request before its handlers run, so nothing else clears the flag.
    const nextSource = { ...source, threadId: 10 } as ThreadDataSource<ThreadDisplayItem>;
    nextSource.list = jasmine.createSpy('list').and.returnValue(of(makePage([])));
    component.source = nextSource;
    component.ngOnChanges({
      source: { firstChange: false, previousValue: source, currentValue: nextSource } as never,
    });

    expect(component.submitting).toBeFalse();
    expect(component.loading).toBeFalse();
  });

  it('leaves the realtime thread and stops typing on destroy', () => {
    component.ngOnInit();
    component.newText = 'a';
    component.onComposerInput();
    (realtime.setTyping as jasmine.Spy).calls.reset();

    component.ngOnDestroy();

    expect(realtime.setTyping).toHaveBeenCalledWith(1, 'discussion', 9, false);
    expect(leaveThread).toHaveBeenCalled();
  });

  it('rebuilds itself when the host swaps in a different thread', () => {
    source.list.and.returnValue(of(makePage([makeItem({ id: 1 })])));
    component.ngOnInit();
    expect(component.roots.length).toBe(1);

    const nextSource = { ...source, threadId: 10 } as ThreadDataSource<ThreadDisplayItem>;
    nextSource.list = jasmine.createSpy('list').and.returnValue(of(makePage([])));
    component.source = nextSource;

    component.ngOnChanges({
      source: { firstChange: false, previousValue: source, currentValue: nextSource } as never,
    });

    expect(leaveThread).toHaveBeenCalled();
    expect(component.roots.length).toBe(0);
    expect(realtime.joinThread).toHaveBeenCalledWith(1, 'discussion', 10);
  });
});
