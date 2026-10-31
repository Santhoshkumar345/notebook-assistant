import openapi from '../../../../api/openapi/v1.json';
import { MAX_DOCUMENT_CHARS, MAX_FILE_BYTES, NAME_PATTERN, checkMarkdownFile } from './file-checks';

describe('the browser-side limits', () => {
  // The server's rules live in the OpenAPI snapshot, which a .NET test keeps equal to the real API.
  const rules = openapi.components.schemas.DocumentIn.properties;

  it('use the same file-name pattern as the server (.NET writes \\z where JavaScript writes $)', () => {
    expect(NAME_PATTERN.source).toBe(rules.name.pattern.replace('\\z', '$'));
  });

  it('use the same maximum length as the server', () => {
    expect(MAX_DOCUMENT_CHARS).toBe(rules.content.maxLength);
  });
});

describe('checkMarkdownFile', () => {
  it('accepts an ordinary markdown file', () => {
    expect(checkMarkdownFile('my notes-v2.md', 100, '# Notes\n\nText')).toBeNull();
  });

  it.each(['notes.txt', 'a/b.md', '../x.md', '.md', 'notes.md\n', 'naïve.md', ''])('rejects the name %j', (name) => {
    expect(checkMarkdownFile(name, 10, '# x')).toContain('not an accepted file name');
  });

  it('allows a name of exactly 102 characters and rejects 103', () => {
    expect(checkMarkdownFile('a' + 'b'.repeat(98) + '.md', 10, '# x')).toBeNull();
    expect(checkMarkdownFile('a' + 'b'.repeat(99) + '.md', 10, '# x')).not.toBeNull();
  });

  it('refuses a file too large to be within the limit before it is read', () => {
    expect(checkMarkdownFile('big.md', MAX_FILE_BYTES + 1)).toContain('too large');
    expect(checkMarkdownFile('ok.md', MAX_FILE_BYTES)).toBeNull();
  });

  it('rejects empty and whitespace-only content', () => {
    expect(checkMarkdownFile('a.md', 0, '')).toContain('empty');
    expect(checkMarkdownFile('a.md', 4, ' \n\t ')).toContain('empty');
  });

  it('rejects content over the character limit, with the counts in the message', () => {
    const message = checkMarkdownFile('a.md', 1000, 'x'.repeat(MAX_DOCUMENT_CHARS + 1));
    expect(message).toContain('200,001');
    expect(message).toContain('200,000');
  });

  it('accepts content of exactly the limit', () => {
    expect(checkMarkdownFile('a.md', 1000, 'x'.repeat(MAX_DOCUMENT_CHARS))).toBeNull();
  });
});
