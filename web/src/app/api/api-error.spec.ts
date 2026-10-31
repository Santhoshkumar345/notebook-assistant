import { HttpClient, HttpErrorResponse, HttpHeaders, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { ApiError, describeError, errorInterceptor, toApiError } from './api-error';

function failure(status: number, body: unknown = null, headers: Record<string, string> = {}): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body, headers: new HttpHeaders(headers), url: '/api/x' });
}

describe('toApiError', () => {
  it('reads a validation problem: the server title, the field messages and the request id', () => {
    const error = toApiError(
      failure(400, { title: 'One or more validation errors occurred.', errors: { Name: ['Bad name.'], Content: ['Too long.', 'Empty.'] } }, { 'X-Request-ID': 'abc123' }),
    );

    expect(error.kind).toBe('validation');
    expect(error.message).toBe('One or more validation errors occurred.');
    expect(error.fieldErrors).toEqual(['Bad name.', 'Too long.', 'Empty.']);
    expect(error.requestId).toBe('abc123');
  });

  it('maps the API statuses to kinds', () => {
    const withId = { 'X-Request-ID': 'r1' };
    expect(toApiError(failure(404, { title: 'No such document.' }, withId)).kind).toBe('not-found');
    expect(toApiError(failure(502, null, withId)).kind).toBe('upstream');
    expect(toApiError(failure(503, null, withId)).kind).toBe('busy');
    expect(toApiError(failure(504, null, withId)).kind).toBe('timeout');
    expect(toApiError(failure(500, null, withId)).kind).toBe('unknown');
  });

  it('treats a 5xx without the API request id as "the API was never reached" (what the dev proxy sends)', () => {
    for (const status of [502, 503, 504]) expect(toApiError(failure(status)).kind).toBe('network');
  });

  it('treats status 0 as a network failure', () => {
    expect(toApiError(failure(0)).kind).toBe('network');
  });

  it('keeps Retry-After only for a busy AI service, as a positive number', () => {
    const busy = toApiError(failure(503, null, { 'X-Request-ID': 'r', 'Retry-After': '10' }));
    expect(busy.retryAfterSeconds).toBe(10);
    expect(toApiError(failure(503, null, { 'X-Request-ID': 'r', 'Retry-After': 'soon' })).retryAfterSeconds).toBeNull();
    expect(toApiError(failure(504, null, { 'X-Request-ID': 'r', 'Retry-After': '10' })).retryAfterSeconds).toBeNull();
  });

  it('survives a body that is not an object', () => {
    expect(toApiError(failure(400, '<html>oops</html>')).message).toBe('The request was not accepted.');
  });
});

describe('describeError', () => {
  const make = (kind: ApiError['kind'], extra: Partial<{ retry: number | null; fields: string[] }> = {}) =>
    new ApiError(kind, 0, 'Server title', 'r', extra.retry ?? null, extra.fields ?? []);

  it('says how long to wait when the service is busy', () => {
    expect(describeError(make('busy', { retry: 10 }))).toContain('about 10 seconds');
    expect(describeError(make('busy'))).not.toContain('seconds');
  });

  it('shows validation messages, falling back to the title', () => {
    expect(describeError(make('validation', { fields: ['A.', 'B.'] }))).toBe('A. B.');
    expect(describeError(make('validation'))).toBe('Server title');
  });

  it('tells the person to check the list after a timeout, since the work may have finished', () => {
    expect(describeError(make('timeout'))).toContain('check the list');
  });
});

describe('errorInterceptor', () => {
  it('turns a failed HTTP call into an ApiError', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([errorInterceptor])), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpClient);
    const controller = TestBed.inject(HttpTestingController);

    const result = firstValueFrom(http.get('/api/x')).catch((e: unknown) => e);
    controller.expectOne('/api/x').flush({ title: 'No such document.' }, { status: 404, statusText: 'Not Found', headers: { 'X-Request-ID': 'r9' } });

    const error = await result;
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).kind).toBe('not-found');
    expect((error as ApiError).requestId).toBe('r9');
  });
});
