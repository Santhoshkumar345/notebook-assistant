import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { ApiError, errorInterceptor } from '../api/api-error';
import { HARD_NAVIGATE } from './hard-navigate';
import { sessionExpiredInterceptor } from './session-interceptor';

describe('sessionExpiredInterceptor', () => {
  let http: HttpClient;
  let controller: HttpTestingController;
  let navigate: ReturnType<typeof vi.fn>;

  const fail = (method: 'get' | 'post', url: string, status: number) => {
    const result = firstValueFrom(method === 'get' ? http.get(url) : http.post(url, {})).catch((e: unknown) => e);
    controller.expectOne(url).flush({ title: 'x' }, { status, statusText: 'x', headers: { 'X-Request-ID': 'r' } });
    return result;
  };

  beforeEach(() => {
    navigate = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        // the same order the app uses: the session interceptor first, the error interceptor last, so it sees an ApiError
        provideHttpClient(withInterceptors([sessionExpiredInterceptor, errorInterceptor])),
        provideHttpClientTesting(),
        { provide: HARD_NAVIGATE, useValue: navigate },
      ],
    });
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  it('loads the sign-in page afresh when a call to the API says the session has ended', async () => {
    const error = await fail('get', '/api/documents', 401);

    expect(navigate).toHaveBeenCalledExactlyOnceWith('/login?expired=1');
    expect(error).toBeInstanceOf(ApiError); // and the caller still sees the failure
    expect((error as ApiError).status).toBe(401);
  });

  it.each(['/api/conversations', '/api/ask', '/api/documents/sync'])('does so for %s too', async (url) => {
    await fail('post', url, 401);
    expect(navigate).toHaveBeenCalledWith('/login?expired=1');
  });

  // These are 401s that are an answer, not an expired session: wrong details, or "not signed in" as the page starts up.
  it.each(['/api/auth/login', '/api/auth/me', '/api/auth/register'])('does not for %s', async (url) => {
    await fail('post', url, 401);
    expect(navigate).not.toHaveBeenCalled();
  });

  it.each([400, 403, 404, 409, 429, 500, 503])('does not for HTTP %i', async (status) => {
    await fail('get', '/api/documents', status);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('leaves successful responses alone', async () => {
    const result = firstValueFrom(http.get('/api/documents'));
    controller.expectOne('/api/documents').flush([]);

    expect(await result).toEqual([]);
    expect(navigate).not.toHaveBeenCalled();
  });
});
