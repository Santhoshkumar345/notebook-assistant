import { HttpClient, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';

import { ApiError } from './api-error';
import type { AskRequest, AskResponse, ConversationOut, ConversationSummary, DocumentIn, DocumentInfo, Health, HealthOut, SyncResult } from './models';

/** Typed calls to the .NET API. Paths are relative: in development the dev server proxies /api to the API (proxy.conf.json). */
@Injectable({ providedIn: 'root' })
export class ApiClient {
  private readonly http = inject(HttpClient);

  /** Never fails: an unhealthy system is an answer, not an error, so a status indicator can show it. */
  health(): Observable<Health> {
    return this.http.get<HealthOut>('/api/health').pipe(
      map((body): Health => ({ state: 'ok', documents: body.ai?.documents ?? 0, chunks: body.ai?.chunks ?? 0 })),
      catchError((error: unknown) => {
        // 503 with the API's own request id: the API is up and says the AI service is not. Anything else: the API itself is out of reach.
        const degraded = error instanceof ApiError && error.status === 503 && error.requestId !== null;
        return of<Health>({ state: degraded ? 'degraded' : 'down' });
      }),
    );
  }

  documents(): Observable<DocumentInfo[]> {
    return this.http.get<DocumentInfo[]>('/api/documents');
  }

  /** `created` is false when a document of that name was replaced. */
  upload(document: DocumentIn): Observable<{ document: DocumentInfo; created: boolean }> {
    return this.http
      .post<DocumentInfo>('/api/documents', document, { observe: 'response' })
      .pipe(map((response: HttpResponse<DocumentInfo>) => ({ document: response.body as DocumentInfo, created: response.status === 201 })));
  }

  reindex(name: string): Observable<DocumentInfo> {
    return this.http.post<DocumentInfo>(`/api/documents/${encodeURIComponent(name)}/reindex`, null);
  }

  remove(name: string): Observable<void> {
    return this.http.delete<void>(`/api/documents/${encodeURIComponent(name)}`);
  }

  sync(): Observable<SyncResult> {
    return this.http.post<SyncResult>('/api/documents/sync', null);
  }

  /** Can take 5 to 45 seconds on the free model tier. Unsubscribing cancels the request, and the API does not save an exchange it did not finish. */
  ask(request: AskRequest): Observable<AskResponse> {
    return this.http.post<AskResponse>('/api/ask', request);
  }

  conversations(): Observable<ConversationSummary[]> {
    return this.http.get<ConversationSummary[]>('/api/conversations');
  }

  conversation(id: string): Observable<ConversationOut> {
    return this.http.get<ConversationOut>(`/api/conversations/${encodeURIComponent(id)}`);
  }

  deleteConversation(id: string): Observable<void> {
    return this.http.delete<void>(`/api/conversations/${encodeURIComponent(id)}`);
  }
}
