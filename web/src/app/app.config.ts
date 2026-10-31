import { provideHttpClient, withInterceptors, withXsrfConfiguration } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';

import { errorInterceptor } from './api/api-error';
import { sessionExpiredInterceptor } from './auth/session-interceptor';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(
      // Responses travel back through this list in reverse: the error interceptor is last so it turns the raw failure into an ApiError first,
      // and the session interceptor, which looks at ApiError, sees the result.
      withInterceptors([sessionExpiredInterceptor, errorInterceptor]),
      // The names the API uses: it sets the XSRF-TOKEN cookie, and Angular copies it into X-XSRF-TOKEN on every unsafe same-origin request.
      withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }),
    ),
  ],
};
