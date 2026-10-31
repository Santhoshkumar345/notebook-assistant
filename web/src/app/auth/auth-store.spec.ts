import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { errorInterceptor } from '../api/api-error';
import { AuthStore } from './auth-store';
import { HARD_NAVIGATE } from './hard-navigate';

const USER = { id: '3acfb8d4-46b8-4816-845e-8d8f1b36e23d', email: 'person@example.test' };
const FROM_API = { 'X-Request-ID': 'req-1' };
const settle = () => new Promise<void>((resolve) => setTimeout(resolve));

describe('AuthStore', () => {
  let store: AuthStore;
  let http: HttpTestingController;
  let navigate: ReturnType<typeof vi.fn>;

  async function next(method: string, url: string): Promise<TestRequest> {
    await settle();
    return http.expectOne({ method, url });
  }

  beforeEach(() => {
    navigate = vi.fn();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([errorInterceptor])), provideHttpClientTesting(), { provide: HARD_NAVIGATE, useValue: navigate }],
    });
    store = TestBed.inject(AuthStore);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  describe('finding out who is signed in', () => {
    it('is unknown until the server has answered', () => {
      expect(store.user()).toBeUndefined();
      expect(store.signedIn()).toBe(false);
    });

    it('asks once however many callers want to know, and shares the answer', async () => {
      const first = store.init();
      const second = store.init();
      const third = store.init();
      http.expectOne('/api/auth/me').flush(USER); // exactly one request
      await Promise.all([first, second, third]);

      expect(store.user()).toEqual(USER);
      expect(store.signedIn()).toBe(true);
    });

    it('is signed out when the server says so', async () => {
      const done = store.init();
      http.expectOne('/api/auth/me').flush({ title: 'Sign in required.' }, { status: 401, statusText: 'x', headers: FROM_API });
      await done;

      expect(store.user()).toBeNull();
      expect(store.signedIn()).toBe(false);
    });

    it('is treated as signed out when the API cannot be reached, so the person lands where they can see the problem', async () => {
      const done = store.init();
      http.expectOne('/api/auth/me').error(new ProgressEvent('error'));
      await done;

      expect(store.user()).toBeNull();
    });
  });

  describe('signing in', () => {
    it('sends the details, then asks who is signed in so the page receives its new CSRF token', async () => {
      const done = store.login('person@example.test', 'a long enough password');
      expect(store.working()).toBe(true);

      const post = await next('POST', '/api/auth/login');
      expect(post.request.body).toEqual({ email: 'person@example.test', password: 'a long enough password' });
      post.flush(USER);
      (await next('GET', '/api/auth/me')).flush(USER);

      expect(await done).toBe(true);
      expect(store.user()).toEqual(USER);
      expect(store.working()).toBe(false);
      expect(store.error()).toBeNull();
    });

    it('shows the server wording for wrong details and signs nobody in', async () => {
      const done = store.login('person@example.test', 'wrong password!!');
      (await next('POST', '/api/auth/login')).flush({ title: 'Email or password is incorrect.' }, { status: 401, statusText: 'x', headers: FROM_API });

      expect(await done).toBe(false);
      expect(store.error()?.kind).toBe('unauthorized');
      expect(store.error()?.message).toBe('Email or password is incorrect.');
      expect(store.signedIn()).toBe(false);
      expect(store.working()).toBe(false);
    });

    it('reports a lock-out with how long to wait', async () => {
      const done = store.login('person@example.test', 'wrong password!!');
      (await next('POST', '/api/auth/login')).flush({ title: 'Too many failed attempts. Try again later.' }, { status: 429, statusText: 'x', headers: { ...FROM_API, 'Retry-After': '840' } });

      expect(await done).toBe(false);
      expect(store.error()?.kind).toBe('rate-limited');
      expect(store.error()?.retryAfterSeconds).toBe(840);
    });

    it('ignores a second attempt while one is in flight', async () => {
      const first = store.login('person@example.test', 'a long enough password');
      expect(await store.login('person@example.test', 'a long enough password')).toBe(false);

      const post = http.expectOne({ method: 'POST', url: '/api/auth/login' }); // exactly one
      post.flush(USER);
      (await next('GET', '/api/auth/me')).flush(USER);
      await first;
    });

    it('clears an earlier error when a new attempt starts, and on request', async () => {
      const failed = store.login('person@example.test', 'wrong password!!');
      (await next('POST', '/api/auth/login')).flush({ title: 'x' }, { status: 401, statusText: 'x', headers: FROM_API });
      await failed;
      expect(store.error()).not.toBeNull();

      store.clearError();
      expect(store.error()).toBeNull();
    });
  });

  describe('registering', () => {
    it('leaves the invite code out when none was typed, and trims one that was', async () => {
      const without = store.register('person@example.test', 'a long enough password', '   ');
      const post = await next('POST', '/api/auth/register');
      expect(post.request.body).toEqual({ email: 'person@example.test', password: 'a long enough password' });
      post.flush(USER, { status: 201, statusText: 'Created' });
      (await next('GET', '/api/auth/me')).flush(USER);
      await without;

      const withCode = store.register('other@example.test', 'a long enough password', '  open-sesame-123 ');
      const second = await next('POST', '/api/auth/register');
      expect(second.request.body).toEqual({ email: 'other@example.test', password: 'a long enough password', inviteCode: 'open-sesame-123' });
      second.flush(USER, { status: 201, statusText: 'Created' });
      (await next('GET', '/api/auth/me')).flush(USER);
      await withCode;
    });

    it.each([
      [409, 'That email address is already registered.', 'conflict'],
      [403, 'A valid invite code is required.', 'forbidden'],
    ])('shows the server wording for a refused registration (HTTP %i)', async (status, title, kind) => {
      const done = store.register('person@example.test', 'a long enough password', '');
      (await next('POST', '/api/auth/register')).flush({ title }, { status, statusText: 'x', headers: FROM_API });

      expect(await done).toBe(false);
      expect(store.error()?.kind).toBe(kind);
      expect(store.error()?.message).toBe(title);
      expect(store.signedIn()).toBe(false);
    });

    it('shows each validation message the server gives for a password it did not accept', async () => {
      const done = store.register('person@example.test', 'short', '');
      (await next('POST', '/api/auth/register')).flush(
        { title: 'One or more validation errors occurred.', errors: { Password: ['Passwords must be at least 12 characters.'] } },
        { status: 400, statusText: 'x', headers: FROM_API },
      );

      expect(await done).toBe(false);
      expect(store.error()?.fieldErrors).toEqual(['Passwords must be at least 12 characters.']);
    });
  });

  describe('signing out', () => {
    it('tells the server, then loads the sign-in page afresh so nothing of the session stays in memory', async () => {
      const done = store.logout();
      (await next('POST', '/api/auth/logout')).flush(null, { status: 204, statusText: 'No Content' });
      await done;

      expect(navigate).toHaveBeenCalledExactlyOnceWith('/login');
    });

    it('still leaves the page when the server could not be told', async () => {
      const done = store.logout();
      (await next('POST', '/api/auth/logout')).error(new ProgressEvent('error'));
      await done;

      expect(navigate).toHaveBeenCalledExactlyOnceWith('/login');
    });
  });
});
