import { Component, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { getApiClientMessage } from '../../../../core/api/models/api-client-error.model';
import { AuthTokenService } from '../../../../core/api/services/auth-token.service';
import { AuthService } from '../../services/auth.service';

/**
 * Confirms an email change from the link we mailed to the new address.
 *
 * Deliberately not signed-in-only: the token is bound to an account and single-use, which is what
 * lets the link work in whatever browser reads that inbox. It also issues no session — confirming
 * revokes every session server-side — so this drops local auth state and sends the user to sign in
 * with their new address.
 */
@Component({
  selector: 'app-verify-email-change',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './verify-email-change.component.html',
  styleUrls: ['./verify-email-change.component.css'],
})
export class VerifyEmailChangeComponent implements OnInit {
  status = signal<'ready' | 'loading' | 'success' | 'error'>('ready');
  message = signal('Confirm this address to finish changing the email on your account.');
  hasToken = false;

  private token: string | null = null;

  constructor(
    private route: ActivatedRoute,
    private auth: AuthService,
    private authToken: AuthTokenService,
    private router: Router,
  ) {}

  ngOnInit(): void {
    this.token = this.route.snapshot.queryParamMap.get('token');
    this.hasToken = !!this.token;

    if (!this.token) {
      this.status.set('error');
      this.message.set('This confirmation link is missing a token.');
    }
  }

  confirm(): void {
    if (!this.token || this.status() === 'loading') return;

    this.status.set('loading');
    this.message.set('Confirming your new email address...');

    this.auth.confirmEmailChange({ token: this.token }).subscribe({
      next: () => {
        // Every session was revoked server-side, so any local token is already dead.
        this.authToken.logoutLocal();
        this.status.set('success');
        this.message.set(
          'Your email address has been changed. Sign in again with your new address.',
        );
        setTimeout(() => void this.router.navigate(['/auth/login']), 2000);
      },
      error: (err) => {
        this.status.set('error');
        this.message.set(
          getApiClientMessage(err, 'This email change could not be confirmed. Please try again.'),
        );
      },
    });
  }
}
