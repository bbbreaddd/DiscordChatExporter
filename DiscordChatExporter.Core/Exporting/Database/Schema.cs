namespace DiscordChatExporter.Core.Exporting.Database;

internal static class Schema
{
    // "user" is quoted throughout because USER, while not reserved in SQLite's own grammar, is
    // reserved in the ANSI SQL standard that most tooling (and humans) assume applies everywhere.
    public const string V1 = """
        CREATE TABLE IF NOT EXISTS guild (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            icon_url TEXT
        );

        CREATE TABLE IF NOT EXISTS channel (
            id INTEGER PRIMARY KEY,
            guild_id INTEGER NOT NULL REFERENCES guild(id),
            kind TEXT NOT NULL,
            category_id INTEGER,
            category TEXT,
            parent_category_id INTEGER,
            parent_category TEXT,
            name TEXT NOT NULL,
            topic TEXT,
            position INTEGER,
            is_archived INTEGER NOT NULL DEFAULT 0,
            last_message_id INTEGER,
            last_exported_at TEXT
        );

        CREATE TABLE IF NOT EXISTS "user" (
            id INTEGER PRIMARY KEY,
            is_bot INTEGER NOT NULL DEFAULT 0,
            discriminator TEXT,
            name TEXT NOT NULL,
            display_name TEXT,
            color TEXT,
            avatar_url TEXT,
            roles_json TEXT
        );

        CREATE TABLE IF NOT EXISTS message (
            id INTEGER PRIMARY KEY,
            channel_id INTEGER NOT NULL REFERENCES channel(id),
            author_id INTEGER NOT NULL REFERENCES "user"(id),
            kind TEXT NOT NULL,
            timestamp TEXT NOT NULL,
            edited_timestamp TEXT,
            call_ended_timestamp TEXT,
            is_pinned INTEGER NOT NULL DEFAULT 0,
            content TEXT NOT NULL,
            reference_json TEXT,
            forwarded_message_json TEXT,
            interaction_json TEXT,
            embeds_json TEXT,
            stickers_json TEXT,
            inline_emojis_json TEXT
        );

        CREATE INDEX IF NOT EXISTS message_channel_ts ON message(channel_id, timestamp);
        CREATE INDEX IF NOT EXISTS message_author ON message(author_id);

        CREATE TABLE IF NOT EXISTS attachment (
            id INTEGER PRIMARY KEY,
            message_id INTEGER NOT NULL REFERENCES message(id) ON DELETE CASCADE,
            url TEXT NOT NULL,
            file_name TEXT NOT NULL,
            file_size_bytes INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS attachment_message ON attachment(message_id);

        CREATE TABLE IF NOT EXISTS reaction (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            message_id INTEGER NOT NULL REFERENCES message(id) ON DELETE CASCADE,
            emoji_id INTEGER,
            emoji_name TEXT NOT NULL,
            emoji_code TEXT,
            emoji_is_animated INTEGER NOT NULL DEFAULT 0,
            emoji_image_url TEXT,
            count INTEGER NOT NULL,
            users_json TEXT
        );

        CREATE INDEX IF NOT EXISTS reaction_message ON reaction(message_id);

        CREATE TABLE IF NOT EXISTS message_mention (
            message_id INTEGER NOT NULL REFERENCES message(id) ON DELETE CASCADE,
            user_id INTEGER NOT NULL,
            PRIMARY KEY (message_id, user_id)
        );

        CREATE INDEX IF NOT EXISTS message_mention_user ON message_mention(user_id);

        CREATE VIRTUAL TABLE IF NOT EXISTS message_fts USING fts5(
            content,
            content = 'message',
            content_rowid = 'id'
        );

        CREATE TRIGGER IF NOT EXISTS message_ai AFTER INSERT ON message BEGIN
            INSERT INTO message_fts(rowid, content) VALUES (new.id, new.content);
        END;

        CREATE TRIGGER IF NOT EXISTS message_ad AFTER DELETE ON message BEGIN
            INSERT INTO message_fts(message_fts, rowid, content) VALUES ('delete', old.id, old.content);
        END;

        CREATE TRIGGER IF NOT EXISTS message_au AFTER UPDATE ON message BEGIN
            INSERT INTO message_fts(message_fts, rowid, content) VALUES ('delete', old.id, old.content);
            INSERT INTO message_fts(rowid, content) VALUES (new.id, new.content);
        END;
        """;

