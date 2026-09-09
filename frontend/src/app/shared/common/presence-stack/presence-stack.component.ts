import { Component, Input } from '@angular/core';

import { AvatarComponent } from '../avatar/avatar.component';

export interface PresenceStackUser {
  userId: number;
  name: string | null;
  username: string | null;
  /** The username as its owner wrote it. Render this; link by `username`. */
  usernameDisplay?: string | null;
  avatar: string | null;
}

/** Overlapping avatars for the people currently online, with a "+N" overflow chip. */
@Component({
  selector: 'app-presence-stack',
  standalone: true,
  imports: [AvatarComponent],
  templateUrl: './presence-stack.component.html',
})
export class PresenceStackComponent {
  @Input() users: PresenceStackUser[] = [];
  /** Total online, which may exceed `users.length` because the server caps the roster. */
  @Input() totalOnline = 0;
  @Input() maxVisible = 5;

  get visible(): PresenceStackUser[] {
    return this.users.slice(0, this.maxVisible);
  }

  get overflow(): number {
    return Math.max(0, this.totalOnline - this.visible.length);
  }

  get summary(): string {
    if (this.totalOnline === 0) return 'No one else is here';
    if (this.totalOnline === 1) return '1 person online';
    return `${this.totalOnline} people online`;
  }

  displayName(user: PresenceStackUser): string {
    return user.name ?? user.usernameDisplay ?? user.username ?? `User #${user.userId}`;
  }

  trackByUserId(_index: number, user: PresenceStackUser): number {
    return user.userId;
  }
}
