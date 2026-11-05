# End-to-end tests

Browser tests that drive the real application: the Angular app, the .NET API (with a throwaway database), the Python AI service (with real
embeddings and real retrieval). Playwright starts all three on ports of their own (4210, 5257, 8010), so it never touches a development stack, and
tears them down afterwards.

**The one thing that is not real is the language model.** `ai-service/e2e_server.py` replaces the two places that would call Gemini with a deterministic
stand-in that cites the top retrieved passage and echoes the question it was given (`[asked: ...]`). That makes the tests free, fast and repeatable, and
lets a test prove what actually reached the AI service: which person's document was retrieved, and what text was sent after a clarifying question. How
good real answers are is measured by the evaluation harnesses in `ai-service/evals`, not here.

## Run

```bash
cd e2e
npm install
npm test            # 41 tests, about two minutes (the first run also builds the .NET API)
npm run test:production   # 52 tests against the production build, see below
```

- It uses a browser already on the machine: Edge by default, `E2E_BROWSER=chrome` for Chrome. Nothing is downloaded.
- It needs the Python environment from the root README (`ai-service/.venv`), the .NET SDK and Node. It does **not** need a Gemini key or the secrets from
  `scripts/setup.ps1`: it uses its own throwaway service key for its own throwaway stack.
- Every test registers its own people with random emails and passwords, so tests are independent and run in parallel. Tracing is off, because a trace
  would record what was typed into the password fields.

## As deployed

`npm run test:production` builds the Angular app for production and runs the same tests against it as it would be deployed (`scripts/run.mjs`):

- the API **serves the built app itself** (`ASPNETCORE_WEBROOT`), so there is no Angular dev server and one origin;
- the API runs **outside Development**, so Secure cookies, the refusal of plain http and the startup checks are all live;
- a small **proxy** (`scripts/proxy.mjs`) stands in for a host's edge: it overwrites `X-Forwarded-Proto` and `X-Forwarded-For` as a real one does, passes a browser
  that walks away on to the API as a cancelled request, and the API believes only that proxy's network (`ForwardedHeaders:Mode=TrustedProxies`).

`production.spec.ts` adds the tests only this shape can run (they are skipped in `npm test`): the page's security headers, no content-security-policy violation during a whole
session, a reload on a deep link, content-hashed files cached for good, a missing file and an unknown API address answered honestly, Secure cookies, and that a visitor
cannot name their own address. Not covered: a real https connection (the browser reaches the stand-in proxy over plain http on localhost, where browsers still accept Secure
cookies), a real host, Linux.

## What is covered

| Spec | What it proves |
|---|---|
| `auth.spec.ts` | Guards and return addresses, the session cookie is HttpOnly and ends on sign-out, a hostile `returnUrl` is ignored, wrong details and unknown emails get the same words, five wrong passwords lock the account, a session that ends elsewhere shows a notice and clears the page, and the sign-in form still works after another session changed who the browser is |
| `documents-and-chat.spec.ts` | Upload, replace-with-confirmation, browser-side file checks, delete; an answer from your own document with its source passage; history survives a reload and a sign-out; "not found" and "no supported answer" are explained |
| `isolation.spec.ts` | Two people in two browsers: same file name, different documents, each answer comes from the asker's own; one cannot read, open, write into or delete the other's conversation (through the page and straight at the API); a request without the CSRF token is refused |
| `resilience.spec.ts` | A rate-limited model shows the error, the request id and the unanswered question, and Retry works; Stop restores the text and saves nothing; the answer to a clarifying question reaches the service together with the original question; "Ask something else instead" does not |
| `followup.spec.ts` | A follow-up is rewritten from the conversation (the stand-in model joins it to the last question), the page says what was searched for, also after a reload; a question that stands on its own is not rewritten; a new conversation does not inherit an old one's context |
| `theme.spec.ts` | The System / Light / Dark choice beats the device both ways, "System" follows the device, the choice is remembered across reloads and sign-out, it is applied with the app's own script blocked (no flash), and the accessibility scan passes when the choice and the device disagree |
| `responsive.spec.ts` | Every page at 320, 375, 768, 1024 and 1440 px: no sideways scrolling, nothing pushed off the edge, and a phone top bar short enough to leave room for the page |
| `accessibility.spec.ts` | An automated axe-core scan (WCAG 2.2 AA and best practices) of every screen and state, in light, dark and phone width |

The accessibility scan finds what a machine can find. It cannot judge whether a screen makes sense to someone using a screen reader, so it
complements a manual pass.

## Screenshots

```bash
npm run screenshots   # needs GEMINI_API_KEY in ai-service/.env; about five model calls
```

Regenerates `docs/screenshots/*.png` with the **real** model (the config turns it on for this project), on the same throwaway stack.
