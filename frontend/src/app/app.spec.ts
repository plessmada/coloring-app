import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './app';

const ready = { status: 'ok', application: 'ColoringApp', database: 'ok' };

describe('Foundation screen', () => {
  let fixture: ComponentFixture<App>;
  let http: HttpTestingController;
  const text = () => (fixture.nativeElement as HTMLElement).textContent;
  const button = () => (fixture.nativeElement as HTMLElement).querySelector('button');
  const fail = () =>
    http.expectOne('/api/health/ready').flush({}, { status: 503, statusText: 'Unavailable' });

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(App);
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });

  it('shows the title and loading state while exactly one request is pending', () => {
    expect(text()).toContain('ColoringApp');
    expect(text()).toContain('Twoje pomysły zamienimy w kolorowanki.');
    expect(text()).toContain('Sprawdzamy gotowość aplikacji…');
    expect(button()).toBeNull();
    const request = http.expectOne('/api/health/ready');
    expect(request.request.method).toBe('GET');
    request.flush(ready);
  });

  it('shows readiness without polling', () => {
    http.expectOne('/api/health/ready').flush(ready);
    fixture.detectChanges();
    expect(text()).toContain('Aplikacja jest gotowa.');
    expect(button()).toBeNull();
    http.expectNone('/api/health/ready');
  });

  it('shows an actionable generic failure without backend details', () => {
    http
      .expectOne('/api/health/ready')
      .flush({ code: 'database_not_initialized' }, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();
    expect(text()).toContain('Nie udało się połączyć z aplikacją. Spróbuj ponownie.');
    expect(text()).not.toContain('database_not_initialized');
    expect(button()?.textContent).toContain('Spróbuj ponownie');
  });

  it('handles network failure in the same way', () => {
    http.expectOne('/api/health/ready').error(new ProgressEvent('error'));
    fixture.detectChanges();
    expect(text()).toContain('Nie udało się połączyć z aplikacją. Spróbuj ponownie.');
  });

  it('retries once, disables the pending button, and returns to ready', () => {
    fail();
    fixture.detectChanges();
    button()?.click();
    fixture.detectChanges();
    expect(text()).toContain('Sprawdzamy gotowość aplikacji…');
    expect(button()?.disabled).toBe(true);
    // A direct event also exercises the pending guard, independently of native disabled behavior.
    button()?.dispatchEvent(new Event('click'));
    http.expectOne('/api/health/ready').flush(ready);
    fixture.detectChanges();
    expect(text()).toContain('Aplikacja jest gotowa.');
    expect(button()).toBeNull();
  });

  it('allows another retry after a second failure', () => {
    fail();
    fixture.detectChanges();
    button()?.click();
    fail();
    fixture.detectChanges();
    expect(button()?.disabled).toBe(false);
    button()?.click();
    http.expectOne('/api/health/ready').flush(ready);
    fixture.detectChanges();
    expect(text()).toContain('Aplikacja jest gotowa.');
  });

  it('shows the failure state when a retry exceeds the five-second deadline', () => {
    fail();
    fixture.detectChanges();
    vi.useFakeTimers();
    button()?.click();
    const request = http.expectOne('/api/health/ready');
    vi.advanceTimersByTime(5000);
    fixture.detectChanges();
    expect(request.cancelled).toBe(true);
    expect(text()).toContain('Nie udało się połączyć z aplikacją. Spróbuj ponownie.');
    expect(button()?.disabled).toBe(false);
  });

  it('cancels a pending request when the screen is destroyed', () => {
    const request = http.expectOne('/api/health/ready');
    fixture.destroy();
    expect(request.cancelled).toBe(true);
  });
});
