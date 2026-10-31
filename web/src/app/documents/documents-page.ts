import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';

import { describeError } from '../api/api-error';
import type { DocumentInfo } from '../api/models';
import { checkMarkdownFile } from './file-checks';
import { DocumentsStore } from './documents-store';

type Confirmation = { kind: 'replace'; name: string; content: string } | { kind: 'delete'; name: string };

/** Why a document is Failed, in words. The server sends only the failure kind, never an upstream message. */
const FAILURE_HINTS: Record<string, string> = {
  Unavailable: 'The AI service could not be reached.',
  Busy: 'The AI service was busy.',
  Timeout: 'The AI service took too long.',
  BadGateway: 'The AI service returned an error.',
  ContractMismatch: 'The API and the AI service disagree about the request format.',
  Cancelled: 'The upload was interrupted.',
};

@Component({
  selector: 'app-documents-page',
  imports: [DatePipe, DecimalPipe],
  templateUrl: './documents-page.html',
  styleUrl: './documents-page.css',
})
export class DocumentsPage {
  protected readonly store = inject(DocumentsStore);
  protected readonly describeError = describeError;

  /** A replace or a delete waiting for the person to say yes. */
  protected readonly confirming = signal<Confirmation | null>(null);
  /** Why the chosen file was not uploaded. */
  protected readonly fileProblem = signal<string | null>(null);

  constructor() {
    void this.store.load();
  }

  protected async onFileInput(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.item(0) ?? null;
    input.value = ''; // so choosing the same file again fires another change event
    await this.chooseFile(file);
  }

  /** Checks the file, then uploads it, or asks first if that would replace a document. */
  async chooseFile(file: File | null): Promise<void> {
    this.fileProblem.set(null);
    this.confirming.set(null);
    if (!file) return;

    const early = checkMarkdownFile(file.name, file.size);
    if (early) return this.fileProblem.set(early);

    const content = await file.text();
    const problem = checkMarkdownFile(file.name, file.size, content);
    if (problem) return this.fileProblem.set(problem);

    if (this.store.exists(file.name)) {
      this.confirming.set({ kind: 'replace', name: file.name, content });
    } else {
      await this.store.upload(file.name, content);
    }
  }

  protected askDelete(name: string): void {
    this.confirming.set({ kind: 'delete', name });
  }

  protected cancel(): void {
    this.confirming.set(null);
  }

  protected async confirm(): Promise<void> {
    const pending = this.confirming();
    this.confirming.set(null);
    if (!pending) return;
    if (pending.kind === 'replace') await this.store.upload(pending.name, pending.content);
    else await this.store.remove(pending.name);
  }

  protected failureHint(document: DocumentInfo): string {
    return (document.error && FAILURE_HINTS[document.error]) || 'The last attempt failed.';
  }
}
