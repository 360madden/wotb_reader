namespace WotBTreader.Storage.Sqlite;

internal sealed record SqliteMigration(int Version, string Name, string Sql);

internal static class SqliteMigrations
{
    public static IReadOnlyList<SqliteMigration> All { get; } =
    [
        new(
            1,
            "initial_evidence_schema",
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at_utc TEXT NOT NULL
            ) STRICT;

            CREATE TABLE source_artifacts (
                id TEXT PRIMARY KEY,
                sha256 TEXT NOT NULL UNIQUE CHECK(length(sha256) = 64),
                byte_length INTEGER NOT NULL CHECK(byte_length >= 0),
                media_type TEXT NOT NULL,
                stored_extension TEXT NOT NULL,
                imported_at_utc TEXT NOT NULL,
                schema_version TEXT NOT NULL
            ) STRICT;

            CREATE TABLE decode_runs (
                id TEXT PRIMARY KEY,
                source_artifact_id TEXT NOT NULL REFERENCES source_artifacts(id),
                decoder_id TEXT NOT NULL,
                decoder_version TEXT NOT NULL,
                schema_version TEXT NOT NULL,
                status INTEGER NOT NULL,
                capabilities INTEGER NOT NULL,
                started_at_utc TEXT NOT NULL,
                completed_at_utc TEXT,
                failure_code TEXT,
                failure_summary TEXT
            ) STRICT;

            CREATE TABLE battle_sessions (
                id TEXT PRIMARY KEY,
                decode_run_id TEXT NOT NULL UNIQUE REFERENCES decode_runs(id),
                game_version TEXT NOT NULL,
                arena_identity TEXT,
                map_id TEXT,
                map_name TEXT,
                battle_time_utc TEXT,
                duration_ticks INTEGER,
                viewpoint_participant_id TEXT,
                schema_version TEXT NOT NULL
            ) STRICT;

            CREATE TABLE participants (
                id TEXT PRIMARY KEY,
                battle_session_id TEXT NOT NULL REFERENCES battle_sessions(id),
                account_id INTEGER,
                entity_id INTEGER,
                team_number INTEGER,
                player_name TEXT,
                clan_tag TEXT,
                vehicle_compact_descriptor INTEGER,
                tank_id TEXT,
                tank_name TEXT,
                tank_class INTEGER NOT NULL,
                bot_status INTEGER NOT NULL,
                bot_status_confidence INTEGER NOT NULL,
                evidence_source_artifact_id TEXT NOT NULL REFERENCES source_artifacts(id),
                evidence_archive_entry TEXT,
                evidence_offset INTEGER NOT NULL,
                evidence_length INTEGER NOT NULL,
                evidence_sha256 TEXT NOT NULL CHECK(length(evidence_sha256) = 64)
            ) STRICT;

            CREATE TABLE position_samples (
                id TEXT PRIMARY KEY,
                battle_session_id TEXT NOT NULL REFERENCES battle_sessions(id),
                participant_id TEXT,
                entity_id INTEGER,
                sequence INTEGER NOT NULL,
                replay_time_ticks INTEGER NOT NULL,
                raw_x REAL NOT NULL,
                raw_y REAL NOT NULL,
                raw_z REAL NOT NULL,
                normalized_x REAL,
                normalized_y REAL,
                raw_coordinate_space INTEGER NOT NULL,
                normalized_coordinate_space INTEGER,
                evidence_source_artifact_id TEXT NOT NULL REFERENCES source_artifacts(id),
                evidence_archive_entry TEXT,
                evidence_offset INTEGER NOT NULL,
                evidence_length INTEGER NOT NULL,
                evidence_sha256 TEXT NOT NULL CHECK(length(evidence_sha256) = 64),
                UNIQUE(battle_session_id, sequence)
            ) STRICT;

            CREATE TABLE canonical_events (
                id TEXT PRIMARY KEY,
                decode_run_id TEXT NOT NULL REFERENCES decode_runs(id),
                battle_session_id TEXT NOT NULL REFERENCES battle_sessions(id),
                sequence INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                replay_time_ticks INTEGER NOT NULL,
                participant_id TEXT,
                entity_id INTEGER,
                values_json TEXT NOT NULL,
                confidence INTEGER NOT NULL,
                evidence_source_artifact_id TEXT NOT NULL REFERENCES source_artifacts(id),
                evidence_archive_entry TEXT,
                evidence_offset INTEGER NOT NULL,
                evidence_length INTEGER NOT NULL,
                evidence_sha256 TEXT NOT NULL CHECK(length(evidence_sha256) = 64),
                UNIQUE(decode_run_id, sequence)
            ) STRICT;

            CREATE TABLE raw_records (
                id TEXT PRIMARY KEY,
                decode_run_id TEXT NOT NULL REFERENCES decode_runs(id),
                ordinal INTEGER NOT NULL,
                record_kind TEXT NOT NULL,
                replay_time_ticks INTEGER,
                evidence_source_artifact_id TEXT NOT NULL REFERENCES source_artifacts(id),
                evidence_archive_entry TEXT,
                evidence_offset INTEGER NOT NULL,
                evidence_length INTEGER NOT NULL,
                evidence_sha256 TEXT NOT NULL CHECK(length(evidence_sha256) = 64),
                properties_json TEXT,
                UNIQUE(decode_run_id, ordinal)
            ) STRICT;

            CREATE TABLE decode_warnings (
                decode_run_id TEXT NOT NULL REFERENCES decode_runs(id),
                ordinal INTEGER NOT NULL,
                warning TEXT NOT NULL,
                PRIMARY KEY(decode_run_id, ordinal)
            ) STRICT;

            CREATE TABLE comparison_runs (
                id TEXT PRIMARY KEY,
                left_source_artifact_id TEXT NOT NULL REFERENCES source_artifacts(id),
                right_source_artifact_id TEXT NOT NULL REFERENCES source_artifacts(id),
                comparator_id TEXT NOT NULL,
                comparator_version TEXT NOT NULL,
                schema_version TEXT NOT NULL,
                timestamp_window_ticks INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL
            ) STRICT;

            CREATE TABLE comparison_items (
                id TEXT PRIMARY KEY,
                comparison_run_id TEXT NOT NULL REFERENCES comparison_runs(id),
                sequence INTEGER NOT NULL,
                classification INTEGER NOT NULL,
                event_type TEXT NOT NULL,
                left_replay_time_ticks INTEGER,
                right_replay_time_ticks INTEGER,
                participant_identity TEXT,
                field TEXT,
                left_value TEXT,
                right_value TEXT,
                explanation TEXT NOT NULL,
                UNIQUE(comparison_run_id, sequence)
            ) STRICT;
            """),
        new(
            2,
            "query_indexes",
            """
            CREATE INDEX IF NOT EXISTS ix_decode_runs_source_started
                ON decode_runs(source_artifact_id, started_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_decode_runs_started
                ON decode_runs(started_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_participants_session
                ON participants(battle_session_id);
            CREATE INDEX IF NOT EXISTS ix_positions_session_time
                ON position_samples(battle_session_id, replay_time_ticks, sequence);
            CREATE INDEX IF NOT EXISTS ix_events_session_time
                ON canonical_events(battle_session_id, replay_time_ticks, sequence);
            CREATE INDEX IF NOT EXISTS ix_raw_records_run
                ON raw_records(decode_run_id, ordinal);
            CREATE INDEX IF NOT EXISTS ix_comparison_items_run
                ON comparison_items(comparison_run_id, sequence);
            """),
        new(
            3,
            "replay_clock_segments",
            """
            CREATE TABLE replay_clock_segments (
                id TEXT PRIMARY KEY,
                battle_session_id TEXT NOT NULL REFERENCES battle_sessions(id),
                sequence INTEGER NOT NULL CHECK(sequence >= 0),
                source_anchor_utc TEXT NOT NULL,
                replay_anchor_ticks INTEGER NOT NULL CHECK(replay_anchor_ticks >= 0),
                speed REAL NOT NULL CHECK(speed > 0),
                source INTEGER NOT NULL,
                uncertainty_ticks INTEGER NOT NULL CHECK(uncertainty_ticks >= 0),
                created_at_utc TEXT NOT NULL,
                UNIQUE(battle_session_id, sequence)
            ) STRICT;

            CREATE INDEX ix_replay_clock_segments_session_anchor
                ON replay_clock_segments(
                    battle_session_id,
                    source_anchor_utc,
                    replay_anchor_ticks);
            """),
    ];
}