    // Additive only: every statement here either adds a nullable/defaulted column to an existing
    // table or creates a new table/index/trigger, so it's always safe to run against a database
    // that already holds rows written under V1. Note: SQLite's ALTER TABLE ADD COLUMN has no
    // "IF NOT EXISTS" form at all (unlike CREATE TABLE/INDEX/TRIGGER, which do) -- confirmed
    // against the bundled sqlite3, which rejects that syntax outright. That's fine here: the
    // PRAGMA user_version gate in MigrateAsync already guarantees this whole block only ever
    // runs once per database, so idempotency doesn't need to be re-derived at the statement level.
    public const string V2 = """
        ALTER TABLE message ADD COLUMN webhook_id INTEGER;
        ALTER TABLE message ADD COLUMN poll_json TEXT;
        ALTER TABLE message ADD COLUMN components_json TEXT;
        ALTER TABLE message ADD COLUMN deleted_at TEXT;

        ALTER TABLE channel ADD COLUMN nsfw INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE channel ADD COLUMN slowmode_seconds INTEGER;
        ALTER TABLE channel ADD COLUMN bitrate INTEGER;
        ALTER TABLE channel ADD COLUMN user_limit INTEGER;
        ALTER TABLE channel ADD COLUMN permission_overwrites_json TEXT;

        ALTER TABLE "user" ADD COLUMN joined_at TEXT;
        ALTER TABLE "user" ADD COLUMN premium_since TEXT;
        ALTER TABLE "user" ADD COLUMN communication_disabled_until TEXT;
        ALTER TABLE "user" ADD COLUMN pending INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE IF NOT EXISTS role (
            id INTEGER PRIMARY KEY, guild_id INTEGER NOT NULL REFERENCES guild(id),
            name TEXT NOT NULL, color TEXT, position INTEGER NOT NULL,
            permissions TEXT NOT NULL,
            hoist INTEGER NOT NULL DEFAULT 0, mentionable INTEGER NOT NULL DEFAULT 0,
            icon_url TEXT, unicode_emoji TEXT, managed INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS guild_emoji (
            id INTEGER PRIMARY KEY, guild_id INTEGER NOT NULL REFERENCES guild(id),
            name TEXT NOT NULL, is_animated INTEGER NOT NULL DEFAULT 0, image_url TEXT NOT NULL,
            creator_id INTEGER, is_available INTEGER NOT NULL DEFAULT 1, is_managed INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS guild_sticker (
            id INTEGER PRIMARY KEY, guild_id INTEGER NOT NULL REFERENCES guild(id),
            name TEXT NOT NULL, description TEXT, tags TEXT, format TEXT NOT NULL,
            source_url TEXT NOT NULL, creator_id INTEGER, is_available INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS scheduled_event (
            id INTEGER PRIMARY KEY, guild_id INTEGER NOT NULL REFERENCES guild(id),
            channel_id INTEGER, creator_id INTEGER, name TEXT NOT NULL, description TEXT,
            start_time TEXT NOT NULL, end_time TEXT, status TEXT NOT NULL, entity_type TEXT NOT NULL,
            location TEXT, cover_image_url TEXT, user_count INTEGER
        );

        CREATE TABLE IF NOT EXISTS message_edit_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            message_id INTEGER NOT NULL REFERENCES message(id) ON DELETE CASCADE,
            content TEXT NOT NULL, edited_timestamp TEXT,
            recorded_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS message_edit_history_message ON message_edit_history(message_id);

        CREATE TABLE IF NOT EXISTS message_pin_event (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            message_id INTEGER NOT NULL REFERENCES message(id) ON DELETE CASCADE,
            channel_id INTEGER NOT NULL, is_pinned INTEGER NOT NULL,
            recorded_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS message_pin_event_message ON message_pin_event(message_id);

        -- These two triggers are the entire implementation of edit-history and pin-history: they
        -- fire on the existing upsert path (the same one the FTS sync trigger already relies on
        -- for every INSERT ... ON CONFLICT DO UPDATE), so no additional C# write-path changes are
        -- needed for either feature beyond adding the deleted_at/webhook_id/poll/components
        -- columns to the same statement.
        CREATE TRIGGER IF NOT EXISTS message_edit_history_ai AFTER UPDATE OF content ON message
            WHEN OLD.content != NEW.content BEGIN
            INSERT INTO message_edit_history (message_id, content, edited_timestamp, recorded_at)
            VALUES (OLD.id, OLD.content, OLD.edited_timestamp, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
        END;

        CREATE TRIGGER IF NOT EXISTS message_pin_event_ai AFTER UPDATE OF is_pinned ON message
            WHEN OLD.is_pinned != NEW.is_pinned BEGIN
            INSERT INTO message_pin_event (message_id, channel_id, is_pinned, recorded_at)
            VALUES (NEW.id, NEW.channel_id, NEW.is_pinned, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
        END;
        """;

    // Records the --after/--before boundaries used by the run that produced last_message_id, so
    // the live-export skip check can tell a genuine "nothing new" rerun apart from a rerun with a
    // widened date range (e.g. a backfill), which must not be skipped even though LastMessageId
    // hasn't moved.
    public const string V3 = """
        ALTER TABLE channel ADD COLUMN last_export_after INTEGER;
        ALTER TABLE channel ADD COLUMN last_export_before INTEGER;
        """;

