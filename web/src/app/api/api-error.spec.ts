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
    expect(toApiError(failure(409, { title: 'Not waiting.' }, withId)).kind).toBe('conflict');
    expect(toApiError(failure(401, { title: 'Sign in required.' }, withId)).kind).toBe('unauthorized');
    expect(toApiError(failure(403, { title: 'A valid invite code is required.' }, withId)).kind).toBe('forbidden');
    expect(toApiError(failure(429, { title: 'Too many requests.' }, withId)).kind).toBe('rate-limited');
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

  it('keeps Retry-After for a rate limit too, since it tells the person when to try again', () => {
    expect(toApiError(failure(429, null, { 'X-Request-ID': 'r', 'Retry-After': '840' })).retryAfterSeconds).toBe(840);
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

  it('uses the server wording for a refused sign-in or registration, so it can say exactly what the server decided', () => {
    expect(describeError(new ApiError('unauthorized', 401, 'Email or password is incorrect.', 'r', null, []))).toBe('Email or password is incorrect.');
    expect(describeError(new ApiError('forbidden', 403, 'A valid invite code is required.', 'r', null, []))).toBe('A valid invite code is required.');
  });

  it('says how long to wait after too many attempts, or just that there were too many', () => {
    expect(describeError(make('rate-limited', { retry: 840 }))).toBe('Too many attempts. Try again in about 840 seconds.');
    expect(describeError(make('rate-limited'))).toBe('Too many attempts.');
  });

  it('uses the server wording for a conflict, because 409 means different things on different routes', () => {
    const taken = new ApiError('conflict', 409, 'That email address is already registered.', 'r', null, []);
    const notWaiting = new ApiError('conflict', 409, 'The conversation is not waiting for an answer to a clarifying question.', 'r', null, []);

    expect(describeError(taken)).toBe('That email address is already registered.');
    expect(describeError(notWaiting)).toBe('The conversation is not waiting for an answer to a clarifying question.');
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
