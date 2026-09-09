import { Component, EventEmitter, Input, Output } from '@angular/core';

/**
 * An accessible on/off switch.
 *
 * Rendered as a real button with role="switch" so Space and Enter activate it without any extra
 * key handling, and assistive technology announces the state rather than a styled div.
 *
 * Deliberately presentational: no ControlValueAccessor until a reactive form actually needs one.
 */
@Component({
  selector: 'app-toggle-switch',
  standalone: true,
  templateUrl: './toggle-switch.component.html',
})
export class ToggleSwitchComponent {
  @Input() checked = false;
  @Input() disabled = false;

  /** Accessible name, for when no visible label is wired up via describedBy. */
  @Input() label = '';

  /** Id of the element describing the switch, for the aria-describedby association. */
  @Input() describedBy = '';

  @Output() checkedChange = new EventEmitter<boolean>();

  toggle(): void {
    if (this.disabled) {
      return;
    }

    this.checkedChange.emit(!this.checked);
  }
}
