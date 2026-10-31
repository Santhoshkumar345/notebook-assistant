import openapi from '../../../../api/openapi/v1.json';
import { MAX_QUESTION_CHARS, MIN_QUESTION_CHARS, checkQuestion } from './question-checks';

describe('the browser-side question limits', () => {
  const rules = openapi.components.schemas.AskIn.properties.question;

  it('use the same bounds as the server', () => {
    expect(MIN_QUESTION_CHARS).toBe(rules.minLength);
    expect(MAX_QUESTION_CHARS).toBe(rules.maxLength);
  });
});

describe('checkQuestion', () => {
  it('accepts a question of the minimum and of the maximum length', () => {
    expect(checkQuestion('abc')).toBeNull();
    expect(checkQuestion('x'.repeat(MAX_QUESTION_CHARS))).toBeNull();
  });

  it('counts the text without surrounding whitespace', () => {
    expect(checkQuestion('  ab  ')).toContain('at least 3');
    expect(checkQuestion('   ')).toContain('at least 3');
    expect(checkQuestion(`  ${'x'.repeat(MAX_QUESTION_CHARS)}  `)).toBeNull();
  });

  it('rejects an over-long question and says by how much it is over', () => {
    const message = checkQuestion('x'.repeat(MAX_QUESTION_CHARS + 1));
    expect(message).toContain('1,001');
    expect(message).toContain('1,000');
  });
});
