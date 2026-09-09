import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ToggleSwitchComponent } from './toggle-switch.component';

describe('ToggleSwitchComponent', () => {
  let fixture: ComponentFixture<ToggleSwitchComponent>;
  let component: ToggleSwitchComponent;

  function button(): HTMLButtonElement {
    return fixture.nativeElement.querySelector('button');
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ToggleSwitchComponent] }).compileComponents();

    fixture = TestBed.createComponent(ToggleSwitchComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('exposes itself as a switch to assistive technology', () => {
    expect(button().getAttribute('role')).toBe('switch');
    expect(button().getAttribute('aria-checked')).toBe('false');
  });

  it('reflects the checked state', () => {
    component.checked = true;
    fixture.detectChanges();

    expect(button().getAttribute('aria-checked')).toBe('true');
  });

  it('emits the opposite state when clicked', () => {
    const emitted: boolean[] = [];
    component.checkedChange.subscribe((value) => emitted.push(value));

    button().click();
    expect(emitted).toEqual([true]);

    component.checked = true;
    fixture.detectChanges();
    button().click();
    expect(emitted).toEqual([true, false]);
  });

  it('emits nothing while disabled', () => {
    const emitted: boolean[] = [];
    component.checkedChange.subscribe((value) => emitted.push(value));
    component.disabled = true;
    fixture.detectChanges();

    button().click();
    component.toggle();

    expect(emitted).toEqual([]);
    expect(button().disabled).toBeTrue();
  });

  it('wires up its accessible name and description', () => {
    component.label = 'Track events I view';
    component.describedBy = 'tracking-help';
    fixture.detectChanges();

    expect(button().getAttribute('aria-label')).toBe('Track events I view');
    expect(button().getAttribute('aria-describedby')).toBe('tracking-help');
  });

  it('omits the aria attributes when nothing was supplied', () => {
    expect(button().getAttribute('aria-label')).toBeNull();
    expect(button().getAttribute('aria-describedby')).toBeNull();
  });
});
