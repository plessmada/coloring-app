import { APIRequestContext, expect, Page, test } from '@playwright/test';

const controls = 'http://127.0.0.1:5215';
const noRelease = {
  audioExists: false,
  imageExists: false,
  stagedCount: 0,
};

interface JobSnapshot {
  status: string;
  calls: string[];
  audioExists: boolean;
  imageExists: boolean;
  stagedCount: number;
  approvedCount: number;
}

async function snapshot(request: APIRequestContext, id: string): Promise<JobSnapshot> {
  const response = await request.get(`${controls}/jobs/${id}`);
  expect(response.ok()).toBe(true);
  return response.json();
}

async function selectScenario(request: APIRequestContext, scenario: string): Promise<void> {
  expect((await request.post(`${controls}/scenario/${scenario}`)).ok()).toBe(true);
}

async function useSyntheticMicrophone(page: Page): Promise<void> {
  // Record a deterministic, audible stream with Chromium's real MediaRecorder.
  await page.addInitScript(() => {
    const mediaDevices = navigator.mediaDevices;
    mediaDevices.getUserMedia = async () => {
      const audio = new AudioContext();
      await audio.resume();
      const destination = audio.createMediaStreamDestination();
      const tone = audio.createOscillator();
      tone.frequency.value = 550;
      tone.connect(destination);
      tone.start();
      const track = destination.stream.getAudioTracks()[0];
      const stop = track.stop.bind(track);
      track.stop = () => {
        stop();
        tone.stop();
        void audio.close();
        document.documentElement.dataset['microphoneReleased'] = 'true';
      };
      return destination.stream;
    };
  });
}

async function record(page: Page, next = false): Promise<string> {
  let submissions = 0;
  const count = (request: import('@playwright/test').Request) => {
    if (new URL(request.url()).pathname === '/api/generations' && request.method() === 'POST') {
      submissions++;
    }
  };
  page.on('request', count);
  try {
    const start = page.getByRole('button', {
      name: next ? 'Nagraj kolejny pomysł' : 'Nagraj pomysł',
    });
    if (!next) {
      await page.goto('/');
      await expect(start).toBeVisible();
      await page.keyboard.press('Tab');
      await expect(start).toBeFocused();
      await page.keyboard.press('Enter');
    } else {
      await start.click();
    }
    const stop = page.getByRole('button', { name: 'Zatrzymaj nagrywanie' });
    await expect(stop).toBeFocused();
    await expect(page.getByText('1 / 60 s')).toBeVisible();
    const accepted = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === '/api/generations' &&
        response.request().method() === 'POST',
    );
    await page.keyboard.press('Enter');
    await page.keyboard.press('Enter');
    const response = await accepted;
    expect(response.status()).toBe(202);
    expect(submissions).toBe(1);
    await expect(page.locator('html')).toHaveAttribute('data-microphone-released', 'true');
    const result = (await response.json()) as { id: string; status: string };
    expect(result.status).toBe('Pending');
    expect(response.headers()['location']).toBe(`/api/generations/${result.id}`);
    return result.id;
  } finally {
    page.off('request', count);
  }
}

test('records, previews, downloads and prints, and denies a second guest who has the ID', async ({
  page,
  browser,
  request,
}, testInfo) => {
  await selectScenario(request, 'success');
  await useSyntheticMicrophone(page);
  const id = await record(page);
  const heading = page.getByRole('heading', { name: 'Twoja kolorowanka jest gotowa!' });
  const preview = page.getByRole('img', { name: 'Wygenerowana kolorowanka' });
  await expect(heading).toBeFocused();
  await expect(heading).toBeVisible();
  await expect(preview).toHaveJSProperty('naturalWidth', 1024);
  await expect(preview).toHaveJSProperty('naturalHeight', 1536);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('guest-result.png'), fullPage: true });

  await page.keyboard.press('Tab');
  const download = page.getByRole('button', { name: 'Pobierz PNG' });
  await expect(download).toBeFocused();
  const downloadEvent = page.waitForEvent('download');
  await page.keyboard.press('Enter');
  expect((await downloadEvent).suggestedFilename()).toBe(
    `kolorowanka-${id.replaceAll('-', '')}.png`,
  );
  await expect(download).toBeEnabled();
  await page.evaluate(() => {
    window.print = () => {
      document.documentElement.dataset['printOpened'] = 'true';
    };
  });
  await page.getByRole('button', { name: 'Drukuj' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-print-opened', 'true');
  await page.emulateMedia({ media: 'print' });
  await expect(heading).toBeHidden();
  await expect(page.getByRole('button')).toHaveCount(0);
  await expect(preview).toBeVisible();
  expect(await preview.evaluate((image) => image.clientWidth / image.clientHeight)).toBeCloseTo(
    2 / 3,
  );
  const pdf = await page.pdf({
    path: testInfo.outputPath('guest-print.pdf'),
    preferCSSPageSize: true,
  });
  const pdfSource = pdf.toString('latin1');
  expect(pdfSource.match(/\/Type \/Page\b/g)).toHaveLength(1);
  const [, width, height] =
    /\/MediaBox\s*\[0 0 (\d+(?:\.\d+)?) (\d+(?:\.\d+)?)\]/.exec(pdfSource) ?? [];
  expect(Number(width)).toBeCloseTo(595, 0);
  expect(Number(height)).toBeCloseTo(842, 0);
  await page.emulateMedia({ media: 'screen' });

  const foreign = await browser.newContext({ baseURL: 'http://localhost:4201' });
  try {
    const second = await foreign.newPage();
    await useSyntheticMicrophone(second);
    const foreignId = await record(second);
    await expect(second.getByRole('img', { name: 'Wygenerowana kolorowanka' })).toHaveJSProperty(
      'naturalWidth',
      1024,
    );
    const wrongOwner = await second.goto(`/api/generations/${id}`);
    expect(wrongOwner?.status()).toBe(404);
    await expect(second.getByText('generation_unavailable', { exact: false })).toBeVisible();
    for (const suffix of ['/image', '/image?download=true']) {
      const denied = await foreign.request.get(`/api/generations/${id}${suffix}`);
      expect(denied.status()).toBe(404);
      expect(denied.headers()['cache-control']).toBe('no-store');
    }
    expect((await foreign.request.get(`/api/generations/${foreignId}`)).status()).toBe(200);
  } finally {
    await foreign.close();
  }
  await expect
    .poll(async () => snapshot(request, id))
    .toMatchObject({
      status: 'Succeeded',
      audioExists: false,
      imageExists: true,
      stagedCount: 0,
    });
  expect(
    (await snapshot(request, id)).calls.filter((call) => call === 'generate_image'),
  ).toHaveLength(1);
});

