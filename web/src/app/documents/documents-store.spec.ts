import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { errorInterceptor } from '../api/api-error';
import type { DocumentInfo } from '../api/models';
import { DocumentsStore } from './documents-store';

const doc = (name: string, status: DocumentInfo['status'] = 'Indexed', error: string | null = null, chunks = 2): DocumentInfo => ({
  name,
  chars: 100,
  chunks: status === 'Failed' ? null : chunks,
  status,
  error,
  createdAt: '2026-10-31T09:00:00+00:00',
  updatedAt: '2026-10-31T09:00:00+00:00',
});

const settle = () => new Promise<void>((resolve) => setTimeout(resolve));
const FROM_API = { 'X-Request-ID': 'req-1' };

describe('DocumentsStore', () => {
  let store: DocumentsStore;
  let http: HttpTestingController;

  /** Waits for the store to issue the next request (it does so after the previous one settles), then returns it. */
  async function next(method: string, url: string): Promise<TestRequest> {
    await settle();
    return http.expectOne({ method, url });
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([errorInterceptor])), provideHttpClientTesting()] });
    store = TestBed.inject(DocumentsStore);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  describe('load', () => {
    it('fills the list', async () => {
      const done = store.load();
      expect(store.loading()).toBe(true);
      http.expectOne('/api/documents').flush([doc('a.md')]);
      await done;

      expect(store.documents().map((d) => d.name)).toEqual(['a.md']);
      expect(store.loaded()).toBe(true);
      expect(store.loading()).toBe(false);
    });

    it('records the failure and stays not-loaded', async () => {
      const done = store.load();
      http.expectOne('/api/documents').flush({ title: 'The AI service is unavailable.' }, { status: 503, statusText: 'x', headers: { ...FROM_API, 'Retry-After': '10' } });
      await done;

      expect(store.loadError()?.kind).toBe('busy');
      expect(store.loaded()).toBe(false);
    });

    it('keeps the old list when a later refresh fails', async () => {
      const first = store.load();
      http.expectOne('/api/documents').flush([doc('a.md')]);
      await first;

      const second = store.load();
      http.expectOne('/api/documents').error(new ProgressEvent('error'));
      await second;

      expect(store.documents().map((d) => d.name)).toEqual(['a.md']);
      expect(store.loadError()).not.toBeNull();
    });
  });

  describe('upload', () => {
    it('says what it is doing, then reports the result and reads the list again', async () => {
      const done = store.upload('a.md', '# a');
      expect(store.working()).toBe('Uploading a.md');

      (await next('POST', '/api/documents')).flush(doc('a.md', 'Indexed', null, 1), { status: 201, statusText: 'Created' });
      (await next('GET', '/api/documents')).flush([doc('a.md')]);
      await done;

      expect(store.notice()).toBe('Added a.md: 1 chunk indexed.');
      expect(store.working()).toBeNull();
      expect(store.documents().map((d) => d.name)).toEqual(['a.md']);
    });

    it('says "Replaced" when the name already existed', async () => {
      const done = store.upload('a.md', '# a');
      (await next('POST', '/api/documents')).flush(doc('a.md'), { status: 200, statusText: 'OK' });
      (await next('GET', '/api/documents')).flush([doc('a.md')]);
      await done;

      expect(store.notice()).toBe('Replaced a.md: 2 chunks indexed.');
    });

    it('still reads the list after a failure, so the Failed row the server kept is shown', async () => {
      const done = store.upload('a.md', '# a');
      (await next('POST', '/api/documents')).flush({ title: 'The AI service is unavailable.' }, { status: 503, statusText: 'x', headers: FROM_API });
      (await next('GET', '/api/documents')).flush([doc('a.md', 'Failed', 'Unavailable')]);
      await done;

      expect(store.actionError()?.kind).toBe('busy');
      expect(store.actionError()?.requestId).toBe('req-1');
      expect(store.notice()).toBeNull();
      expect(store.documents()[0].status).toBe('Failed');
      expect(store.failedCount()).toBe(1);
      expect(store.working()).toBeNull();
    });

    it('keeps the success notice even if the refresh afterwards fails', async () => {
      const done = store.upload('a.md', '# a');
      (await next('POST', '/api/documents')).flush(doc('a.md'), { status: 201, statusText: 'Created' });
      (await next('GET', '/api/documents')).error(new ProgressEvent('error'));
      await done;

      expect(store.notice()).toContain('Added a.md');
      expect(store.loadError()).not.toBeNull();
      expect(store.working()).toBeNull();
    });
  });

  describe('one write at a time', () => {
    it('ignores a second write while one is in flight', async () => {
      const first = store.upload('a.md', '# a');
      const second = store.upload('b.md', '# b'); // a double click, or a second button

      await second; // returns at once without sending anything
      const post = http.expectOne({ method: 'POST', url: '/api/documents' }); // exactly one: expectOne fails on two
      expect(post.request.body.name).toBe('a.md');

      post.flush(doc('a.md'), { status: 201, statusText: 'Created' });
      (await next('GET', '/api/documents')).flush([doc('a.md')]);
      await first;
    });

    it('accepts a new write once the previous one has finished', async () => {
      const first = store.remove('a.md');
      (await next('DELETE', '/api/documents/a.md')).flush(null, { status: 204, statusText: 'No Content' });
      (await next('GET', '/api/documents')).flush([]);
      await first;

      const second = store.remove('b.md');
      (await next('DELETE', '/api/documents/b.md')).flush(null, { status: 204, statusText: 'No Content' });
      (await next('GET', '/api/documents')).flush([]);
      await second;

      expect(store.notice()).toBe('Deleted b.md.');
    });

    it('clears the previous notice and error when the next write starts', async () => {
      const first = store.remove('a.md');
      (await next('DELETE', '/api/documents/a.md')).flush(null, { status: 204, statusText: 'No Content' });
      (await next('GET', '/api/documents')).flush([]);
      await first;
      expect(store.notice()).not.toBeNull();

      const second = store.reindex('b.md');
      expect(store.notice()).toBeNull();
      (await next('POST', '/api/documents/b.md/reindex')).flush(doc('b.md'));
      (await next('GET', '/api/documents')).flush([doc('b.md')]);
      await second;
    });
  });

  describe('remove, reindex, sync', () => {
    it('refreshes the list when the document was already gone (404)', async () => {
      const done = store.remove('a.md');
      (await next('DELETE', '/api/documents/a.md')).flush({ title: 'No such document.' }, { status: 404, statusText: 'x', headers: FROM_API });
      (await next('GET', '/api/documents')).flush([]);
      await done;

      expect(store.actionError()?.kind).toBe('not-found');
      expect(store.documents()).toEqual([]);
    });

    it('reports a re-index', async () => {
      const done = store.reindex('a.md');
      (await next('POST', '/api/documents/a.md/reindex')).flush(doc('a.md', 'Indexed', null, 3));
      (await next('GET', '/api/documents')).flush([doc('a.md')]);
      await done;

      expect(store.notice()).toBe('Re-indexed a.md: 3 chunks.');
    });

    it('reports a sync, naming what was removed from the index', async () => {
      const done = store.sync();
      (await next('POST', '/api/documents/sync')).flush({ reindexed: 2, removedFromIndex: ['orphan.md', 'old.md'] });
      (await next('GET', '/api/documents')).flush([doc('a.md'), doc('b.md')]);
      await done;

      expect(store.notice()).toBe('Synced: 2 documents re-sent, 2 stray entries removed from the index (orphan.md, old.md).');
    });

    it('reports a sync that removed nothing', async () => {
      const done = store.sync();
      (await next('POST', '/api/documents/sync')).flush({ reindexed: 1, removedFromIndex: [] });
      (await next('GET', '/api/documents')).flush([doc('a.md')]);
      await done;

      expect(store.notice()).toBe('Synced: 1 document re-sent.');
    });
  });
});
