import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { HealthService } from './health.service';

describe('HealthService deadline', () => {
  afterEach(() => vi.useRealTimers());

  it('fails and cancels the request after five seconds', () => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const http = TestBed.inject(HttpTestingController);
    const failed = vi.fn();
    TestBed.inject(HealthService).checkReadiness().subscribe({ error: failed });
    const request = http.expectOne('/api/health/ready');
    vi.advanceTimersByTime(4999);
    expect(failed).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(failed).toHaveBeenCalledOnce();
    expect(request.cancelled).toBe(true);
    http.verify();
  });
});
