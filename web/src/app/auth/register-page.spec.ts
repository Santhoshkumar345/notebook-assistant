import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { errorInterceptor } from '../api/api-error';
import { HARD_NAVIGATE } from './hard-navigate';
import { RegisterPage } from './register-page';

const USER = { id: '3acfb8d4-46b8-4816-845e-8d8f1b36e23d', email: 'new@example.test' };
const FROM_API = { 'X-Request-ID': 'req-9' };
const settle = () => new Promise<void>((resolve) => setTimeout(resolve));

describe('RegisterPage', () => {
  let fixture: ComponentFixture<RegisterPage>;
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

  async function type(id: string, value: string) {
    field(id).value = value;
    field(id).dispatchEvent(new Event('input'));
    await render();
  }

  async function next(method: string, url: string): Promise<TestRequest> {
    await settle();
    return http.expectOne({ method, url });
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([errorInterceptor])), provideHttpClientTesting(), provideRouter([]), { provide: HARD_NAVIGATE, useValue: vi.fn() }],
    });
    http = TestBed.inject(HttpTestingController);
    navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    fixture = TestBed.createComponent(RegisterPage);
    await render();
  });

  afterEach(() => http.verify());

  it('asks for what a password manager needs to offer a new password, and says the one rule up front', () => {
    expect(field('email').autocomplete).toBe('username');
    expect(field('password').autocomplete).toBe('new-password');
    expect(field('password').getAttribute('minlength')).toBe('12');
    expect(field('password').getAttribute('maxlength')).toBe('128');
    expect(text()).toContain('At least 12 characters');
    expect(text()).toContain('only if you were given one');
  });

  it('does not accuse an empty password, but explains a short one as it is typed', async () => {
    expect(root().querySelector('.help.problem')).toBeNull();

    await type('password', 'too short');

    expect(root().querySelector('.help.problem')?.textContent).toContain('Use at least 12 characters');
    expect(field('password').getAttribute('aria-invalid')).toBe('true');
  });

  it('allows creating the account only with an address and a long enough password', async () => {
    expect(submit().disabled).toBe(true);
    await type('email', 'not an address');
    await type('password', 'a long enough password');
    expect(submit().disabled).toBe(true);

    await type('email', 'new@example.test');
    expect(submit().disabled).toBe(false);

    await type('password', 'too short');
    expect(submit().disabled).toBe(true);
  });

  it('creates the account without an invite code unless one was typed, then starts at the documents page', async () => {
    await type('email', ' new@example.test ');
    await type('password', 'a long enough password');

    submit().click();
    const post = await next('POST', '/api/auth/register');
    expect(post.request.body).toEqual({ email: 'new@example.test', password: 'a long enough password' });
    post.flush(USER, { status: 201, statusText: 'Created' });
    (await next('GET', '/api/auth/me')).flush(USER);
    await settle();

    expect(navigate).toHaveBeenCalledExactlyOnceWith('/documents');
  });

  it('sends the invite code when there is one', async () => {
    await type('email', 'new@example.test');
    await type('password', 'a long enough password');
    await type('invite', 'open-sesame-123');

    submit().click();
    const post = await next('POST', '/api/auth/register');
    expect(post.request.body.inviteCode).toBe('open-sesame-123');
    post.flush(USER, { status: 201, statusText: 'Created' });
    (await next('GET', '/api/auth/me')).flush(USER);
    await settle();
  });

  it.each([
    [409, 'That email address is already registered.'],
    [403, 'A valid invite code is required.'],
  ])('shows the server wording when registration is refused (HTTP %i), and empties the password', async (status, title) => {
    await type('email', 'new@example.test');
    await type('password', 'a long enough password');

    submit().click();
    (await next('POST', '/api/auth/register')).flush({ title }, { status, statusText: 'x', headers: FROM_API });
    await settle();
    await render();

    expect(root().querySelector('[role=alert]')?.textContent).toContain(title);
    expect(field('email').value).toBe('new@example.test');
    expect(field('password').value).toBe('');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('links to the sign-in page', () => {
    expect(root().querySelector('a[href="/login"]')).not.toBeNull();
  });
});