    // Same pattern as message_edit_history/message_pin_event in V2: a trigger on the existing
    // upsert path is the entire implementation, so no C# write-path changes are needed. Every
    // CHANNEL_UPDATE/THREAD_UPDATE (and catch-up/scan-missing re-export) already runs the
    // channel upsert that sets the `name` column unconditionally, so a rename is just a row
    // where OLD.name != NEW.name.
    public const string V4 = """
        CREATE TABLE IF NOT EXISTS channel_name_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id INTEGER NOT NULL REFERENCES channel(id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            recorded_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS channel_name_history_channel ON channel_name_history(channel_id);

        CREATE TRIGGER IF NOT EXISTS channel_name_history_ai AFTER UPDATE OF name ON channel
            WHEN OLD.name != NEW.name BEGIN
            INSERT INTO channel_name_history (channel_id, name, recorded_at)
            VALUES (OLD.id, OLD.name, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
        END;
        """;

    public const string V5 = """
        ALTER TABLE guild ADD COLUMN banner_url TEXT;

        CREATE TABLE IF NOT EXISTS media_asset (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            owner_kind TEXT NOT NULL,
            owner_id INTEGER NOT NULL,
            asset_kind TEXT NOT NULL,
            source_url TEXT NOT NULL,
            local_path TEXT NOT NULL,
            is_current INTEGER NOT NULL DEFAULT 1,
            recorded_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS media_asset_owner ON media_asset(owner_kind, owner_id, asset_kind);
        CREATE UNIQUE INDEX IF NOT EXISTS media_asset_current ON media_asset(owner_kind, owner_id, asset_kind)
            WHERE is_current = 1;
        """;

    public const string V6 = """
        CREATE TABLE IF NOT EXISTS media_blob (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            content_hash TEXT NOT NULL,
            size_bytes INTEGER NOT NULL,
            local_path TEXT NOT NULL,
            recorded_at TEXT NOT NULL,
            UNIQUE(content_hash, size_bytes)
        );

        ALTER TABLE media_asset ADD COLUMN blob_id INTEGER REFERENCES media_blob(id);
        CREATE INDEX IF NOT EXISTS media_asset_blob ON media_asset(blob_id);
        """;

    public const string V7 = """
        CREATE TABLE IF NOT EXISTS poll_vote_event (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            message_id INTEGER NOT NULL,
            channel_id INTEGER NOT NULL,
            answer_id INTEGER NOT NULL,
            user_id INTEGER NOT NULL,
            is_added INTEGER NOT NULL,
            recorded_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS poll_vote_event_message ON poll_vote_event(message_id);
        CREATE INDEX IF NOT EXISTS poll_vote_event_user ON poll_vote_event(user_id);
        """;

    public const string V8 = """
        ALTER TABLE message ADD COLUMN components_raw_json TEXT;
        """;

    public const string V9 = """
        ALTER TABLE guild ADD COLUMN premium_tier INTEGER;
        ALTER TABLE guild ADD COLUMN premium_subscription_count INTEGER;
        ALTER TABLE guild ADD COLUMN approximate_member_count INTEGER;

        CREATE TABLE IF NOT EXISTS guild_member_count_snapshot (
            guild_id INTEGER NOT NULL REFERENCES guild(id) ON DELETE CASCADE,
            snapshot_date TEXT NOT NULL,
            approximate_member_count INTEGER NOT NULL,
            recorded_at TEXT NOT NULL,
            PRIMARY KEY (guild_id, snapshot_date)
        );
        """;

    // Records media URLs that returned a permanent "gone" response (404/410) so that a later
    // run (e.g. watchguild --catch-up, which re-enqueues every channel on each gateway READY)
    // doesn't waste a request re-attempting a link that will never come back. Keyed on the
    // *normalized* URL (signature params stripped) so a freshly re-signed CDN link for the same
    // asset still matches. Deliberately does NOT ledger timeouts/5xx/network errors -- those are
    // transient and must be retried, since permanently marking a live link dead during an outage
    // would be silent data loss.
    public const string V10 = """
        CREATE TABLE IF NOT EXISTS media_download_failure (
            url_normalized TEXT PRIMARY KEY,
            status_code INTEGER,
            first_failed_at TEXT NOT NULL,
            last_attempt_at TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 1
        );
        """;

    // Discord role gradient/"holographic" colors (the role's "colors" object). The pre-existing
    // "color" column is unaffected and keeps holding the primary color for solid-colored roles.
    public const string V11 = """
        ALTER TABLE role ADD COLUMN secondary_color TEXT;
        ALTER TABLE role ADD COLUMN tertiary_color TEXT;
        """;

    // Tracks how far a --force-full-scan pass has actually gotten for a channel, separately from
    // last_message_id (which a normal incremental run also advances). Lets a second
    // --force-full-scan invocation -- e.g. resuming after a crash partway through a guild-wide
    // pass -- skip channels it already fully re-walked instead of re-walking them from scratch.
    public const string V12 = """
        ALTER TABLE channel ADD COLUMN force_scanned_message_id INTEGER;
        """;
}
