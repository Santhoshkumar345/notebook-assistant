import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiClient } from '../api/api-client';
import type { ApiError } from '../api/api-error';
import { asApiError } from '../api/api-error';
import type { DocumentInfo } from '../api/models';

/**
 * The documents screen's state. One write runs at a time (the API serialises document writes anyway, and a double click should not
 * queue a second upload). After every write, successful or not, the list is read again: a failed upload still leaves a row on the
 * server (status Failed), and the screen has to show it.
 */
@Injectable({ providedIn: 'root' })
export class DocumentsStore {
  private readonly api = inject(ApiClient);

  readonly documents = signal<DocumentInfo[]>([]);
  readonly loaded = signal(false);
  readonly loading = signal(false);
  readonly loadError = signal<ApiError | null>(null);

  /** What the single write in flight is doing ("Uploading notes.md"), or null. */
  readonly working = signal<string | null>(null);
  readonly actionError = signal<ApiError | null>(null);
  readonly notice = signal<string | null>(null);

  private readonly writeCount = signal(0);
  /** Goes up after every write finishes (success or not), so other parts of the screen, such as the header's searchable-document count, can refresh. */
  readonly writes = this.writeCount.asReadonly();

  readonly failedCount = computed(() => this.documents().filter((d) => d.status === 'Failed').length);

  exists(name: string): boolean {
    return this.documents().some((d) => d.name === name);
  }

  async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.documents.set(await firstValueFrom(this.api.documents()));
      this.loadError.set(null);
      this.loaded.set(true);
    } catch (error) {
      this.loadError.set(asApiError(error));
    } finally {
      this.loading.set(false);
    }
  }

  async upload(name: string, content: string): Promise<void> {
    await this.write(`Uploading ${name}`, async () => {
      const { document, created } = await firstValueFrom(this.api.upload({ name, content }));
      this.notice.set(`${created ? 'Added' : 'Replaced'} ${document.name}: ${plural(document.chunks ?? 0, 'chunk')} indexed.`);
    });
  }

  async reindex(name: string): Promise<void> {
    await this.write(`Re-indexing ${name}`, async () => {
      const document = await firstValueFrom(this.api.reindex(name));
      this.notice.set(`Re-indexed ${document.name}: ${plural(document.chunks ?? 0, 'chunk')}.`);
    });
  }

  async remove(name: string): Promise<void> {
    await this.write(`Deleting ${name}`, async () => {
      await firstValueFrom(this.api.remove(name));
      this.notice.set(`Deleted ${name}.`);
    });
  }

  async sync(): Promise<void> {
    await this.write('Syncing the search index', async () => {
      const result = await firstValueFrom(this.api.sync());
      const removed = result.removedFromIndex.length;
      this.notice.set(
        `Synced: ${plural(result.reindexed, 'document')} re-sent` +
          (removed > 0 ? `, ${plural(removed, 'stray entry', 'stray entries')} removed from the index (${result.removedFromIndex.join(', ')}).` : '.'),
      );
    });
  }

  dismissNotice(): void {
    this.notice.set(null);
  }

  dismissActionError(): void {
    this.actionError.set(null);
  }

  private async write(description: string, action: () => Promise<void>): Promise<void> {
    if (this.working() !== null) return; // already busy: ignore, the buttons are disabled too

    this.working.set(description);
    this.notice.set(null);
    this.actionError.set(null);
    try {
      await action();
    } catch (error) {
      this.actionError.set(asApiError(error));
    } finally {
      await this.load(); // success or failure: the server's list is the truth
      this.working.set(null);
      this.writeCount.update((n) => n + 1);
    }
  }
}

function plural(count: number, one: string, many = `${one}s`): string {
  return `${count} ${count === 1 ? one : many}`;
}
