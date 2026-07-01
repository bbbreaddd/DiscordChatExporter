namespace DiscordChatExporter.Core.Exporting.Database;

internal static class Schema
{
    // "user" is quoted throughout because USER, while not reserved in SQLite's own grammar, is
    // reserved in the ANSI SQL standard that most tooling (and humans) assume applies everywhere.
    public const string CreateStatements = """
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
}
