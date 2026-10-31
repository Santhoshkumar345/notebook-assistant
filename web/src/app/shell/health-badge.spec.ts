import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { errorInterceptor } from '../api/api-error';
import { ChatStore } from '../chat/chat-store';
import { DocumentsStore } from '../documents/documents-store';
import { HEALTH_POLL_MS, HealthBadge } from './health-badge';

describe('HealthBadge', () => {
  let fixture: ComponentFixture<HealthBadge>;
  let http: HttpTestingController;
  const writes = signal(0); // stands in for DocumentsStore.writes
  const asks = signal(0); // stands in for ChatStore.asks
  const label = () => (fixture.nativeElement as HTMLElement).textContent?.replace(/\s+/g, ' ').trim();

  beforeEach(() => {
    vi.useFakeTimers();
    writes.set(0);
    asks.set(0);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),
        { provide: DocumentsStore, useValue: { writes } },
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

  it('says it is checking until the first answer', () => {
    expect(label()).toBe('Checking…');
    vi.advanceTimersByTime(0);
    http.expectOne('/api/health').flush({ status: 'ok', ai: { status: 'ok', documents: 1, chunks: 5 } });
  });

  it('shows how many documents are searchable when everything is up', () => {
    answer({ status: 'ok', ai: { status: 'ok', documents: 3, chunks: 40 } });
    expect(label()).toBe('Ready: 3 documents searchable');
  });

  it('uses the singular for one document', () => {
    answer({ status: 'ok', ai: { status: 'ok', documents: 1, chunks: 4 } });
    expect(label()).toBe('Ready: 1 document searchable');
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
    answer({ status: 'ok', ai: { status: 'ok', documents: 3, chunks: 40 } });

    vi.advanceTimersByTime(HEALTH_POLL_MS - 1);
    http.expectNone('/api/health');
    vi.advanceTimersByTime(1);
    http.expectOne('/api/health').flush('', { status: 504, statusText: 'x' });
    fixture.detectChanges();

    expect(label()).toBe('API unreachable');
  });

  it('checks again straight away after a document changes, without waiting for the timer', () => {
    answer({ status: 'ok', ai: { status: 'ok', documents: 0, chunks: 0 } });
    expect(label()).toBe('Ready: 0 documents searchable');

    writes.set(1);
    fixture.detectChanges(); // lets the effect behind toObservable run
    http.expectOne('/api/health').flush({ status: 'ok', ai: { status: 'ok', documents: 1, chunks: 2 } });
    fixture.detectChanges();

    expect(label()).toBe('Ready: 1 document searchable');
  });

  it('checks again straight away when a question finishes, so a failed one shows the AI service as unavailable', () => {
    answer({ status: 'ok', ai: { status: 'ok', documents: 2, chunks: 9 } });
    expect(label()).toBe('Ready: 2 documents searchable');

    asks.set(1);
    fixture.detectChanges();
    http.expectOne('/api/health').flush({ status: 'degraded', ai: null }, { status: 503, statusText: 'x', headers: { 'X-Request-ID': 'r1' } });
    fixture.detectChanges();

    expect(label()).toBe('AI service unavailable');
  });
});
