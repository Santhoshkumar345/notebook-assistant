import { HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { ApiError } from './api/api-error';
import { appConfig } from './app.config';
import { HARD_NAVIGATE } from './auth/hard-navigate';

/** The app's own providers, not a chain built for the test: this is where the interceptor order and the CSRF header names are decided. */
describe('appConfig HTTP wiring', () => {
  let http: HttpClient;
  let controller: HttpTestingController;
  let navigate: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    navigate = vi.fn();
    TestBed.configureTestingModule({ providers: [...appConfig.providers, provideHttpClientTesting(), { provide: HARD_NAVIGATE, useValue: navigate }] });
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
    controller.verify();
  });

  it('turns a failure into an ApiError before the session check looks at it, so a 401 loads the sign-in page', async () => {
    const result = firstValueFrom(http.get('/api/documents')).catch((e: unknown) => e);
    controller.expectOne('/api/documents').flush({ title: 'Sign in required.' }, { status: 401, statusText: 'x', headers: { 'X-Request-ID': 'r' } });

    expect(await result).toBeInstanceOf(ApiError);
    expect(navigate).toHaveBeenCalledExactlyOnceWith('/login?expired=1');
  });

  it('copies the XSRF-TOKEN cookie into X-XSRF-TOKEN on unsafe requests, the names the API uses', () => {
    document.cookie = 'XSRF-TOKEN=token-from-the-server; path=/';

    http.post('/api/documents/sync', null).subscribe();

    expect(controller.expectOne('/api/documents/sync').request.headers.get('X-XSRF-TOKEN')).toBe('token-from-the-server');
  });

  it('sends no token on reads', () => {
    document.cookie = 'XSRF-TOKEN=token-from-the-server; path=/';

    http.get('/api/documents').subscribe();

    expect(controller.expectOne('/api/documents').request.headers.has('X-XSRF-TOKEN')).toBe(false);
  });
});
