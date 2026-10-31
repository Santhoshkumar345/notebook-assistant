import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { errorInterceptor } from '../api/api-error';
import { ChatStore } from './chat-store';
import { ConversationList } from './conversation-list';

const AT = '2026-10-31T09:30:00+00:00';
const item = (id: string, title: string, messages = 2) => ({ id, title, messages, createdAt: AT, updatedAt: AT });
const settle = () => new Promise<void>((resolve) => setTimeout(resolve));

describe('ConversationList', () => {
  let fixture: ComponentFixture<ConversationList>;
  let store: ChatStore;
  let http: HttpTestingController;
  let navigate: ReturnType<typeof vi.spyOn>;

  const root = () => fixture.nativeElement as HTMLElement;
  const text = () => root().textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const button = (label: string) =>
    Array.from(root().querySelectorAll('button')).find((b) => b.textContent?.trim() === label || b.getAttribute('aria-label') === label) as HTMLButtonElement | undefined;

  async function render() {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([errorInterceptor])), provideHttpClientTesting(), provideRouter([])] });
    store = TestBed.inject(ChatStore);
    http = TestBed.inject(HttpTestingController);
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(ConversationList);
    await render();
  });

  afterEach(() => http.verify());

  it('offers a new conversation and says when there are none yet', async () => {
    store.listLoaded.set(true);
    await render();

    expect(root().querySelector('a.new')?.getAttribute('href')).toBe('/chat');
    expect(text()).toContain('No conversations yet.');
  });

  it('does not say "none yet" before the list has loaded', () => {
    expect(text()).not.toContain('No conversations yet.');
  });

  it('lists conversations as links with their title and size, and marks the open one', async () => {
    store.conversations.set([item('a', 'First question'), item('b', 'Second question', 6)]);
    store.activeId.set('b');
    store.listLoaded.set(true);
    await render();

    const links = root().querySelectorAll('li a');
    expect(links.length).toBe(2);
    expect(links[0].getAttribute('href')).toBe('/chat/a');
    expect(links[0].textContent).toContain('First question');
    expect(links[1].textContent).toContain('6 messages');
    expect(links[0].getAttribute('aria-current')).toBeNull();
    expect(links[1].getAttribute('aria-current')).toBe('page');
  });

  it('shows a load failure with a retry', async () => {
    store.listError.set({ kind: 'network', status: 0, message: 'x', requestId: null, retryAfterSeconds: null, fieldErrors: [], name: 'ApiError' });
    await render();

    expect(text()).toContain('Could not load conversations');
    expect(text()).toContain('Cannot reach the API');
    expect(button('Retry')).toBeDefined();
  });

  describe('deleting', () => {
    beforeEach(async () => {
      store.conversations.set([item('a', 'First question'), item('b', 'Second question')]);
      store.activeId.set('a');
      await render();
    });

    it('asks first and deletes nothing if cancelled', async () => {
      button('Delete conversation: First question')!.click();
      await render();
      expect(text()).toContain('Delete this conversation?');

      button('Cancel')!.click();
      await render();

      http.expectNone({ method: 'DELETE', url: '/api/conversations/a' });
      expect(root().querySelectorAll('li').length).toBe(2);
    });

    it('deletes after confirmation and leaves for a new conversation when it was the open one', async () => {
      button('Delete conversation: First question')!.click();
      await render();

      button('Delete')!.click();
      await settle();
      http.expectOne({ method: 'DELETE', url: '/api/conversations/a' }).flush(null, { status: 204, statusText: 'No Content' });
      await settle();
      http.expectOne({ method: 'GET', url: '/api/conversations' }).flush([item('b', 'Second question')]);
      await settle();

      expect(navigate).toHaveBeenCalledWith(['/chat']);
    });

    it('stays where it is when a different conversation is deleted', async () => {
      button('Delete conversation: Second question')!.click();
      await render();

      button('Delete')!.click();
      await settle();
      http.expectOne({ method: 'DELETE', url: '/api/conversations/b' }).flush(null, { status: 204, statusText: 'No Content' });
      await settle();
      http.expectOne({ method: 'GET', url: '/api/conversations' }).flush([item('a', 'First question')]);
      await settle();

      expect(navigate).not.toHaveBeenCalled();
    });
  });
});
