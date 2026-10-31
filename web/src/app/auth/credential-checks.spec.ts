import openapi from '../../../../api/openapi/v1.json';
import { MAX_PASSWORD_CHARS, MIN_PASSWORD_CHARS, checkPassword, looksLikeEmail } from './credential-checks';

describe('the browser-side password limits', () => {
  const rules = openapi.components.schemas.RegisterIn.properties.password;

  it('use the same bounds as the server', () => {
    expect(MIN_PASSWORD_CHARS).toBe(rules.minLength);
    expect(MAX_PASSWORD_CHARS).toBe(rules.maxLength);
  });
});

describe('checkPassword', () => {
  it('accepts a password of the minimum and of the maximum length', () => {
    expect(checkPassword('x'.repeat(MIN_PASSWORD_CHARS))).toBeNull();
    expect(checkPassword('x'.repeat(MAX_PASSWORD_CHARS))).toBeNull();
  });

  it('refuses one character short, and one over', () => {
    expect(checkPassword('x'.repeat(MIN_PASSWORD_CHARS - 1))).toContain('at least 12');
    expect(checkPassword('x'.repeat(MAX_PASSWORD_CHARS + 1))).toContain('at most 128');
  });

  it('asks for length and nothing else: no capitals, digits or symbols', () => {
    expect(checkPassword('just some ordinary words')).toBeNull();
    expect(checkPassword('aaaaaaaaaaaa')).toBeNull();
  });

  it('counts spaces and unusual characters as characters', () => {
    expect(checkPassword('            ')).toBeNull(); // twelve spaces: the server decides whether that is wise, the length is met
    expect(checkPassword('é'.repeat(12))).toBeNull();
  });
});

describe('looksLikeEmail', () => {
  it.each(['a@b', 'person@example.com', ' person@example.com ', 'first.last+tag@example.co.uk'])('accepts %j', (value) => {
    expect(looksLikeEmail(value)).toBe(true);
  });

  it.each(['', 'person', '@example.com', 'person@', 'two words@example.com', 'a@b@c'])('rejects %j', (value) => {
    expect(looksLikeEmail(value)).toBe(false);
  });
});
