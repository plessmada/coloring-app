import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, timeout } from 'rxjs';

export interface ReadinessResponse {
  status: 'ok';
  application: 'ColoringApp';
  database: 'ok';
}

@Injectable({ providedIn: 'root' })
export class HealthService {
  private readonly http = inject(HttpClient);

  checkReadiness(): Observable<ReadinessResponse> {
    return this.http.get<ReadinessResponse>('/api/health/ready').pipe(timeout(5000));
  }
}
