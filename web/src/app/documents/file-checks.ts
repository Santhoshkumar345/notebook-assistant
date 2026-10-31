// Checked in the browser for fast, specific feedback. The API checks the same rules again, because a browser is not a trust boundary.
// These values are asserted against api/openapi/v1.json in file-checks.spec.ts, so they cannot drift from the server's.

export const MAX_DOCUMENT_CHARS = 200_000;

// .NET's pattern ends in \z (end of text, no trailing-newline quirk); in JavaScript, without the m flag, $ already means that.
export const NAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._ -]{0,98}\.md$/;

// A character is at most 4 bytes in UTF-8, so a larger file cannot be within the limit: refuse it before reading it into memory.
export const MAX_FILE_BYTES = MAX_DOCUMENT_CHARS * 4;

/** A message for a person if the file cannot be uploaded, otherwise null. */
export function checkMarkdownFile(name: string, size: number, content?: string): string | null {
  if (!NAME_PATTERN.test(name)) {
    return `"${name}" is not an accepted file name. Use letters, digits, spaces, dots, dashes or underscores (up to 100 characters), ending in .md.`;
  }
  if (size > MAX_FILE_BYTES) {
    return `"${name}" is too large. The limit is ${MAX_DOCUMENT_CHARS.toLocaleString('en-US')} characters.`;
  }
  if (content !== undefined) {
    if (content.trim() === '') return `"${name}" is empty.`;
    if (content.length > MAX_DOCUMENT_CHARS) {
      return `"${name}" has ${content.length.toLocaleString('en-US')} characters. The limit is ${MAX_DOCUMENT_CHARS.toLocaleString('en-US')}.`;
    }
  }
  return null;
}
