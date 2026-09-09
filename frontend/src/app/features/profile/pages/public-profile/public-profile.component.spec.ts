import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, ParamMap, Router } from '@angular/router';
import { BehaviorSubject, of } from 'rxjs';

import { ProfileService, PublicProfile } from '../../services/profile.service';
import { PublicProfileComponent } from './public-profile.component';

class ActivatedRouteStub {
  private readonly params = new BehaviorSubject<ParamMap>(
    convertToParamMap({ username: 'old-name' }),
  );
  readonly paramMap = this.params.asObservable();

  setUsername(username: string): void {
    this.params.next(convertToParamMap({ username }));
  }
}

describe('PublicProfileComponent', () => {
  let fixture: ComponentFixture<PublicProfileComponent>;
  let route: ActivatedRouteStub;
  let router: jasmine.SpyObj<Router>;
  let profileService: jasmine.SpyObj<ProfileService>;

  beforeEach(async () => {
    route = new ActivatedRouteStub();
    router = jasmine.createSpyObj<Router>('Router', ['navigate']);
    profileService = jasmine.createSpyObj<ProfileService>('ProfileService', ['getPublicProfile']);

    await TestBed.configureTestingModule({
      imports: [PublicProfileComponent],
      providers: [
        { provide: ActivatedRoute, useValue: route },
        { provide: Router, useValue: router },
        { provide: ProfileService, useValue: profileService },
      ],
    }).compileComponents();
  });

  it('replace-navigates an active alias to the canonical username', () => {
    const profile: PublicProfile = {
      Username: 'new-name',
      UsernameDisplay: 'new-name',
      Name: 'Member',
      Avatar: null,
      Usertype: 'Participant',
      CreatedAtUtc: '2026-01-01T00:00:00Z',
    };
    profileService.getPublicProfile.and.returnValue(of(profile));
    fixture = TestBed.createComponent(PublicProfileComponent);

    fixture.detectChanges();

    expect(fixture.componentInstance.profile).toEqual(profile);
    expect(router.navigate).toHaveBeenCalledOnceWith(['/profile', 'new-name'], {
      replaceUrl: true,
    });
  });

  it('replace-navigates case variants to the normalized canonical username', () => {
    profileService.getPublicProfile.and.returnValue(
      of({
        Username: 'new-name',
        UsernameDisplay: 'new-name',
        Name: null,
        Avatar: null,
        Usertype: 'Participant',
        CreatedAtUtc: '2026-01-01T00:00:00Z',
      }),
    );
    route.setUsername(' NEW-NAME ');
    fixture = TestBed.createComponent(PublicProfileComponent);

    fixture.detectChanges();

    expect(router.navigate).toHaveBeenCalledOnceWith(['/profile', 'new-name'], {
      replaceUrl: true,
    });
  });
});
