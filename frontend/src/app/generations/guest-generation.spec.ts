import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { GuestGeneration } from './guest-generation';

const job = '04fc67e8-79aa-42ba-9634-e4fc53391625';
const statusUrl = `/api/generations/${job}`;

class TestRecorder extends EventTarget {
  static isTypeSupported = () => true;
  state: RecordingState = 'inactive';

  start(): void {
    this.state = 'recording';
  }

  stop(): void {
    this.state = 'inactive';
    const data = new Blob(['controlled audio'], { type: 'audio/webm' });
    this.dispatchEvent(Object.assign(new Event('dataavailable'), { data }));
    this.dispatchEvent(new Event('stop'));
  }
}

describe('Guest recording lifecycle', () => {
  let fixture: ComponentFixture<GuestGeneration>;
  let http: HttpTestingController;
  const stopTrack = vi.fn();
  const originalMediaDevices = Object.getOwnPropertyDescriptor(navigator, 'mediaDevices');
  const button = () => (fixture.nativeElement as HTMLElement).querySelector('button');

  beforeEach(() => {
    vi.stubGlobal('isSecureContext', true);
    vi.stubGlobal('MediaRecorder', TestRecorder);
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia: async () => ({ getTracks: () => [{ stop: stopTrack }] }) },
    });
    TestBed.configureTestingModule({
      imports: [GuestGeneration],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(GuestGeneration);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    stopTrack.mockClear();
  });

  afterEach(() => {
    fixture.destroy();
    if (originalMediaDevices) {
      Object.defineProperty(navigator, 'mediaDevices', originalMediaDevices);
    } else {
      Reflect.deleteProperty(navigator, 'mediaDevices');
    }
    vi.unstubAllGlobals();
    vi.useRealTimers();
    http.verify();
  });

  async function record(): Promise<void> {
    button()?.click();
    await Promise.resolve();
    fixture.detectChanges();
    expect(button()?.textContent).toContain('Zatrzymaj nagrywanie');
  }

  it('stops at 60 seconds and waits three seconds between completed status reads', async () => {
    vi.useFakeTimers();
    await record();
    vi.advanceTimersByTime(59_999);
    http.expectNone('/api/generations');
    vi.advanceTimersByTime(1);
    expect(stopTrack).toHaveBeenCalledOnce();
    const upload = http.expectOne('/api/generations');
    expect(upload.request.method).toBe('POST');
    upload.flush(
      { id: job, status: 'Pending' },
      {
        status: 202,
        statusText: 'Accepted',
        headers: { Location: statusUrl },
      },
    );

    vi.advanceTimersByTime(2_999);
    http.expectNone(statusUrl);
    vi.advanceTimersByTime(1);
    const firstRead = http.expectOne(statusUrl);
    vi.advanceTimersByTime(6_000);
    http.expectNone(statusUrl);
    firstRead.flush({ id: job, status: 'Processing' });
    vi.advanceTimersByTime(3_000);
    http
      .expectOne(statusUrl)
      .flush({ id: job, status: 'Failed', errorCode: 'unintelligible_audio' });
    vi.advanceTimersByTime(9_000);
    http.expectNone(statusUrl);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Nie udało się zrozumieć opisu.');
  });

  it('releases the microphone when leaving without submitting the recording', async () => {
    await record();
    window.dispatchEvent(new Event('pagehide'));
    fixture.detectChanges();
    expect(stopTrack).toHaveBeenCalledOnce();
    expect(button()?.textContent).toContain('Nagraj pomysł');
    http.expectNone('/api/generations');
  });

  it('does not retry an upload with an ambiguous outcome', async () => {
    vi.useFakeTimers();
    await record();
    button()?.click();
    const upload = http.expectOne('/api/generations');
    upload.error(new ProgressEvent('error'));
    vi.advanceTimersByTime(60_000);
    http.expectNone('/api/generations');
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Nie udało się potwierdzić wysłania');
  });
});
