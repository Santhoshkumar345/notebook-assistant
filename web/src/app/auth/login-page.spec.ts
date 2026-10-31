import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';

import { errorInterceptor } from '../api/api-error';
import { HARD_NAVIGATE } from './hard-navigate';
import { LoginPage } from './login-page';

const USER = { id: '3acfb8d4-46b8-4816-845e-8d8f1b36e23d', email: 'person@example.test' };
const FROM_API = { 'X-Request-ID': 'req-9' };
const settle = () => new Promise<void>((resolve) => setTimeout(resolve));

describe('LoginPage', () => {
  let fixture: ComponentFixture<LoginPage>;
  let http: HttpTestingController;
  let navigate: ReturnType<typeof vi.spyOn>;

  const root = () => fixture.nativeElement as HTMLElement;
  const text = () => root().textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const field = (id: string) => root().querySelector(`#${id}`) as HTMLInputElement;
  const submit = () => root().querySelector('button[type=submit]') as HTMLButtonElement;

  async function render() {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function open(query: Record<string, string> = {}) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: HARD_NAVIGATE, useValue: vi.fn() },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    fixture = TestBed.createComponent(LoginPage);
    await render();
  }

  async function fill(email: string, password: string) {
    field('email').value = email;
    field('email').dispatchEvent(new Event('input'));
    field('password').value = password;
    field('password').dispatchEvent(new Event('input'));
    await render();
  }

  async function next(method: string, url: string): Promise<TestRequest> {
    await settle();
    return http.expectOne({ method, url });
  }

  async function signInSucceeds() {
    submit().click();
    (await next('POST', '/api/auth/login')).flush(USER);
    (await next('GET', '/api/auth/me')).flush(USER);
    await settle();
  }

  afterEach(() => http.verify());

  it('asks for an email and a password in the way password managers recognise', async () => {
    await open();

    expect(field('email').type).toBe('email');
    expect(field('email').autocomplete).toBe('username');
    expect(field('password').type).toBe('password');
    expect(field('password').autocomplete).toBe('current-password');
    expect(root().querySelector('label[for=email]')?.textContent).toBe('Email');
    expect(root().querySelector('a[href="/register"]')).not.toBeNull();
  });

  it('keeps Sign in disabled until both fields have something in them', async () => {
    await open();
    expect(submit().disabled).toBe(true);

    await fill('person@example.test', '');
    expect(submit().disabled).toBe(true);
    await fill('person@example.test', 'a long enough password');
    expect(submit().disabled).toBe(false);
  });

  it('signs in and goes to the page the person was heading for', async () => {
    await open({ returnUrl: '/documents' });
    await fill('  person@example.test  ', 'a long enough password');

    submit().click();
    const post = await next('POST', '/api/auth/login');
    expect(post.request.body).toEqual({ email: 'person@example.test', password: 'a long enough password' }); // trimmed email
    post.flush(USER);
    (await next('GET', '/api/auth/me')).flush(USER);
    await settle();

    expect(navigate).toHaveBeenCalledExactlyOnceWith('/documents');
  });

  it('goes to the chat page when there is no returnUrl', async () => {
    await open();
    await fill('person@example.test', 'a long enough password');
    await signInSucceeds();

    expect(navigate).toHaveBeenCalledWith('/chat');
  });

  it.each(['https://evil.example/phish', '//evil.example', 'javascript:alert(1)'])('does not follow a returnUrl that leaves the app (%s)', async (returnUrl) => {
    await open({ returnUrl });
    await fill('person@example.test', 'a long enough password');
    await signInSucceeds();

    expect(navigate).toHaveBeenCalledExactlyOnceWith('/chat');
  });

  it('shows the server wording for wrong details, keeps the email, and empties the password', async () => {
    await open();
    await fill('person@example.test', 'wrong password!!');

    submit().click();
    (await next('POST', '/api/auth/login')).flush({ title: 'Email or password is incorrect.' }, { status: 401, statusText: 'x', headers: FROM_API });
    await settle();
    await render();

    expect(root().querySelector('[role=alert]')?.textContent).toContain('Email or password is incorrect.');
    expect(field('email').value).toBe('person@example.test');
    expect(field('password').value).toBe(''); // a failed attempt is never left in the box
    expect(navigate).not.toHaveBeenCalled();
  });

  it('says how long to wait after too many attempts', async () => {
    await open();
    await fill('person@example.test', 'wrong password!!');

    submit().click();
    (await next('POST', '/api/auth/login')).flush({ title: 'x' }, { status: 429, statusText: 'x', headers: { ...FROM_API, 'Retry-After': '840' } });
    await settle();
    await render();

    expect(text()).toContain('Too many attempts. Try again in about 840 seconds.');
  });

  it('shows what is happening while the request is out, and does not allow a second one', async () => {
    await open();
    await fill('person@example.test', 'a long enough password');

    submit().click();
    await settle();
    await render();

    expect(submit().textContent).toContain('Signing in');
    expect(submit().disabled).toBe(true);
    const post = http.expectOne({ method: 'POST', url: '/api/auth/login' });
    post.flush(USER);
    (await next('GET', '/api/auth/me')).flush(USER);
    await settle();
  });

  it('explains why the person is here when their session ended', async () => {
    await open({ expired: '1' });
    expect(text()).toContain('Your session ended. Sign in again to continue.');
  });

  it('says nothing about an ended session otherwise', async () => {
    await open();
    expect(text()).not.toContain('session ended');
  });
});
