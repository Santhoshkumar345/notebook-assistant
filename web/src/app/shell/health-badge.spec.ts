import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { errorInterceptor } from '../api/api-error';
import type { DocumentInfo } from '../api/models';
import { ChatStore } from '../chat/chat-store';
import { DocumentsStore } from '../documents/documents-store';
import { HEALTH_POLL_MS, HealthBadge } from './health-badge';

const doc = (name: string, status: DocumentInfo['status']): DocumentInfo => ({
  name,
  chars: 10,
  chunks: 1,
  status,
  error: null,
  createdAt: '2026-10-31T09:00:00+00:00',
  updatedAt: '2026-10-31T09:00:00+00:00',
});

describe('HealthBadge', () => {
  let fixture: ComponentFixture<HealthBadge>;
  let http: HttpTestingController;
  const writes = signal(0); // stands in for DocumentsStore.writes
  const asks = signal(0); // stands in for ChatStore.asks
  const loaded = signal(false); // and for whether the signed-in person's own document list has been read
  const documents = signal<DocumentInfo[]>([]);
  const label = () => (fixture.nativeElement as HTMLElement).textContent?.replace(/\s+/g, ' ').trim();

  beforeEach(() => {
    vi.useFakeTimers();
    writes.set(0);
    asks.set(0);
    loaded.set(false);
    documents.set([]);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),
        { provide: DocumentsStore, useValue: { writes, loaded, documents } },
        { provide: ChatStore, useValue: { asks } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(HealthBadge);
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
    http.verify();
    vi.useRealTimers();
  });

  /** Lets the poll timer fire and answers the request it makes. */
  function answer(body: string | object, init: { status?: number; headers?: Record<string, string> } = {}) {
    vi.advanceTimersByTime(0);
    http.expectOne('/api/health').flush(body, { status: init.status ?? 200, statusText: 'x', headers: init.headers });
    fixture.detectChanges();
  }

  const healthy = { status: 'ok', ai: { status: 'ok' } };

  it('says it is checking until the first answer', () => {
    expect(label()).toBe('Checking…');
    vi.advanceTimersByTime(0);
    http.expectOne('/api/health').flush(healthy);
  });

  it('says only "Ready" when everything is up and the person has no document list yet (nobody is signed in, or it has not loaded)', () => {
    answer(healthy);
    expect(label()).toBe('Ready');
  });

  it("shows how many of the signed-in person's own documents are searchable, counting only the indexed ones", () => {
    loaded.set(true);
    documents.set([doc('a.md', 'Indexed'), doc('b.md', 'Indexed'), doc('c.md', 'Failed'), doc('d.md', 'Pending')]);
    answer(healthy);

    expect(label()).toBe('Ready: 2 documents searchable');
  });

  it('uses the singular for one document, and says none when there are none', () => {
    loaded.set(true);
    documents.set([doc('a.md', 'Indexed')]);
    answer(healthy);
    expect(label()).toBe('Ready: 1 document searchable');

    documents.set([]);
    fixture.detectChanges();
    expect(label()).toBe('Ready: 0 documents searchable');
  });

  it('follows the document list as it changes, without another health check', () => {
    loaded.set(true);
    answer(healthy);
    expect(label()).toBe('Ready: 0 documents searchable');

    documents.set([doc('a.md', 'Indexed')]);
    fixture.detectChanges();

    expect(label()).toBe('Ready: 1 document searchable');
  });

  it('never takes a count from the health answer, which would span every user', () => {
    answer({ status: 'ok', ai: { status: 'ok', documents: 999, chunks: 9999 } });
    expect(label()).toBe('Ready');
    expect(label()).not.toContain('999');
  });

  it('says the AI service is unavailable when the API answers degraded', () => {
    answer({ status: 'degraded', ai: null }, { status: 503, headers: { 'X-Request-ID': 'r1' } });
    expect(label()).toBe('AI service unavailable');
  });

  it('says the API is unreachable when nothing from it answered', () => {
    answer('', { status: 504 });
    expect(label()).toBe('API unreachable');
  });

  it('checks again every 30 seconds and updates', () => {
    answer(healthy);

    vi.advanceTimersByTime(HEALTH_POLL_MS - 1);
    http.expectNone('/api/health');
    vi.advanceTimersByTime(1);
    http.expectOne('/api/health').flush('', { status: 504, statusText: 'x' });
    fixture.detectChanges();

    expect(label()).toBe('API unreachable');
  });

  it('checks again straight away after a document changes, without waiting for the timer', () => {
    answer(healthy);

    writes.set(1);
    fixture.detectChanges(); // lets the effect behind toObservable run
    http.expectOne('/api/health').flush({ status: 'degraded', ai: null }, { status: 503, statusText: 'x', headers: { 'X-Request-ID': 'r1' } });
    fixture.detectChanges();

    expect(label()).toBe('AI service unavailable');
  });

  it('checks again straight away when a question finishes, so a failed one shows the AI service as unavailable', () => {
    answer(healthy);
    expect(label()).toBe('Ready');

    asks.set(1);
    fixture.detectChanges();
    http.expectOne('/api/health').flush({ status: 'degraded', ai: null }, { status: 503, statusText: 'x', headers: { 'X-Request-ID': 'r1' } });
    fixture.detectChanges();

    expect(label()).toBe('AI service unavailable');
  });
});
