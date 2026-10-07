-- ECUStudio schema v1 (PostgreSQL)
CREATE TABLE IF NOT EXISTS projects (
    id          uuid PRIMARY KEY,
    name        text        NOT NULL,
    vin         varchar(17),
    created_at  timestamptz NOT NULL,
    updated_at  timestamptz NOT NULL,
    -- Full project aggregate (files metadata, hardware overrides, headline). Versioned by the app.
    document    jsonb       NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_projects_updated_at ON projects (updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_projects_vin ON projects (vin);

-- Binary content is stored once per file id; the project document references it.
CREATE TABLE IF NOT EXISTS file_contents (
    file_id     uuid PRIMARY KEY,
    sha256      char(64)    NOT NULL,
    size        integer     NOT NULL CHECK (size > 0),
    content     bytea       NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_file_contents_sha256 ON file_contents (sha256);

CREATE TABLE IF NOT EXISTS analyses (
    id               uuid PRIMARY KEY,
    project_id       uuid        NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    analysis_version text        NOT NULL,
    created_at       timestamptz NOT NULL,
    report           jsonb       NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_analyses_project ON analyses (project_id, created_at DESC);

CREATE TABLE IF NOT EXISTS candidate_decisions (
    id             bigserial PRIMARY KEY,
    project_id     uuid        NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    binary_sha256  char(64)    NOT NULL,
    address        integer     NOT NULL,
    decision       text        NOT NULL CHECK (decision IN ('confirm', 'reject')),
    role           text,
    note           text,
    decided_at     timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_candidate_decisions_project ON candidate_decisions (project_id, decided_at);

-- AI response cache: key = hash(binary, stock, vehicle profile, analysis version, agent, context, model).
CREATE TABLE IF NOT EXISTS ai_cache (
    key                 text PRIMARY KEY,
    agent               text        NOT NULL,
    model               text        NOT NULL,
    response            jsonb       NOT NULL,
    input_tokens        bigint      NOT NULL DEFAULT 0,
    output_tokens       bigint      NOT NULL DEFAULT 0,
    cache_read_tokens   bigint      NOT NULL DEFAULT 0,
    cache_write_tokens  bigint      NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL
);