test('a restart during image generation fails the expired job without generating or releasing another image', async ({
  page,
  request,
}) => {
  await selectScenario(request, 'pause_image');
  await useSyntheticMicrophone(page);
  const id = await record(page);
  await expect.poll(async () => (await snapshot(request, id)).calls).toContain('generate_image');
  await expect(page.getByText('Tworzymy kolorowankę…')).toBeVisible();
  const before = await snapshot(request, id);
  expect(before).toMatchObject({ status: 'Processing', audioExists: true, imageExists: false });
  expect((await request.post(`${controls}/restart`)).ok()).toBe(true);
  expect((await snapshot(request, id)).status).toBe('Processing');
  expect((await request.post(`${controls}/expire-lease/${id}`)).ok()).toBe(true);
  await expect(
    page.getByText('Problem techniczny przerwał tworzenie kolorowanki.', { exact: false }),
  ).toBeVisible();
  await expect(page.getByRole('img', { name: 'Wygenerowana kolorowanka' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Nagraj pomysł' })).toBeVisible();
  await expect
    .poll(async () => snapshot(request, id))
    .toMatchObject({
      ...noRelease,
      status: 'Failed',
      approvedCount: before.approvedCount,
    });
  expect((await snapshot(request, id)).calls).toEqual(before.calls);
  expect(before.calls.filter((call) => call === 'generate_image')).toHaveLength(1);
  expect((await page.request.get(`/api/generations/${id}/image?download=true`)).status()).toBe(404);
});

test('a failed image review withholds the bytes and shows the technical failure', async ({
  page,
  request,
}) => {
  await selectScenario(request, 'image_check_error');
  await useSyntheticMicrophone(page);
  const before = await snapshot(request, '00000000-0000-0000-0000-000000000000');
  const id = await record(page);
  await expect(
    page.getByText('Problem techniczny przerwał tworzenie kolorowanki.', { exact: false }),
  ).toBeVisible();
  await expect(page.getByRole('img', { name: 'Wygenerowana kolorowanka' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Pobierz PNG' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Nagraj pomysł' })).toBeFocused();
  await expect
    .poll(async () => snapshot(request, id))
    .toMatchObject({
      ...noRelease,
      status: 'Failed',
      approvedCount: before.approvedCount,
    });
  const calls = (await snapshot(request, id)).calls;
  expect(calls.at(-1)).toBe('moderate_image');
  expect(calls.filter((call) => call === 'generate_image')).toHaveLength(1);
  for (const suffix of ['/image', '/image?download=true']) {
    expect((await page.request.get(`/api/generations/${id}${suffix}`)).status()).toBe(404);
  }
});

test('explains denied microphone permission without uploading', async ({ page }) => {
  await page.addInitScript(() => {
    navigator.mediaDevices.getUserMedia = async () => {
      throw new DOMException('Permission denied', 'NotAllowedError');
    };
  });
  await page.route('**/api/generations', () => {
    throw new Error('Unexpected upload');
  });
  await page.goto('/');
  await page.getByRole('button', { name: 'Nagraj pomysł' }).click();
  await expect(page.getByText('Zezwól na użycie mikrofonu', { exact: false })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Nagraj pomysł' })).toBeFocused();
});

test('retry reads the same job after a transient status error', async ({ page, request }) => {
  await selectScenario(request, 'profanity');
  await useSyntheticMicrophone(page);
  let reads = 0;
  let posts = 0;
  page.on('request', (request) => {
    if (request.method() === 'POST') posts++;
  });
  await page.route(/\/api\/generations\/[\da-f-]{36}$/, async (route) => {
    reads++;
    if (reads === 1) await route.fulfill({ status: 503 });
    else await route.continue();
  });
  const id = await record(page);
  await expect(
    page.getByText('Nie udało się sprawdzić, czy kolorowanka jest gotowa.'),
  ).toBeVisible();
  const retry = page.getByRole('button', { name: 'Sprawdź ponownie' });
  await expect(retry).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByText('Spróbuj opisać inny pomysł na kolorowankę.')).toBeVisible();
  expect(reads).toBe(2);
  expect(posts).toBe(1);
  expect((await snapshot(request, id)).calls).not.toContain('generate_image');
});
