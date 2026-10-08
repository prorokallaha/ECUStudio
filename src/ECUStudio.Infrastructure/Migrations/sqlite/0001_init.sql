-- ECUStudio schema v1 (SQLite, standalone desktop)
CREATE TABLE IF NOT EXISTS projects (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    vin         TEXT,
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL,
    document    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_projects_updated_at ON projects (updated_at);
CREATE INDEX IF NOT EXISTS ix_projects_vin ON projects (vin);

CREATE TABLE IF NOT EXISTS file_contents (
    file_id     TEXT PRIMARY KEY,
    sha256      TEXT    NOT NULL,
    size        INTEGER NOT NULL CHECK (size > 0),
    content     BLOB    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_file_contents_sha256 ON file_contents (sha256);

CREATE TABLE IF NOT EXISTS analyses (
    id               TEXT PRIMARY KEY,
    project_id       TEXT NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    analysis_version TEXT NOT NULL,
    created_at       TEXT NOT NULL,
    report           TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_analyses_project ON analyses (project_id, created_at);

CREATE TABLE IF NOT EXISTS candidate_decisions (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id     TEXT    NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    binary_sha256  TEXT    NOT NULL,
    address        INTEGER NOT NULL,
    decision       TEXT    NOT NULL CHECK (decision IN ('confirm', 'reject')),
    role           TEXT,
    note           TEXT,
    decided_at     TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_candidate_decisions_project ON candidate_decisions (project_id, decided_at);

CREATE TABLE IF NOT EXISTS ai_cache (
    key                 TEXT PRIMARY KEY,
    agent               TEXT    NOT NULL,
    model               TEXT    NOT NULL,
    response            TEXT    NOT NULL,
    input_tokens        INTEGER NOT NULL DEFAULT 0,
    output_tokens       INTEGER NOT NULL DEFAULT 0,
    cache_read_tokens   INTEGER NOT NULL DEFAULT 0,
    cache_write_tokens  INTEGER NOT NULL DEFAULT 0,
    created_at          TEXT    NOT NULL
);
