import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { ApiClient } from './api-client';
import { errorInterceptor } from './api-error';
import type { DocumentInfo } from './models';

const DOCUMENT: DocumentInfo = {
  name: 'my notes.md',
  chars: 12,
  chunks: 2,
  status: 'Indexed',
  error: null,
  createdAt: '2026-10-31T09:00:00+00:00',
  updatedAt: '2026-10-31T09:00:00+00:00',
};

describe('ApiClient', () => {
  let client: ApiClient;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([errorInterceptor])), provideHttpClientTesting()] });
    client = TestBed.inject(ApiClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  describe('health', () => {
    it('reports ok with the counts', async () => {
      const result = firstValueFrom(client.health());
      http.expectOne('/api/health').flush({ status: 'ok', ai: { status: 'ok', documents: 3, chunks: 40 } });
      expect(await result).toEqual({ state: 'ok', documents: 3, chunks: 40 });
    });

    it('reports degraded when the API answers 503 with its own request id', async () => {
      const result = firstValueFrom(client.health());
      http.expectOne('/api/health').flush({ status: 'degraded', ai: null }, { status: 503, statusText: 'Service Unavailable', headers: { 'X-Request-ID': 'r1' } });
      expect(await result).toEqual({ state: 'degraded' });
    });

    it('reports down when nothing from the API answered', async () => {
      const result = firstValueFrom(client.health());
      http.expectOne('/api/health').flush('', { status: 504, statusText: 'Gateway Timeout' }); // the dev proxy, no request id
      expect(await result).toEqual({ state: 'down' });
    });

    it('reports down on a network error', async () => {
      const result = firstValueFrom(client.health());
      http.expectOne('/api/health').error(new ProgressEvent('error'));
      expect(await result).toEqual({ state: 'down' });
    });
  });

  describe('documents', () => {
    it('lists', async () => {
      const result = firstValueFrom(client.documents());
      http.expectOne({ method: 'GET', url: '/api/documents' }).flush([DOCUMENT]);
      expect(await result).toEqual([DOCUMENT]);
    });

    it('uploads the name and content, and says whether it was created or replaced', async () => {
      const created = firstValueFrom(client.upload({ name: 'a.md', content: '# a' }));
      const first = http.expectOne({ method: 'POST', url: '/api/documents' });
      expect(first.request.body).toEqual({ name: 'a.md', content: '# a' });
      first.flush(DOCUMENT, { status: 201, statusText: 'Created' });
      expect((await created).created).toBe(true);

      const replaced = firstValueFrom(client.upload({ name: 'a.md', content: '# a2' }));
      http.expectOne('/api/documents').flush(DOCUMENT, { status: 200, statusText: 'OK' });
      expect((await replaced).created).toBe(false);
    });

    it('encodes the name in the path for reindex and delete', async () => {
      const reindex = firstValueFrom(client.reindex('my notes.md'));
      http.expectOne({ method: 'POST', url: '/api/documents/my%20notes.md/reindex' }).flush(DOCUMENT);
      await reindex;

      const removal = firstValueFrom(client.remove('my notes.md'));
      http.expectOne({ method: 'DELETE', url: '/api/documents/my%20notes.md' }).flush(null, { status: 204, statusText: 'No Content' });
      await removal;
    });

    it('syncs', async () => {
      const result = firstValueFrom(client.sync());
      http.expectOne({ method: 'POST', url: '/api/documents/sync' }).flush({ reindexed: 2, removedFromIndex: ['x.md'] });
      expect(await result).toEqual({ reindexed: 2, removedFromIndex: ['x.md'] });
    });
  });
});
