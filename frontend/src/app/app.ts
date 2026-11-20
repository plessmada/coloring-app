import { Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { GuestGeneration } from './generations/guest-generation';
import { HealthService } from './health/health.service';

type ReadinessState = 'loading' | 'ready' | 'failure';

@Component({
  selector: 'app-root',
  imports: [GuestGeneration],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly health = inject(HealthService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly state = signal<ReadinessState>('loading');
  protected readonly retryVisible = signal(false);
  private pending = false;

  ngOnInit(): void {
    this.checkReadiness();
  }

  protected checkReadiness(): void {
    if (this.pending) return;
    this.pending = true;
    this.state.set('loading');
    this.health
      .checkReadiness()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.pending = false;
          this.state.set('ready');
          this.retryVisible.set(false);
        },
        error: () => {
          this.pending = false;
          this.state.set('failure');
          this.retryVisible.set(true);
        },
      });
  }
}
