CREATE TABLE coloring_app.guest_sessions (
    id uuid PRIMARY KEY,
    token_digest bytea UNIQUE CHECK (octet_length(token_digest) = 32),
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at = created_at + interval '24 hours')
);

CREATE INDEX guest_sessions_expiry ON coloring_app.guest_sessions (expires_at)
    WHERE token_digest IS NOT NULL;

CREATE TABLE coloring_app.generations (
    id uuid PRIMARY KEY,
    guest_session_id uuid NOT NULL REFERENCES coloring_app.guest_sessions (id),
    user_id uuid,
    status text NOT NULL CHECK (status IN ('Pending', 'Processing', 'Succeeded', 'Rejected', 'Failed')),
    created_at timestamptz NOT NULL,
    started_at timestamptz,
    completed_at timestamptz,
    audio_key uuid UNIQUE,
    audio_is_silent boolean NOT NULL DEFAULT false,
    owner_token uuid,
    lease_until timestamptz,
    deadline_at timestamptz,
    error_code text,
    transcript text,
    final_prompt text,
    keywords text[],
    image_key uuid UNIQUE,
    CHECK ((status = 'Processing') = (owner_token IS NOT NULL)),
    CHECK ((status = 'Processing') = (lease_until IS NOT NULL)),
    CHECK ((status = 'Processing') = (deadline_at IS NOT NULL)),
    CHECK (lease_until IS NULL OR lease_until <= deadline_at),
    CHECK (status <> 'Pending' OR started_at IS NULL),
    CHECK (status NOT IN ('Processing', 'Succeeded', 'Rejected') OR started_at IS NOT NULL),
    CHECK ((status IN ('Succeeded', 'Rejected', 'Failed')) = (completed_at IS NOT NULL)),
    CHECK (status NOT IN ('Pending', 'Processing') OR audio_key IS NOT NULL),
    CHECK (audio_key IS NOT NULL OR NOT audio_is_silent),
    CHECK ((status = 'Succeeded') = (transcript IS NOT NULL)),
    CHECK ((status = 'Succeeded') = (final_prompt IS NOT NULL)),
    CHECK ((status = 'Succeeded') = (keywords IS NOT NULL)),
    CHECK ((status = 'Succeeded') = (image_key IS NOT NULL)),
    CHECK (cardinality(keywords) BETWEEN 1 AND 8),
    CHECK (CASE status
        WHEN 'Failed' THEN error_code IS NOT NULL AND error_code IN ('unintelligible_audio', 'generation_failed')
        WHEN 'Rejected' THEN error_code IS NOT NULL AND error_code = 'content_rejected'
        ELSE error_code IS NULL
    END)
);

CREATE INDEX generations_pending ON coloring_app.generations (created_at, id) WHERE status = 'Pending';
CREATE INDEX generations_lease ON coloring_app.generations (lease_until) WHERE status = 'Processing';
CREATE INDEX generations_completion ON coloring_app.generations (completed_at)
    WHERE status IN ('Failed', 'Rejected');
CREATE INDEX generations_terminal_audio ON coloring_app.generations (audio_key)
    WHERE completed_at IS NOT NULL AND audio_key IS NOT NULL;
CREATE INDEX generations_guest ON coloring_app.generations (guest_session_id);
