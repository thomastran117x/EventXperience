import { User } from '../../../../core/stores/user.model';
import { createThreadNode } from '../thread-tree/thread-tree-state';
import { ThreadLabels } from './thread-data-source';
import { ThreadDisplayItem, ThreadDisplayNode, ThreadNodeAction } from './thread-item';
import { ThreadNodeComponent } from './thread-node.component';

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

function makeNode(overrides: Partial<ThreadDisplayItem> = {}): ThreadDisplayNode {
  return createThreadNode<ThreadDisplayItem>({
    id: 1,
    createdAt: '2026-08-15T12:00:00Z',
    updatedAt: '2026-08-15T12:00:00Z',
    userId: 7,
    content: 'Hello',
    author: {
      id: 7,
      name: 'Taylor Rider',
      username: 'taylor',
      usernameDisplay: 'taylor',
      avatar: null,
    },
    isDeleted: false,
    likeCount: 0,
    dislikeCount: 0,
    currentUserReaction: null,
    directReplyCount: 0,
    ...overrides,
  });
}

describe('ThreadNodeComponent', () => {
  let component: ThreadNodeComponent;
  let emitted: ThreadNodeAction[];

  beforeEach(() => {
    component = new ThreadNodeComponent();
    component.node = makeNode();
    component.labels = LABELS;
    component.currentUser = { Id: 7 } as User;

    emitted = [];
    component.action.subscribe((action) => emitted.push(action));
  });

  it('indents up to the depth cap and then stops', () => {
    component.depth = 0;
    expect(component.indentPx).toBe(12);

    component.depth = ThreadNodeComponent.MaxIndentDepth - 1;
    expect(component.indentPx).toBe(12);

    component.depth = ThreadNodeComponent.MaxIndentDepth;
    expect(component.indentPx).toBe(0);
  });

  it('recognizes the viewer own message', () => {
    expect(component.isOwn).toBeTrue();

    component.currentUser = { Id: 8 } as User;
    expect(component.isOwn).toBeFalse();

    component.currentUser = null;
    expect(component.isOwn).toBeFalse();
  });

  it('marks a message as edited only when it changed after posting', () => {
    expect(component.isEdited).toBeFalse();

    component.node.updatedAt = '2026-08-15T12:05:00Z';
    expect(component.isEdited).toBeTrue();

    component.node.isDeleted = true;
    expect(component.isEdited).toBeFalse();
  });

  it('pluralizes the child count', () => {
    component.node.directReplyCount = 1;
    expect(component.childCountLabel).toBe('1 reply');

    component.node.directReplyCount = 4;
    expect(component.childCountLabel).toBe('4 replies');
  });

  it('opens the reply box and clears any stale error', () => {
    component.node.error = 'previous failure';

    component.toggleReply();

    expect(component.node.replyOpen).toBeTrue();
    expect(component.node.error).toBe('');
  });

  it('discards the draft and reports it when the composer is cancelled', () => {
    component.toggleReply();
    component.node.replyText = 'half written';
    emitted.length = 0;

    component.cancelReply();

    expect(component.node.replyOpen).toBeFalse();
    expect(component.node.replyText).toBe('');
    expect(emitted).toEqual([{ type: 'typing', node: component.node, active: false }]);
  });

  it('cancels rather than reopening when toggled closed', () => {
    component.toggleReply();
    component.node.replyText = 'half written';
    emitted.length = 0;

    component.toggleReply();

    expect(component.node.replyOpen).toBeFalse();
    expect(component.node.replyText).toBe('');
    expect(emitted).toEqual([{ type: 'typing', node: component.node, active: false }]);
  });

  it('emits a create only when the reply box has content', () => {
    component.node.replyText = '   ';
    component.submitChild();
    expect(emitted).toEqual([]);

    component.node.replyText = '  Nested  ';
    component.submitChild();
    expect(emitted).toEqual([{ type: 'create', node: component.node, content: 'Nested' }]);
  });

  it('seeds the edit box from the current content', () => {
    component.node.content = 'Original';

    component.startEdit();

    expect(component.node.editOpen).toBeTrue();
    expect(component.node.editText).toBe('Original');
  });

  it('emits an edit only when the edit box has content', () => {
    component.node.editText = '  ';
    component.saveEdit();
    expect(emitted).toEqual([]);

    component.node.editText = ' Edited ';
    component.saveEdit();
    expect(emitted).toEqual([{ type: 'edit', node: component.node, content: 'Edited' }]);
  });

  it('refuses to submit a child while one is already in flight', () => {
    component.node.replyText = 'Nested';
    component.node.busy = true;

    component.submitChild();

    expect(emitted).toEqual([]);
  });

  it('refuses to save an edit while one is already in flight', () => {
    component.node.editText = 'Edited';
    component.node.busy = true;

    component.saveEdit();

    expect(emitted).toEqual([]);
  });

  it('reports nested composer content upward for the typing indicator', () => {
    component.node.replyText = '  ';
    component.onReplyInput();
    expect(emitted).toEqual([{ type: 'typing', node: component.node, active: false }]);

    emitted.length = 0;
    component.node.replyText = 'Drafting';
    component.onReplyInput();
    expect(emitted).toEqual([{ type: 'typing', node: component.node, active: true }]);
  });

  it('discards descendant composers when the subtree is collapsed', () => {
    const grandchild = makeNode({ id: 4 });
    grandchild.replyOpen = true;
    grandchild.replyText = 'deep draft';
    const child = makeNode({ id: 3 });
    child.replyOpen = true;
    child.replyText = 'child draft';
    child.children = [grandchild];
    component.node.children = [child];
    component.node.childrenLoaded = true;
    emitted.length = 0;

    component.toggleCollapsed();

    expect(component.collapsed).toBeTrue();
    expect(child.replyOpen).toBeFalse();
    expect(child.replyText).toBe('');
    expect(grandchild.replyOpen).toBeFalse();
    // Both hidden composers report themselves, so neither heartbeat keeps running.
    expect(emitted).toEqual([
      { type: 'typing', node: child, active: false },
      { type: 'typing', node: grandchild, active: false },
    ]);
  });

  it('leaves descendants alone when expanding again', () => {
    component.node.children = [makeNode({ id: 3 })];
    component.toggleCollapsed();
    emitted.length = 0;

    component.toggleCollapsed();

    expect(component.collapsed).toBeFalse();
    expect(emitted).toEqual([]);
  });

  it('emits reactions upward', () => {
    component.react('Like');
    component.react('Dislike');

    expect(emitted).toEqual([
      { type: 'react', node: component.node, reaction: 'Like' },
      { type: 'react', node: component.node, reaction: 'Dislike' },
    ]);
  });

  it('submits on Enter but not on Shift+Enter', () => {
    const submit = jasmine.createSpy('submit');

    component.onComposerKeydown(new KeyboardEvent('keydown', { key: 'Enter' }), submit);
    expect(submit).toHaveBeenCalledTimes(1);

    component.onComposerKeydown(
      new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true }),
      submit,
    );
    component.onComposerKeydown(new KeyboardEvent('keydown', { key: 'a' }), submit);
    expect(submit).toHaveBeenCalledTimes(1);
  });

  it('groups its own children by author and time', () => {
    component.node.children = [
      makeNode({ id: 2, userId: 7, createdAt: '2026-08-15T12:00:00Z' }),
      makeNode({ id: 3, userId: 7, createdAt: '2026-08-15T12:01:00Z' }),
      makeNode({ id: 4, userId: 8, createdAt: '2026-08-15T12:02:00Z' }),
    ];

    expect(component.isChildGrouped(0)).toBeFalse();
    expect(component.isChildGrouped(1)).toBeTrue();
    expect(component.isChildGrouped(2)).toBeFalse();
  });

  it('shows the deleted label instead of an author name', () => {
    component.node.isDeleted = true;

    expect(component.authorName).toBe('Reply deleted');
  });

  it('formats both the relative and the exact timestamp', () => {
    expect(component.formatDate('2026-08-15T12:00:00Z')).not.toBe('');
    expect(component.exactDate('2026-08-15T12:00:00Z')).not.toBe('');
  });
});
