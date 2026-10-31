import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

import { ApiError } from '../api/api-error';
import { HARD_NAVIGATE } from './hard-navigate';

/**
 * A 401 from anything but the sign-in endpoints means the session has ended (it expired, or the account was signed out elsewhere): load
 * the sign-in page afresh, which also drops everything the previous session left in memory. It must sit before the error interceptor in
 * the list, so that it sees the failure after it has become an ApiError.
 */
export const sessionExpiredInterceptor: HttpInterceptorFn = (request, next) => {
  const hardNavigate = inject(HARD_NAVIGATE);
  return next(request).pipe(
    catchError((error: unknown) => {
      if (error instanceof ApiError && error.status === 401 && !request.url.startsWith('/api/auth/')) hardNavigate('/login?expired=1');
      return throwError(() => error);
    }),
  );
};
