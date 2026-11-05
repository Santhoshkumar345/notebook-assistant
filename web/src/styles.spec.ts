import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const css = readFileSync(resolve(process.cwd(), 'src', 'styles.css'), 'utf-8');

/** The `--name: value` declarations inside the first block that starts with this selector. */
function tokens(selector: string): Record<string, string> {
  const start = css.indexOf(selector);
  expect(start, `${selector} is in styles.css`).toBeGreaterThan(-1);
  const body = css.slice(css.indexOf('{', start) + 1, css.indexOf('}', start));
  return Object.fromEntries([...body.matchAll(/(--[\w-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
}

describe('the colour tokens', () => {
  it('are the same dark colours whether dark comes from the device or from the toggle', () => {
    const fromDevice = tokens(":root:not([data-theme='light'])");
    const fromChoice = tokens(":root[data-theme='dark']");

    expect(Object.keys(fromChoice).length).toBeGreaterThan(20);
    expect(fromDevice).toEqual(fromChoice); // two copies of one palette: this keeps them from drifting apart
  });

  it('define every token the light palette defines, so dark never falls back to a light colour by omission', () => {
    const light = tokens(':root {');
    const dark = tokens(":root[data-theme='dark']");
    const colourAndShadow = Object.keys(light).filter((name) => /^--(bg|surface|text|muted|border|accent|focus|danger|on-danger|ok|warn|bad|info|glow|shadow)/.test(name));

    for (const name of colourAndShadow) expect(dark, name).toHaveProperty([name]);
  });

  it('give an explicit light choice the light colour scheme, and an explicit dark choice the dark one, so form controls and scrollbars match', () => {
    expect(css).toMatch(/:root\[data-theme='light'\]\s*\{\s*color-scheme:\s*light;/);
    expect(css).toMatch(/:root\[data-theme='dark'\]\s*\{\s*color-scheme:\s*dark;/);
  });
});
