import { ComponentFixture, TestBed } from '@angular/core/testing';

import { UsernameSuggestionsComponent } from './username-suggestions.component';

describe('UsernameSuggestionsComponent', () => {
  let fixture: ComponentFixture<UsernameSuggestionsComponent>;
  let component: UsernameSuggestionsComponent;

  const suggestions = [
    { username: 'smartcat23', display: 'SmartCat23' },
    { username: 'braveotter47', display: 'BraveOtter47' },
    { username: 'swiftpine08', display: 'SwiftPine08' },
  ];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [UsernameSuggestionsComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(UsernameSuggestionsComponent);
    component = fixture.componentInstance;
  });

  function buttons(): HTMLButtonElement[] {
    return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'));
  }

  it('renders a chip per suggestion, labelled with the display form', () => {
    fixture.componentRef.setInput('suggestions', suggestions);
    fixture.detectChanges();

    const labels = buttons().map((button) => button.textContent?.trim());
    expect(labels).toContain('SmartCat23');
    expect(labels).toContain('BraveOtter47');
    expect(labels).toContain('SwiftPine08');
  });

  /**
   * The property that lets every host degrade to the pre-suggestion UI for free when a draw comes
   * back short, rather than each of them needing its own guard.
   */
  it('renders nothing at all when there is nothing to suggest', () => {
    fixture.componentRef.setInput('suggestions', []);
    fixture.componentRef.setInput('loading', false);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent?.trim()).toBe('');
    expect(buttons()).toEqual([]);
  });

  it('emits the whole suggestion when a chip is clicked', () => {
    fixture.componentRef.setInput('suggestions', suggestions);
    fixture.detectChanges();
    const picked: unknown[] = [];
    component.pick.subscribe((suggestion) => picked.push(suggestion));

    buttons()[0].click();

    expect(picked).toEqual([suggestions[0]]);
  });

  it('marks the chip matching the current field value as chosen', () => {
    fixture.componentRef.setInput('suggestions', suggestions);
    fixture.componentRef.setInput('selected', 'braveotter47');
    fixture.detectChanges();

    const pressed = buttons()
      .filter((button) => button.getAttribute('aria-pressed') === 'true')
      .map((button) => button.textContent?.trim());
    expect(pressed).toEqual(['BraveOtter47']);
  });

  it('offers a shuffle only once names have arrived', () => {
    fixture.componentRef.setInput('suggestions', suggestions);
    fixture.componentRef.setInput('loading', true);
    fixture.detectChanges();
    expect(buttons().map((b) => b.textContent?.trim())).not.toContain('Shuffle');

    fixture.componentRef.setInput('loading', false);
    fixture.detectChanges();

    const shuffle = buttons().find((button) => button.textContent?.trim() === 'Shuffle');
    expect(shuffle).toBeDefined();
    const shuffled: unknown[] = [];
    component.shuffle.subscribe(() => shuffled.push(true));
    shuffle!.click();
    expect(shuffled.length).toBe(1);
  });

  it('shows a loading note before the first draw returns', () => {
    fixture.componentRef.setInput('suggestions', []);
    fixture.componentRef.setInput('loading', true);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Finding names');
  });
});
