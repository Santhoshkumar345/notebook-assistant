// Checked in the browser for immediate feedback; the API checks again. Asserted against api/openapi/v1.json in question-checks.spec.ts.

export const MIN_QUESTION_CHARS = 3;
export const MAX_QUESTION_CHARS = 1000;

/** A message for a person if the text cannot be sent, otherwise null. */
export function checkQuestion(text: string): string | null {
  const length = text.trim().length;
  if (length < MIN_QUESTION_CHARS) return `Type at least ${MIN_QUESTION_CHARS} characters.`;
  if (length > MAX_QUESTION_CHARS) return `That is ${length.toLocaleString('en-US')} characters. The limit is ${MAX_QUESTION_CHARS.toLocaleString('en-US')}.`;
  return null;
}
