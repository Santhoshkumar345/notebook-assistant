// Checked in the browser for immediate feedback; the API checks again. The password bounds are asserted against api/openapi/v1.json in
// credential-checks.spec.ts, so they cannot drift from the server's.

export const MIN_PASSWORD_CHARS = 12;
export const MAX_PASSWORD_CHARS = 128;

/** A message for a person if the password cannot be used, otherwise null. Length is the only rule: no capitals, digits or symbols are demanded. */
export function checkPassword(password: string): string | null {
  if (password.length < MIN_PASSWORD_CHARS) return `Use at least ${MIN_PASSWORD_CHARS} characters. A few ordinary words work well.`;
  if (password.length > MAX_PASSWORD_CHARS) return `Use at most ${MAX_PASSWORD_CHARS} characters.`;
  return null;
}

/** Deliberately loose: the server decides what an address is. This only catches an obvious slip before a round trip. */
export function looksLikeEmail(email: string): boolean {
  return /^[^\s@]+@[^\s@]+$/.test(email.trim());
}
