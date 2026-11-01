# Web

The Angular app for Notebook Assistant. It talks only to the .NET API in `../api`, never to the Python AI service.
Angular 21, standalone components, signals, zoneless change detection, Vitest.

## Run

Start the AI service and the API first (see the root README), then:

```bash
npm install
npm start
```

Open http://localhost:4200. The dev server proxies `/api` to the .NET API on `http://127.0.0.1:5247` (`proxy.conf.js`; set `API_TARGET` to point it elsewhere),
so the browser sees one origin and no CORS configuration is needed.

```bash
npm test          # unit tests (Vitest, jsdom)
npm run build     # production build, with bundle budgets
```

## The API client is generated, then checked

The API's OpenAPI document is committed as `../api/openapi/v1.json`. The TypeScript types in `src/app/api/schema.d.ts` are generated from it:

```bash
npm run gen:api     # regenerate the types after the API changed
npm run check:api   # regenerate and fail if the committed types differ
```

A .NET test (`OpenApiSnapshotTests`) fails when the running API no longer matches that snapshot, so the types cannot silently go stale.
To accept an intended API change: `UPDATE_OPENAPI=1 dotnet test` in `../api`, then `npm run gen:api`, then commit both files.

## Layout

```
src/app/
  api/         ApiClient (typed HttpClient calls), ApiError + interceptor (every failure becomes one error type), generated schema.d.ts
  auth/        AuthStore (who is signed in, found by asking the server), route guards, session-expiry interceptor, sign-in and registration pages,
               safe-return-url (an open-redirect guard), credential-checks (browser-side rules, tested against the API contract)
  documents/   DocumentsStore (state and the one-write-at-a-time rule), DocumentsPage, file-checks (browser-side rules)
  chat/        ChatStore (conversations, the request in flight, stop and retry, whether a reply answers a clarifying question),
               ChatPage with Composer, MessageView (one rendering per answer status), ConversationList, ThinkingIndicator, question-checks
  shell/       HealthBadge (is the API up, is the AI service behind it up; re-checks after document changes and finished questions)
```

`file-checks.spec.ts`, `question-checks.spec.ts` and `credential-checks.spec.ts` read the OpenAPI snapshot and fail if the browser's file-name pattern, size limit, question length or password length limits differ from the server's.

## Not built yet

Streaming answers, Markdown rendering of answers, multi-file upload, password reset and email verification (the API cannot send email).
