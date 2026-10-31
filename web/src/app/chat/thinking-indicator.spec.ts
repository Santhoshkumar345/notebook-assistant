import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ThinkingIndicator } from './thinking-indicator';

describe('ThinkingIndicator', () => {
  let fixture: ComponentFixture<ThinkingIndicator>;
  const text = () => (fixture.nativeElement as HTMLElement).textContent?.replace(/\s+/g, ' ').trim() ?? '';

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-31T09:00:00Z'));
    fixture = TestBed.createComponent(ThinkingIndicator);
    fixture.componentRef.setInput('since', Date.now());
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
    vi.useRealTimers();
  });

  function wait(seconds: number) {
    vi.advanceTimersByTime(seconds * 1000);
    fixture.detectChanges();
  }

  it('counts the seconds since the question was sent', () => {
    expect(text()).toContain('Thinking… 0 s');
    wait(3);
    expect(text()).toContain('Thinking… 3 s');
  });

  it('admits when it is slow, after 15 seconds and not before', () => {
    wait(14);
    expect(text()).not.toContain('Still working');
    wait(1);
    expect(text()).toContain('Still working');
  });

  it('lets the person stop', () => {
    let stopped = 0;
    fixture.componentInstance.stop.subscribe(() => stopped++);

    (fixture.nativeElement as HTMLElement).querySelector('button')!.click();

    expect(stopped).toBe(1);
  });

  it('stops counting when it goes away', () => {
    const before = vi.getTimerCount();
    fixture.destroy();
    expect(vi.getTimerCount()).toBeLessThan(before);
  });
});
