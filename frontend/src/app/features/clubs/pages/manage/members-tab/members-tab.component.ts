import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { finalize } from 'rxjs/operators';

import { getApiClientMessage } from '../../../../../core/api/models/api-client-error.model';
import { ClubMember } from '../../../models/club-management.types';
import {
  ClubInvitationLink,
  ClubMemberInvitation,
} from '../../../models/club-member-invitation.types';
import { Club } from '../../../models/club.types';
import { ClubManagementService } from '../../../services/club-management.service';
import { ClubsService } from '../../../services/clubs.service';

@Component({
  selector: 'app-members-tab',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './members-tab.component.html',
})
export class MembersTabComponent implements OnInit {
  private readonly destroyRef = inject(DestroyRef);

  clubId = 0;
  club: Club | null = null;
  members: ClubMember[] = [];
  loading = true;
  error = '';
  success = '';

  page = 1;
  readonly pageSize = 20;
  totalCount = 0;
  readonly skeletons = Array.from({ length: 6 });

  memberSearch = '';
  private readonly searchInput$ = new Subject<string>();

  // Specific (emailed) invitations
  inviteIdentifier = '';
  inviting = false;
  invitations: ClubMemberInvitation[] = [];
  loadingInvitations = true;
  revokingUserId: number | null = null;

  // Shareable invite links
  links: ClubInvitationLink[] = [];
  loadingLinks = true;
  linkExpiresAt = '';
  linkMaxRedemptions: number | null = null;
  creatingLink = false;
  revokingLinkId: number | null = null;
  lastCreatedShareUrl = '';
  copied = false;

  constructor(
    private route: ActivatedRoute,
    private management: ClubManagementService,
    private clubsService: ClubsService,
  ) {}

