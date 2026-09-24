import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideHttpClient, withFetch } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    // Coalescing batches the change detection ticks that a burst of events
    // would otherwise trigger one by one.
    provideZoneChangeDetection({ eventCoalescing: true }),

    provideRouter(routes, withComponentInputBinding()),

    // withFetch() puts HttpClient on the fetch API, which is what makes the
    // response headers we rely on (X-Idempotent-Replay) readable consistently.
    provideHttpClient(withFetch()),
  ],
};
