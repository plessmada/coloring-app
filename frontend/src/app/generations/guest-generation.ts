import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { GenerationService, GenerationStatus } from './generation.service';

type State =
  | { kind: 'idle' | 'permission' | 'recording' | 'uploading' | 'checking' }
  | { kind: 'waiting'; processing: boolean }
  | { kind: 'retry' | 'error'; message: string }
  | { kind: 'result'; imageUrl: string };

const format = 'audio/webm;codecs=opus';
const timeLimit = 60_000;
const sizeLimit = 5 * 1024 * 1024;
const unavailable = 'Ta kolorowanka wygasła lub jest niedostępna. Nagraj nowy pomysł.';
const technical = 'Problem techniczny przerwał tworzenie kolorowanki. Nagraj pomysł ponownie.';

@Component({
  selector: 'app-guest-generation',
  templateUrl: './guest-generation.html',
  styleUrl: './guest-generation.css',
  host: { '(window:pagehide)': 'leavePage()' },
})
export class GuestGeneration {
  private readonly api = inject(GenerationService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);
  private readonly primary = viewChild<ElementRef<HTMLElement>>('primary');
  protected readonly state = signal<State>({ kind: 'idle' });
  protected readonly seconds = signal(0);
  protected readonly imageLoaded = signal(false);
  protected readonly downloading = signal(false);
  protected readonly downloadMessage = signal('');
  private stream?: MediaStream;
  private recorder?: MediaRecorder;
  private elapsedTimer?: number;
  private stopTimer?: number;
  private pollTimer?: number;
  private statusUrl?: string;
  private captureAttempt = 0;

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.cancelCapture();
      window.clearTimeout(this.pollTimer);
    });
  }

  protected async startRecording(): Promise<void> {
    if (!['idle', 'error', 'result'].includes(this.state().kind)) return;

    this.statusUrl = undefined;
    this.imageLoaded.set(false);
    this.downloadMessage.set('');
    if (!window.isSecureContext) {
      this.fail('Otwórz aplikację przez HTTPS albo na localhost, aby użyć mikrofonu.');
      return;
    }
    if (
      typeof MediaRecorder === 'undefined' ||
      !navigator.mediaDevices?.getUserMedia ||
      !MediaRecorder.isTypeSupported?.(format)
    ) {
      this.fail('Ta przeglądarka nie nagrywa w wymaganym formacie. Otwórz aplikację w Chrome.');
      return;
    }

    this.state.set({ kind: 'permission' });
    const attempt = ++this.captureAttempt;
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      if (attempt !== this.captureAttempt || this.destroyRef.destroyed) {
        stream.getTracks().forEach((track) => track.stop());
        return;
      }

      this.stream = stream;
      const recorder = new MediaRecorder(stream, { mimeType: format });
      this.recorder = recorder;
      const chunks: Blob[] = [];
      let bytes = 0;
      recorder.addEventListener('dataavailable', ({ data }) => {
        if (this.recorder !== recorder || !data.size) return;
        bytes += data.size;
        if (bytes > sizeLimit) {
          this.fail('Nagranie jest zbyt duże. Nagraj krótszy opis.');
          return;
        }
        chunks.push(data);
      });
      recorder.addEventListener('error', () => {
        if (this.recorder === recorder) this.fail('Nagranie się nie udało. Sprawdź mikrofon.');
      });
      recorder.addEventListener(
        'stop',
        () => {
          if (this.recorder !== recorder) return;
          this.recorder = undefined;
          this.releaseMicrophone();
          this.submit(new Blob(chunks, { type: format }));
        },
        { once: true },
      );
      recorder.start(250);
      const started = performance.now();
      this.seconds.set(0);
      this.state.set({ kind: 'recording' });
      this.elapsedTimer = window.setInterval(
        () => this.seconds.set(Math.min(60, Math.floor((performance.now() - started) / 1000))),
        200,
      );
      this.stopTimer = window.setTimeout(() => this.stopRecording(), timeLimit);
      this.focusPrimary();
    } catch (error: unknown) {
      if (attempt !== this.captureAttempt || this.destroyRef.destroyed) return;
      const name = error instanceof DOMException ? error.name : '';
      if (name === 'NotAllowedError' || name === 'SecurityError') {
        this.fail('Zezwól na użycie mikrofonu w ustawieniach tej strony i spróbuj ponownie.');
      } else if (name === 'NotFoundError' || name === 'NotReadableError' || name === 'AbortError') {
        this.fail('Mikrofon jest niedostępny. Podłącz go lub zamknij aplikację, która go używa.');
      } else {
        this.fail('Nie udało się nagrać dźwięku. Sprawdź mikrofon i spróbuj ponownie w Chrome.');
      }
    }
  }

  protected stopRecording(): void {
    if (this.state().kind !== 'recording' || !this.recorder) return;
    this.state.set({ kind: 'uploading' });
    if (this.recorder.state !== 'inactive') this.recorder.stop();
    this.releaseMicrophone();
  }

  protected leavePage(): void {
    if (['recording', 'permission'].includes(this.state().kind)) {
      this.cancelCapture();
      this.state.set({ kind: 'idle' });
    }
  }

  private submit(audio: Blob): void {
    if (!audio.size) {
      this.fail('Nagranie jest puste. Nagraj pomysł jeszcze raz.');
      return;
    }
    this.state.set({ kind: 'uploading' });
    this.api
      .submit(audio)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (url) => {
          this.statusUrl = url;
          this.wait(false);
        },
        error: (error: unknown) => {
          const status = error instanceof HttpErrorResponse ? error.status : 0;
          if (status === 413) this.fail('Nagranie jest zbyt duże. Nagraj krótszy opis.');
          else if (status === 415)
            this.fail('Format nagrania jest nieobsługiwany. Spróbuj ponownie w Chrome.');
          else if (status === 400)
            this.fail('Nagranie jest nieprawidłowe. Nagraj pomysł jeszcze raz.');
          else this.fail('Nie udało się potwierdzić wysłania nagrania. Możesz nagrać nowy pomysł.');
        },
      });
  }

  private wait(processing: boolean): void {
    this.state.set({ kind: 'waiting', processing });
    this.pollTimer = window.setTimeout(() => this.readStatus(), 3000);
  }

  protected retryStatus(): void {
    if (this.state().kind !== 'retry') return;
    this.state.set({ kind: 'checking' });
    this.readStatus();
  }

  private readStatus(): void {
    if (!this.statusUrl) return;
    this.api
      .status(this.statusUrl)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (status) => this.showStatus(status),
        error: (error: unknown) => {
          if (error instanceof HttpErrorResponse && [401, 403, 404, 410].includes(error.status)) {
            this.fail(unavailable);
            return;
          }
          this.state.set({
            kind: 'retry',
            message: 'Nie udało się sprawdzić, czy kolorowanka jest gotowa.',
          });
          this.focusPrimary();
        },
      });
  }

  private showStatus(result: GenerationStatus): void {
    switch (result.status) {
      case 'Pending':
      case 'Processing':
        this.wait(result.status === 'Processing');
        break;
      case 'Succeeded':
        this.state.set({ kind: 'result', imageUrl: result.imageUrl });
        this.focusPrimary();
        break;
      default:
        this.fail(
          result.errorCode === 'unintelligible_audio'
            ? 'Nie udało się zrozumieć opisu. Nagraj go jeszcze raz.'
            : result.errorCode === 'content_rejected'
              ? 'Spróbuj opisać inny pomysł na kolorowankę.'
              : technical,
        );
    }
  }

  protected onImageLoad(image: HTMLImageElement): void {
    this.imageLoaded.set(image.complete && image.naturalWidth > 0);
  }

  protected onImageError(): void {
    this.fail(unavailable);
  }

  protected download(): void {
    const current = this.state();
    if (current.kind !== 'result' || !this.imageLoaded() || this.downloading()) return;
    this.downloading.set(true);
    this.downloadMessage.set('');
    this.api
      .download(current.imageUrl)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.downloading.set(false);
          if (this.state() !== current || !response.body) return;
          const link = document.createElement('a');
          const objectUrl = URL.createObjectURL(response.body);
          link.href = objectUrl;
          link.download =
            response.headers
              .get('Content-Disposition')
              ?.match(/kolorowanka-[\da-f]{32}\.png/i)?.[0] ?? 'kolorowanka.png';
          link.click();
          window.setTimeout(() => URL.revokeObjectURL(objectUrl), 1000);
        },
        error: (error: unknown) => {
          this.downloading.set(false);
          if (this.state() !== current) return;
          if (error instanceof HttpErrorResponse && error.status === 404) this.fail(unavailable);
          else this.downloadMessage.set('Nie udało się pobrać pliku. Spróbuj ponownie.');
        },
      });
  }

  protected print(): void {
    if (this.state().kind === 'result' && this.imageLoaded()) window.print();
  }

  private fail(message: string): void {
    this.cancelCapture();
    window.clearTimeout(this.pollTimer);
    this.statusUrl = undefined;
    this.imageLoaded.set(false);
    this.state.set({ kind: 'error', message });
    this.focusPrimary();
  }

  private cancelCapture(): void {
    this.captureAttempt++;
    const recorder = this.recorder;
    this.recorder = undefined;
    if (recorder && recorder.state !== 'inactive') recorder.stop();
    this.releaseMicrophone();
  }

  private releaseMicrophone(): void {
    window.clearTimeout(this.stopTimer);
    window.clearInterval(this.elapsedTimer);
    this.stream?.getTracks().forEach((track) => track.stop());
    this.stream = undefined;
  }

  private focusPrimary(): void {
    afterNextRender(
      { write: () => this.primary()?.nativeElement.focus() },
      { injector: this.injector },
    );
  }
}
