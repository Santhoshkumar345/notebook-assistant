import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { errorInterceptor } from '../api/api-error';
import type { DocumentInfo } from '../api/models';
import { DocumentsPage } from './documents-page';

const doc = (name: string, status: DocumentInfo['status'] = 'Indexed', error: string | null = null): DocumentInfo => ({
  name,
  chars: 12345,
  chunks: status === 'Failed' ? null : 4,
  status,
  error,
  createdAt: '2026-10-31T09:00:00+00:00',
  updatedAt: '2026-10-31T09:30:00+00:00',
});

const settle = () => new Promise<void>((resolve) => setTimeout(resolve));

describe('DocumentsPage', () => {
  let fixture: ComponentFixture<DocumentsPage>;
  let http: HttpTestingController;

  const root = () => fixture.nativeElement as HTMLElement;
  const text = () => root().textContent?.replace(/\s+/g, ' ') ?? '';
  const button = (label: string) =>
    Array.from(root().querySelectorAll('button')).find((b) => b.textContent?.trim() === label || b.getAttribute('aria-label') === label) as HTMLButtonElement | undefined;

  async function render() {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function next(method: string, url: string): Promise<TestRequest> {
    await settle();
    return http.expectOne({ method, url });
  }

  /** Creates the page and answers its first list request. Pass null to leave that request unanswered. */
  async function open(documents: DocumentInfo[] | null) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([errorInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(DocumentsPage);
    const request = http.expectOne('/api/documents');
    if (documents) request.flush(documents);
    await render();
    return request;
  }

  afterEach(() => http.verify());

  it('says it is loading until the list arrives', async () => {
    const pending = await open(null);
    expect(text()).toContain('Loading documents');

    pending.flush([]);
    await render();
    expect(text()).not.toContain('Loading documents');
  });

  it('shows an empty state with guidance', async () => {
    await open([]);
    expect(text()).toContain('No documents yet');
    expect(root().querySelector('table')).toBeNull();
  });

  it('lists each document with its status as text, counts and a labelled action per row', async () => {
    await open([doc('a.md'), doc('b.md', 'Failed', 'Unavailable')]);

    const rows = root().querySelectorAll('tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('a.md');
    expect(rows[0].textContent).toContain('Indexed');
    expect(rows[0].textContent).toContain('12,345');
    expect(rows[1].textContent).toContain('Failed');
    expect(rows[1].textContent).toContain('The AI service could not be reached.');
    expect(button('Re-index b.md')).toBeDefined();
    expect(button('Delete a.md')).toBeDefined();
  });

  it('warns that a failed document is not searchable', async () => {
    await open([doc('a.md'), doc('b.md', 'Failed', 'Timeout')]);
    expect(text()).toContain('1 document is not searchable');
  });

  it('shows a load failure with its request id and retries on request', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([errorInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(DocumentsPage);
    http.expectOne('/api/documents').flush({ title: 'x' }, { status: 503, statusText: 'x', headers: { 'X-Request-ID': 'req-77', 'Retry-After': '10' } });
    await render();

    expect(text()).toContain('Could not load documents');
    expect(text()).toContain('about 10 seconds');
    expect(text()).toContain('req-77');

    button('Retry')!.click();
    http.expectOne('/api/documents').flush([doc('a.md')]);
    await render();
    expect(root().querySelectorAll('tbody tr').length).toBe(1);
    expect(text()).not.toContain('Could not load documents');
  });

  describe('uploading', () => {
    it('uploads a new file straight away, using its name and text', async () => {
      await open([]);

      const done = fixture.componentInstance.chooseFile(new File(['# Hello\n\nWorld'], 'hello.md'));
      const post = await next('POST', '/api/documents');
      expect(post.request.body).toEqual({ name: 'hello.md', content: '# Hello\n\nWorld' });
      post.flush(doc('hello.md'), { status: 201, statusText: 'Created' });
      (await next('GET', '/api/documents')).flush([doc('hello.md')]);
      await done;
      await render();

      expect(text()).toContain('Added hello.md');
      expect(root().querySelectorAll('tbody tr').length).toBe(1);
    });

    it('asks before replacing a document that already exists, and replaces on confirmation', async () => {
      await open([doc('hello.md')]);

      await fixture.componentInstance.chooseFile(new File(['# New text'], 'hello.md'));
      await render();
      expect(text()).toContain('hello.md already exists');
      http.expectNone('/api/documents'); // nothing sent yet

      button('Replace')!.click();
      const post = await next('POST', '/api/documents');
      expect(post.request.body.content).toBe('# New text');
      post.flush(doc('hello.md'), { status: 200, statusText: 'OK' });
      (await next('GET', '/api/documents')).flush([doc('hello.md')]);
      await settle();
      await render();
      expect(text()).toContain('Replaced hello.md');
    });

    it('sends nothing when the replacement is cancelled', async () => {
      await open([doc('hello.md')]);
      await fixture.componentInstance.chooseFile(new File(['# New text'], 'hello.md'));
      await render();

      button('Cancel')!.click();
      await render();

      http.expectNone('/api/documents');
      expect(text()).not.toContain('already exists');
    });

    it.each([
      ['notes.txt', '# x', 'not an accepted file name'],
      ['empty.md', '   ', 'is empty'],
    ])('rejects %s in the browser, with a reason, without calling the API', async (name, content, reason) => {
      await open([]);

      await fixture.componentInstance.chooseFile(new File([content], name));
      await render();

      expect(text()).toContain(reason);
      http.expectNone('/api/documents');
    });

    it('shows a failed upload with its request id, and the Failed row the server kept', async () => {
      await open([]);

      const done = fixture.componentInstance.chooseFile(new File(['# Hello'], 'hello.md'));
      (await next('POST', '/api/documents')).flush({ title: 'The AI service is unavailable.' }, { status: 503, statusText: 'x', headers: { 'X-Request-ID': 'req-5' } });
      (await next('GET', '/api/documents')).flush([doc('hello.md', 'Failed', 'Unavailable')]);
      await done;
      await render();

      expect(text()).toContain('The AI service is busy or not reachable');
      expect(text()).toContain('req-5');
      expect(root().querySelector('.badge.failed')?.textContent).toContain('Failed');
    });
  });

  describe('deleting', () => {
    it('asks first, then deletes and refreshes', async () => {
      await open([doc('a.md')]);

      button('Delete a.md')!.click();
      await render();
      expect(text()).toContain('Delete a.md?');
      http.expectNone('/api/documents/a.md');

      button('Delete')!.click();
      (await next('DELETE', '/api/documents/a.md')).flush(null, { status: 204, statusText: 'No Content' });
      (await next('GET', '/api/documents')).flush([]);
      await settle();
      await render();

      expect(text()).toContain('Deleted a.md.');
      expect(text()).toContain('No documents yet');
    });

    it('keeps the document when the delete is cancelled', async () => {
      await open([doc('a.md')]);
      button('Delete a.md')!.click();
      await render();

      button('Cancel')!.click();
      await render();

      http.expectNone('/api/documents/a.md');
      expect(root().querySelectorAll('tbody tr').length).toBe(1);
    });
  });

  it('disables every action while a write is in flight', async () => {
    await open([doc('a.md')]);

    const done = fixture.componentInstance.chooseFile(new File(['# x'], 'new.md'));
    await settle();
    await render();

    expect(text()).toContain('Uploading new.md');
    expect(button('Delete a.md')!.disabled).toBe(true);
    expect(button('Re-index a.md')!.disabled).toBe(true);
    expect(button('Sync index')!.disabled).toBe(true);
    expect((root().querySelector('input[type=file]') as HTMLInputElement).disabled).toBe(true);

    http.expectOne({ method: 'POST', url: '/api/documents' }).flush(doc('new.md'), { status: 201, statusText: 'Created' });
    (await next('GET', '/api/documents')).flush([doc('a.md'), doc('new.md')]);
    await done;
    await render();
    expect(button('Delete a.md')!.disabled).toBe(false);
  });
});
