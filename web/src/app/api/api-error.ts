import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';

export type ApiErrorKind =
  | 'network' // nothing from the API answered (it is down, or the dev proxy could not reach it)
  | 'validation' // 400: the request broke a rule
  | 'not-found' // 404
  | 'busy' // 503: the AI service is busy or unreachable
  | 'timeout' // 504: the AI service took too long
  | 'upstream' // 502: the AI service answered with something unusable
  | 'unknown';

/** The API's request id header. Every response from the API carries it; a gateway or proxy that never reached the API does not. */
export const REQUEST_ID_HEADER = 'X-Request-ID';

/**
 * A failed call, reduced to what the screens need. Built from the API's RFC 7807 error bodies, so the title is the server's own
 * wording; the request id lets a screenshot of an error be matched to the log lines of both services.
 */
export class ApiError extends Error {
  constructor(
    readonly kind: ApiErrorKind,
    readonly status: number,
    message: string,
    readonly requestId: string | null,
    readonly retryAfterSeconds: number | null,
    readonly fieldErrors: readonly string[],
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

interface ProblemBody {
  title?: unknown;
  errors?: unknown;
}

export function toApiError(response: HttpErrorResponse): ApiError {
  const requestId = response.headers?.get(REQUEST_ID_HEADER) ?? null;
  const body: ProblemBody = typeof response.error === 'object' && response.error !== null ? response.error : {};
  const title = typeof body.title === 'string' ? body.title : '';
  const fieldErrors = collectFieldErrors(body.errors);
  const retryAfter = Number(response.headers?.get('Retry-After'));
  const retryAfterSeconds = Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter : null;

  const make = (kind: ApiErrorKind, fallback: string) =>
    new ApiError(kind, response.status, title || fallback, requestId, kind === 'busy' ? retryAfterSeconds : null, fieldErrors);

  // The API stamps its request id on every response, errors included. A 5xx without one came from something in front of it
  // (the dev proxy, a gateway) that could not reach the API at all.
  if (response.status === 0 || (response.status >= 502 && response.status <= 504 && requestId === null)) {
    return make('network', 'The API cannot be reached.');
  }

  switch (response.status) {
    case 400:
      return make('validation', 'The request was not accepted.');
    case 404:
      return make('not-found', 'Not found.');
    case 502:
      return make('upstream', 'The AI service returned something unusable.');
    case 503:
      return make('busy', 'The AI service is busy or unavailable.');
    case 504:
      return make('timeout', 'The AI service took too long to answer.');
    default:
      return make('unknown', `Unexpected error (HTTP ${response.status}).`);
  }
}

/** Turns every failed HTTP call into an <see cref="ApiError"/>, so no screen has to understand HttpErrorResponse. */
export const errorInterceptor: HttpInterceptorFn = (request, next) =>
  next(request).pipe(
    catchError((error: unknown) => throwError(() => (error instanceof HttpErrorResponse ? toApiError(error) : error))),
  );

/** One sentence for a person. The request id is shown separately so it can be copied. */
export function describeError(error: ApiError): string {
  switch (error.kind) {
    case 'network':
      return 'Cannot reach the API. Check that it is running, then try again.';
    case 'validation':
      return error.fieldErrors.length > 0 ? error.fieldErrors.join(' ') : error.message;
    case 'not-found':
      return 'That item no longer exists. The list has been refreshed.';
    case 'busy': {
      const wait = error.retryAfterSeconds === null ? '' : ` Try again in about ${error.retryAfterSeconds} seconds.`;
      return `The AI service is busy or not reachable.${wait}`;
    }
    case 'timeout':
      return 'The AI service took too long to answer. The request may still have finished: check the list.';
    case 'upstream':
      return 'The AI service returned something the API could not use.';
    default:
      return error.message;
  }
}

function collectFieldErrors(errors: unknown): string[] {
  if (typeof errors !== 'object' || errors === null) return [];
  return Object.values(errors as Record<string, unknown>)
    .flatMap((messages) => (Array.isArray(messages) ? messages : []))
    .filter((message): message is string => typeof message === 'string');
}
