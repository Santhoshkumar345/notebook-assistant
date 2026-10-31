const DEFAULT_URL = '/chat';

/**
 * Where to go after signing in. The `returnUrl` query parameter comes from the address bar, which anyone can put anything in, so it is only
 * followed when it is a plain path inside this app. Otherwise a link such as /login?returnUrl=//evil.example would sign a person in and
 * then send them to a look-alike page (an open redirect).
 */
export function safeReturnUrl(value: string | null | undefined): string {
  if (!value) return DEFAULT_URL;
  if (!value.startsWith('/')) return DEFAULT_URL; // "https://evil.example", "javascript:alert(1)", "evil.example"
  if (value.startsWith('//') || value.startsWith('/\\')) return DEFAULT_URL; // a protocol-relative URL: the browser would read it as another site
  if (/[\u0000-\u001f\u007f\\]/.test(value)) return DEFAULT_URL; // control characters and backslashes are treated as slashes by some browsers
  if (/^\/(login|register)(\/|\?|#|$)/.test(value)) return DEFAULT_URL; // signing in again to land on the sign-in page helps no one
  return value;
}
