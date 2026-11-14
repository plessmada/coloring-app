# ColoringApp — Product Specification

Status: Approved MVP scope
Approval date: 2026-11-12
Architecture-aligned update approved: 2026-11-14
Target delivery: End of December 2026

## Purpose and audience

ColoringApp turns a child's spoken idea into a printable coloring page. It removes the need to browse large catalogs and advertising-heavy websites, and supports original ideas instead of limiting users to existing pictures.

The primary participant is a child who can describe an idea aloud. Reading, writing, and independent operation of the entire application are not required. A parent may operate the recording controls and print the result. Parents and teachers are also potential users.

The first release is a working, publicly accessible web application for a university Web Applications course. It must demonstrate coloring-page generation, database persistence, registration, and login. Commercial launch and monetization are future objectives.

## MVP scope

### Recording and generation

- Provide a responsive web interface for phones and computers.
- Support Polish UI text and Polish spoken descriptions only.
- Allow both guests and authenticated users to generate coloring pages.
- Accept descriptions through voice recording only, with explicit start and stop controls.
- Automatically submit the recording to the backend when recording stops.
- Transcribe, validate the content, enrich the description, and generate a coloring page without requiring transcript confirmation or editing.
- Reject vulgar content, sexual content, graphic violence, and other content inappropriate for children before image generation. Child-friendly fantasy subjects such as monsters, dragons, knights, and dinosaurs are allowed.
- Preserve the child's intended subject when enriching the description. Optimize the prompt for clear outlines, a white background, and no shading.
- Produce one black-and-white coloring page per successful recording, in a portrait layout suitable for A4 printing.
- Automatically check the generated image before making it available. Rejection ends the attempt without automatically generating a replacement; a technical failure of the check also prevents release of the image.
- Show a processing state while the user waits.
- Provide image preview, PNG download, and printing through the browser's print dialog with an A4-friendly layout.

### Accounts and authentication

- Use an email address and a one-time code delivered by email; do not use passwords.
- Successful email verification creates an account if it does not exist, or signs the user into the existing account.
- Registration and login share one user flow. No separate email-confirmation message or password-reset flow is needed.
- Use ordinary user accounts, without dedicated child accounts or child-specific roles.
- Keep users signed in for seven days from login without automatic extension, with an option to log out earlier.

### History and saving a guest result

- Automatically save every successful authenticated generation to the user's account history.
- Show a thumbnail, generation date, and recognized description in history.
- Allow users to open, download, and print a stored image again without regenerating it.
- Guests see their current result but have no guest history, including no history persisted locally in the browser.
- Guest access lasts 24 hours through a temporary access session. Its cookie does not contain a history of coloring pages. After access expires, an unclaimed generation remains stored but can no longer be opened or claimed through that session.
- Show a call to action below a guest result: "Zachowaj tę kolorowankę na swoim koncie".
- After registration or login through that action, associate that exact existing generation with the authenticated account. Do not generate a replacement image.
- A new account created through this flow already contains the saved coloring page.
- Do not display the enriched prompt or generated keywords in the user interface.

## Data and recording lifecycle

Persist the following for each successful generation on the server:

- Raw transcript.
- Final image-generation prompt.
- Keywords derived from the description, reserved for future search functionality.
- Generated image.
- Generation date and account association, if applicable.

Guest generations are persisted without an account association until explicitly saved through the authentication flow. This server-side persistence does not provide guests with a history feature.

Successful generations and their associated images and text are retained indefinitely, including unclaimed guest generations. Expiration of guest access does not delete the stored generation.

Recordings are temporary processing inputs. Delete them when each attempt ends, whether it succeeds, fails, or is rejected. Do not retain an audio archive.

A cleanup mechanism removes orphaned temporary recordings after interrupted processing and retries failed deletions. Application-side deletion does not define the AI provider's retention policy; that remains an integration prerequisite in [architecture.md](architecture.md).

## Error behavior

- Unintelligible or unusable speech: explain that the description could not be understood and ask the user to record a new description.
- Disallowed content: reject the request and ask for a different description; do not generate an image.
- Technical failure: explain that processing failed for a technical reason rather than blaming the spoken description.
- Rejected output: withhold the image and end the attempt without automatically generating another image.
- Interrupted processing: pending jobs can be processed after restart, while interrupted in-progress attempts end with a technical error. Do not automatically repeat image generation whose remote outcome is unknown.
- Keep messages short, in Polish, and actionable.
- Do not provide transcript editing as a recovery path in the MVP.