  ngOnInit(): void {
    this.clubId =
      Number.parseInt(this.route.parent?.snapshot.paramMap.get('clubId') ?? '', 10) || 0;
    if (!this.clubId) {
      this.loading = false;
      this.error = 'A valid club ID is required.';
      return;
    }
    this.loadClub();
    this.loadMembers();
    this.loadInvitations();
    this.loadLinks();

    this.searchInput$
      .pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.page = 1;
        this.loadMembers();
      });
  }

  onMemberSearch(value: string): void {
    this.memberSearch = value;
    this.searchInput$.next(value);
  }

  get totalPages(): number {
    return Math.max(1, Math.ceil(this.totalCount / this.pageSize));
  }

  capacityPercent(): number {
    const max = this.club?.maxMemberCount ?? 0;
    if (max <= 0) return 0;
    return Math.min(100, (this.totalCount / max) * 100);
  }

  displayName(member: ClubMember): string {
    return member.name || member.usernameDisplay || member.username || `User #${member.userId}`;
  }

  initials(member: ClubMember): string {
    const source = member.name || member.username || '';
    return source ? source.slice(0, 2).toUpperCase() : `#${member.userId}`.slice(0, 2);
  }

  goToPage(page: number): void {
    if (page < 1 || page > this.totalPages || page === this.page) return;
    this.page = page;
    this.loadMembers();
  }

  // ---- Specific invitations ------------------------------------------------

  sendInvite(): void {
    const identifier = this.inviteIdentifier.trim();
    if (!identifier) {
      this.error = 'Enter a username or email address.';
      return;
    }

    this.inviting = true;
    this.error = '';
    this.success = '';

    this.management
      .inviteMember(this.clubId, identifier)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.inviting = false)),
      )
      .subscribe({
        next: (response) => {
          const email = response.data?.recipientEmail ?? identifier;
          this.success = `Invitation sent to ${email}.`;
          this.inviteIdentifier = '';
          this.loadInvitations();
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to send invitation.');
        },
      });
  }

  revokeInvitation(invitation: ClubMemberInvitation): void {
    this.revokingUserId = invitation.recipientUserId;
    this.error = '';
    this.success = '';

    this.management
      .revokeMemberInvitation(this.clubId, invitation.recipientUserId)
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

  // ---- Invite links --------------------------------------------------------

  createLink(): void {
    if (!this.linkExpiresAt) {
      this.error = 'Choose when the link should expire.';
      return;
    }

    const expiresAt = new Date(this.linkExpiresAt);
    if (Number.isNaN(expiresAt.getTime()) || expiresAt.getTime() <= Date.now()) {
      this.error = 'The expiry must be a future date and time.';
      return;
    }

    this.creatingLink = true;
    this.error = '';
    this.success = '';
    this.lastCreatedShareUrl = '';
    this.copied = false;

    this.management
      .createMemberInviteLink(this.clubId, expiresAt.toISOString(), this.linkMaxRedemptions)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.creatingLink = false)),
      )
      .subscribe({
        next: (response) => {
          const shareUrl = response.data?.shareUrl ?? '';
          this.lastCreatedShareUrl = shareUrl ? this.toAbsoluteUrl(shareUrl) : '';
          this.success = 'Invite link created. Copy it below — the link is only shown once.';
          this.linkExpiresAt = '';
          this.linkMaxRedemptions = null;
          this.loadLinks();
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to create invite link.');
        },
      });
  }

  revokeLink(link: ClubInvitationLink): void {
    this.revokingLinkId = link.id;
    this.error = '';
    this.success = '';

    this.management
      .revokeMemberInviteLink(this.clubId, link.id)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.revokingLinkId = null)),
      )
      .subscribe({
        next: (response) => {
          const updated = response.data;
          this.links = this.links.map((l) => (l.id === link.id ? (updated ?? l) : l));
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to revoke invite link.');
        },
      });
  }

  copyShareUrl(): void {
    if (!this.lastCreatedShareUrl) return;
    void navigator.clipboard?.writeText(this.lastCreatedShareUrl).then(() => {
      this.copied = true;
      setTimeout(() => (this.copied = false), 2000);
    });
  }

  linkUsage(link: ClubInvitationLink): string {
    return link.maxRedemptions != null
      ? `${link.redemptionCount} / ${link.maxRedemptions} used`
      : `${link.redemptionCount} used · unlimited`;
  }

  private toAbsoluteUrl(relative: string): string {
    if (/^https?:\/\//i.test(relative)) return relative;
    const origin = typeof window !== 'undefined' ? window.location.origin : '';
    return `${origin}${relative}`;
  }

  private loadClub(): void {
    this.clubsService
      .getClub(this.clubId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.club = response.data ?? null;
        },
        error: () => {
          /* capacity denominator is best-effort */
        },
      });
  }

  private loadMembers(): void {
    this.loading = true;
    this.error = '';
    this.management
      .getMembers(this.clubId, this.page, this.pageSize, this.memberSearch)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.loading = false)),
      )
      .subscribe({
        next: (response) => {
          this.members = response.data?.items ?? [];
          this.totalCount = response.data?.totalCount ?? 0;
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to load members.');
        },
      });
  }

  private loadInvitations(): void {
    this.loadingInvitations = true;
    this.management
      .getMemberInvitations(this.clubId)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.loadingInvitations = false)),
      )
      .subscribe({
        next: (response) => {
          this.invitations = response.data ?? [];
        },
        // Non-critical; keep the tab usable if pending invites fail to load.
        error: () => {
          this.invitations = [];
        },
      });
  }

  private loadLinks(): void {
    this.loadingLinks = true;
    this.management
      .getMemberInviteLinks(this.clubId)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.loadingLinks = false)),
      )
      .subscribe({
        next: (response) => {
          this.links = response.data ?? [];
        },
        error: () => {
          this.links = [];
        },
      });
  }

  trackByInvitation(_index: number, invitation: ClubMemberInvitation): number {
    return invitation.recipientUserId;
  }

  trackByLink(_index: number, link: ClubInvitationLink): number {
    return link.id;
  }
}
