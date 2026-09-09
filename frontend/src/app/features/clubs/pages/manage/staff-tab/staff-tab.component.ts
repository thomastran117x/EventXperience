import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { finalize } from 'rxjs/operators';

import { getApiClientMessage } from '../../../../../core/api/models/api-client-error.model';
import { ClubInvitation } from '../../../models/club-invitation.types';
import { ClubStaff, ClubStaffRole } from '../../../models/club-management.types';
import { ClubManagementService } from '../../../services/club-management.service';

@Component({
  selector: 'app-staff-tab',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './staff-tab.component.html',
})
export class StaffTabComponent implements OnInit {
  private readonly destroyRef = inject(DestroyRef);

  clubId = 0;
  staff: ClubStaff[] = [];
  loading = true;
  error = '';
  success = '';

  // Server-side search (debounced)
  staffSearch = '';
  private readonly searchInput$ = new Subject<string>();

  // Invite staff form
  addIdentifier = '';
  addRole: ClubStaffRole = 'Manager';
  adding = false;

  // Pending invitations
  invitations: ClubInvitation[] = [];
  loadingInvitations = true;
  revokingUserId: number | null = null;

  // Remove
  removingUserId: number | null = null;

  constructor(
    private route: ActivatedRoute,
    private management: ClubManagementService,
  ) {}

  ngOnInit(): void {
    this.clubId =
      Number.parseInt(this.route.parent?.snapshot.paramMap.get('clubId') ?? '', 10) || 0;
    if (!this.clubId) {
      this.loading = false;
      this.error = 'A valid club ID is required.';
      return;
    }
    this.load();
    this.loadInvitations();

    this.searchInput$
      .pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.load());
  }

  onStaffSearch(value: string): void {
    this.staffSearch = value;
    this.searchInput$.next(value);
  }

  private load(): void {
    this.loading = true;
    this.error = '';
    this.management
      .getStaff(this.clubId, this.staffSearch)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.loading = false)),
      )
      .subscribe({
        next: (response) => {
          this.staff = response.data ?? [];
        },
        error: (err) => {
          this.error = getApiClientMessage(
            err,
            'Unable to load staff. Only the owner can manage staff.',
          );
        },
      });
  }

  private loadInvitations(): void {
    this.loadingInvitations = true;
    this.management
      .getStaffInvitations(this.clubId)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.loadingInvitations = false)),
      )
      .subscribe({
        next: (response) => {
          this.invitations = response.data ?? [];
        },
        // Pending invitations are non-critical; keep the tab usable if they fail to load.
        error: () => {
          this.invitations = [];
        },
      });
  }

  sendInvite(): void {
    const identifier = this.addIdentifier.trim();
    if (!identifier) {
      this.error = 'Enter a username or email address.';
      return;
    }

    this.adding = true;
    this.error = '';
    this.success = '';

    this.management
      .inviteStaff(this.clubId, identifier, this.addRole)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.adding = false)),
      )
      .subscribe({
        next: (response) => {
          const email = response.data?.recipientEmail ?? identifier;
          this.success = `Invitation sent to ${email} as ${this.addRole.toLowerCase()}.`;
          this.addIdentifier = '';
          this.loadInvitations();
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to send invitation.');
        },
      });
  }

  revokeInvitation(invitation: ClubInvitation): void {
    this.revokingUserId = invitation.recipientUserId;
    this.error = '';
    this.success = '';

    this.management
      .revokeStaffInvitation(this.clubId, invitation.recipientUserId)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.revokingUserId = null)),
      )
      .subscribe({
        next: () => {
          this.invitations = this.invitations.filter(
            (i) => i.recipientUserId !== invitation.recipientUserId,
          );
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to revoke invitation.');
        },
      });
  }

  removeStaff(member: ClubStaff): void {
    this.removingUserId = member.userId;
    this.error = '';
    this.success = '';

    this.management
      .removeStaff(this.clubId, member.userId)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.removingUserId = null)),
      )
      .subscribe({
        next: () => {
          this.success = `User #${member.userId} removed.`;
          this.staff = this.staff.filter((s) => s.userId !== member.userId);
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to remove staff member.');
        },
      });
  }

  displayName(member: ClubStaff): string {
    return member.name || member.usernameDisplay || member.username || `User #${member.userId}`;
  }

  initials(member: ClubStaff): string {
    const source = member.name || member.username || '';
    return source ? source.slice(0, 2).toUpperCase() : `#${member.userId}`.slice(0, 2);
  }

  trackByUserId(_index: number, member: ClubStaff): number {
    return member.userId;
  }

  trackByInvitation(_index: number, invitation: ClubInvitation): number {
    return invitation.recipientUserId;
  }
}
