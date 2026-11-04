import { Component } from '@angular/core';

/**
 * The panel beside the sign-in and registration forms: what the app is for, in three plain claims that are each something the app really does
 * (it cites the passage an answer came from, it says so when the documents do not contain the answer, and one person's documents are not visible to
 * another). Decoration for sighted people: it repeats nothing a form needs, so it is hidden from assistive technology and on narrow screens.
 */
@Component({
  selector: 'app-auth-showcase',
  host: { 'aria-hidden': 'true' },
  template: `
    <div class="inner">
      <p class="title">Ask your notes. Get answers you can check.</p>
      <ul>
        <li>
          <svg viewBox="0 0 20 20" focusable="false"><path d="M4 10.5l4 4 8-9" /></svg>
          <span>Every answer shows the passage it came from</span>
        </li>
        <li>
          <svg viewBox="0 0 20 20" focusable="false"><path d="M4 10.5l4 4 8-9" /></svg>
          <span>If your documents do not say, it tells you instead of guessing</span>
        </li>
        <li>
          <svg viewBox="0 0 20 20" focusable="false"><path d="M4 10.5l4 4 8-9" /></svg>
          <span>Your documents are private to your account</span>
        </li>
      </ul>
    </div>
  `,
  styles: `
    :host {
      position: relative;
      display: flex;
      align-items: center;
      overflow: hidden;
      padding: 2.5rem 2rem;
      background: var(--brand-gradient);
      color: #fff;
    }
    /* Soft shapes behind the words. Kept faint enough that white text stays above 5:1 wherever they overlap it. */
    :host::before,
    :host::after {
      content: '';
      position: absolute;
      border-radius: 50%;
      background: rgba(255, 255, 255, 0.07);
    }
    :host::before {
      width: 18rem;
      height: 18rem;
      right: -7rem;
      top: -6rem;
    }
    :host::after {
      width: 14rem;
      height: 14rem;
      left: -5rem;
      bottom: -6rem;
    }
    .inner {
      position: relative;
      z-index: 1;
    }
    .title {
      margin: 0 0 1.5rem;
      font-size: 1.6rem;
      font-weight: 750;
      line-height: 1.2;
      letter-spacing: -0.02em;
    }
    ul {
      display: grid;
      gap: 0.9rem;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    li {
      display: flex;
      align-items: flex-start;
      gap: 0.7rem;
      line-height: 1.4;
    }
    svg {
      flex: none;
      width: 1.3rem;
      height: 1.3rem;
      padding: 0.2rem;
      border-radius: 50%;
      background: rgba(255, 255, 255, 0.22);
      fill: none;
      stroke: #fff;
      stroke-width: 2.4;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
    @media (max-width: 47.99rem) {
      :host {
        display: none;
      }
    }
  `,
})
export class AuthShowcase {}
