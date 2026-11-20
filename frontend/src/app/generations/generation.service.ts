import { HttpClient, HttpResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, map, timeout } from 'rxjs';

interface Submission {
  id: string;
  status: 'Pending';
}

export type GenerationStatus = { id: string } & (
  | { status: 'Pending' | 'Processing' }
  | { status: 'Succeeded'; imageUrl: string }
  | { status: 'Failed' | 'Rejected'; errorCode: string }
);

@Injectable({ providedIn: 'root' })
export class GenerationService {
  private readonly http = inject(HttpClient);

  submit(audio: Blob): Observable<string> {
    const form = new FormData();
    form.append('audio', audio, 'recording.webm');
    return this.http.post<Submission>('/api/generations', form, { observe: 'response' }).pipe(
      timeout(55_000),
      map((response) => {
        const url = response.headers.get('Location');
        if (
          response.status !== 202 ||
          response.body?.status !== 'Pending' ||
          url !== `/api/generations/${response.body.id}` ||
          !/^\/api\/generations\/[\da-f-]{36}$/i.test(url)
        ) {
          throw new Error('Invalid submission response');
        }
        return url;
      }),
    );
  }

  status(url: string): Observable<GenerationStatus> {
    return this.http.get<GenerationStatus>(url).pipe(
      timeout(8_000),
      map((result) => {
        if (
          `/api/generations/${result.id}` !== url ||
          !['Pending', 'Processing', 'Succeeded', 'Rejected', 'Failed'].includes(result.status) ||
          (result.status === 'Succeeded' && result.imageUrl !== `${url}/image`)
        ) {
          throw new Error('Invalid status response');
        }
        return result;
      }),
    );
  }

  download(imageUrl: string): Observable<HttpResponse<Blob>> {
    return this.http
      .get(`${imageUrl}?download=true`, { observe: 'response', responseType: 'blob' })
      .pipe(timeout(15_000));
  }
}
