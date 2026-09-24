import { bootstrapApplication } from '@angular/platform-browser';

import { AppComponent } from './app/app.component';
import { appConfig } from './app/app.config';

bootstrapApplication(AppComponent, appConfig).catch((error: unknown) =>
  // Nothing has rendered at this point, so the console is the only place left
  // to say what went wrong.
  console.error('Failed to bootstrap the application', error),
);
