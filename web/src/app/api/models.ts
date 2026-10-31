import type { components } from './schema';

// Everything here is derived from schema.d.ts, which is generated from the API's OpenAPI document (npm run gen:api).
// If the API changes a field, these types change with it and the build shows every place that has to follow.

type Schemas = components['schemas'];

export type DocumentInfo = Schemas['DocumentInfo'];
export type DocumentIn = Schemas['DocumentIn'];
export type DocumentStatus = Schemas['DocumentStatus'];
export type SyncResult = Schemas['SyncResult'];
export type HealthOut = Schemas['HealthOut'];

export type AnswerStatus = Schemas['AnswerStatus'];
export type AskResponse = Schemas['AskResponse'];
export type SourceOut = Schemas['SourceOut'];
export type MessageOut = Schemas['MessageOut'];
export type ConversationSummary = Schemas['ConversationSummary'];
export type ConversationOut = Schemas['ConversationOut'];

/** standard: always search, then answer. agent: may also ask a clarifying question or summarize a whole document (slower, more model calls). */
export type AskMode = 'pipeline' | 'agent';

/** The request body, with the mode narrowed from the schema's plain string to the two values the API accepts. */
export type AskRequest = Omit<Schemas['AskIn'], 'mode'> & { mode: AskMode };

/** What the app knows about the system: the API and, behind it, the AI service. */
export type Health =
  | { state: 'checking' }
  | { state: 'ok'; documents: number; chunks: number }
  | { state: 'degraded' } // the API answers, the AI service behind it does not
  | { state: 'down' }; // the API itself cannot be reached
