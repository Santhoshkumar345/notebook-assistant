import { safeReturnUrl } from './safe-return-url';

describe('safeReturnUrl', () => {
  it.each(['/chat', '/chat/3acfb8d4-46b8-4816-845e-8d8f1b36e23d', '/documents', '/chat?x=1#top', '/loginx', '/chat/login'])('follows the path inside the app %j', (url) => {
    expect(safeReturnUrl(url)).toBe(url);
  });

  it.each([null, undefined, ''])('falls back to the chat page when there is nothing to return to (%j)', (value) => {
    expect(safeReturnUrl(value)).toBe('/chat');
  });

  // Each of these, followed blindly after signing in, would send a person to a site the attacker chose.
  it.each([
    'https://evil.example/phish',
    'http://evil.example',
    '//evil.example',
    '//evil.example/path',
    '/\\evil.example',
    '\\\\evil.example',
    'javascript:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    'evil.example',
    'chat',
    '/chat\nSet-Cookie: x=y',
    '/chat\r\n',
    '/a\\b',
    '/\u0000chat',
  ])('does not follow %j', (url) => {
    expect(safeReturnUrl(url)).toBe('/chat');
  });

  it.each(['/login', '/login?returnUrl=/chat', '/login#x', '/login/', '/register', '/register?x=1'])('does not return to a sign-in page (%j)', (url) => {
    expect(safeReturnUrl(url)).toBe('/chat');
  });
});