## Acceptance criteria

| ID | Scenario | Expected result |
| --- | --- | --- |
| AC-01 | A guest records an understandable, permitted Polish description and stops recording. | Processing starts automatically and produces one previewable coloring page without login or transcript confirmation. |
| AC-02 | A coloring page is generated successfully. | The user can download a PNG and open browser printing with a portrait A4-friendly layout. |
| AC-03 | A description contains disallowed content. | The request is rejected before image generation and a Polish message asks for a different description. |
| AC-04 | A recording cannot be understood. | The application asks the user to record a new description. |
| AC-05 | An external service or another technical step fails. | The application reports a technical failure, without incorrectly claiming that the description was unintelligible. |
| AC-06 | An authenticated user successfully generates a coloring page. | The generation automatically appears in that account's history with its thumbnail, date, and transcript. |
| AC-07 | A guest chooses to save the current result and completes email-code verification. | The exact existing generation is associated with the verified account, including an account created in this flow. |
| AC-08 | A user opens a previous generation from history. | The stored image can be viewed, downloaded, and printed without another AI generation call. |
| AC-09 | A generation succeeds for either a guest or an authenticated user. | The server retains the transcript, final prompt, keywords, and image with the appropriate account association or guest status. |
| AC-10 | A processing attempt succeeds, fails, or is rejected. | Its temporary audio is deleted. |
| AC-11 | A user completes the flow on a phone or computer. | Recording controls, processing feedback, result preview, and account controls remain usable in a responsive layout. |
| AC-12 | An output image is rejected, or its required check fails technically. | The image is withheld. The attempt reports the appropriate error and does not automatically generate a replacement. |
| AC-13 | A guest's 24-hour access expires. | The generation remains stored, but that guest session can no longer retrieve or claim it. |
| AC-14 | A restart interrupts an active processing attempt. | The abandoned attempt ends with a technical failure and temporary audio is cleaned up. Pending jobs remain eligible for processing. |
| AC-15 | Seven days pass after login, or the user logs out earlier. | Authenticated access requires a new successful email-code verification. |

## Nonfunctional expectations

- Deploy the application online and support end-to-end use through a web browser.
- Keep the principal flow simple enough for a parent to operate while a child supplies the idea.
- Protect account history and the association of guest results with accounts; concrete controls belong in the architecture specification.
- Provide processing feedback and distinguish content-related failures from technical failures.
- Preserve saved images for repeat downloads and prints without incurring another generation call.
- Evaluate speech recognition, image suitability, and generation latency using real integrations before delivery. No numerical latency or quality target has been approved yet.

## Explicitly outside the MVP

- Generation quotas or limits for guests and authenticated users, including a global generation cap.
- Advertising, payments, subscriptions, and other monetization.
- Languages other than Polish.
- Typed descriptions, transcript confirmation, and transcript editing.
- User-selectable styles or difficulty levels, and image editing.
- Search, folders, renaming, and user-facing deletion of coloring pages.
- Dedicated child accounts.
- Separate PDF export.

Keywords are stored in the MVP, but no search feature is implemented.

## Known risks and accepted tradeoffs

- Public guest generation without limits can result in uncontrolled paid AI calls. Generation limits were explicitly deferred despite this risk.
- Speech recognition can misunderstand children's speech, and generated images can differ from the intended idea. The MVP uses a new recording rather than a text-editing workflow.
- Moderation and output quality depend on the chosen integrations and require validation; perfect detection is not assumed.
- Authentication depends on working email delivery, including during the university demonstration.
- Server-side storage will grow because successful generations, including unclaimed guest results, have no automatic expiry.
- A deployment or restart can fail an active generation; automatic recovery of interrupted image generation is outside the MVP.

## Related architecture

[architecture.md](architecture.md) records the approved technology choices, processing and storage design, authentication protections, deployment, observability, and test strategy. It also identifies remaining decisions and prerequisites, including supported browsers, provider-side data retention, technical processing bounds, and infrastructure details.

This document defines product behavior and scope. The architecture document defines its technical realization. Deferring generation quotas does not defer authentication protections.

Implementation must not start solely from this document. Use the approved architecture baseline, [development-guidelines.md](development-guidelines.md), [agent instructions](../AGENTS.md), and [roadmap.md](roadmap.md). Substantial implementation requires an approved feature specification and resolution of its material prerequisites.
