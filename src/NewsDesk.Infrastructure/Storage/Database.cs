using Microsoft.Data.Sqlite;

namespace NewsDesk.Infrastructure.Storage;

/// <summary>Opens connections and applies transactional schema migrations.</summary>
public sealed class Database
{
    public string Path { get; }
    private readonly string _connectionString;

    public Database(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        Migrate();
    }

    public static string DefaultPath()
    {
        var overridden = Environment.GetEnvironmentVariable("NEWSDESK_DATA");
        var dir = !string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalNewsDesk");
        return System.IO.Path.Combine(dir, "newsdesk.db");
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        using (var wal = c.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }
        using (var init = c.CreateCommand())
        {
            init.CommandText = "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);";
            init.ExecuteNonQuery();
        }
        long current;
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
            current = (long)q.ExecuteScalar()!;
        }
        if (current > Migrations.Length)
            throw new InvalidOperationException($"This database was created by a newer version of the app (schema {current}; this build supports {Migrations.Length}).");
        for (var v = (int)current; v < Migrations.Length; v++)
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[v] + $"\nINSERT INTO schema_version (version) VALUES ({v + 1});";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    // Append new migrations; never edit a released one.
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE projects (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            description TEXT NOT NULL DEFAULT '',
            time_zone TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            schedule_json TEXT NOT NULL,
            retention_days INTEGER NOT NULL DEFAULT 30,
            rule_version_id TEXT NOT NULL,
            next_due_utc TEXT,
            last_run_utc TEXT,
            created_utc TEXT NOT NULL
        );
        CREATE TABLE rule_versions (
            id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            version INTEGER NOT NULL,
            rules_json TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            UNIQUE (project_id, version)
        );
        CREATE TABLE sources (
            id TEXT PRIMARY KEY,
            feed_url TEXT NOT NULL UNIQUE,
            site_url TEXT,
            title TEXT NOT NULL DEFAULT '',
            connector TEXT NOT NULL DEFAULT 'feed',
            health TEXT NOT NULL DEFAULT 'Unknown',
            last_attempt_utc TEXT,
            last_success_utc TEXT,
            last_error TEXT,
            next_retry_utc TEXT,
            etag TEXT,
            last_modified TEXT
        );
        CREATE TABLE project_sources (
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
            priority INTEGER NOT NULL DEFAULT 50,
            is_primary INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (project_id, source_id)
        );
        CREATE TABLE articles (
            id TEXT PRIMARY KEY,
            source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
            publisher_id TEXT,
            url TEXT NOT NULL,
            canonical_url TEXT NOT NULL,
            title TEXT NOT NULL,
            published_utc TEXT,
            updated_utc TEXT,
            discovered_utc TEXT NOT NULL,
            current_revision_id TEXT NOT NULL
        );
        CREATE UNIQUE INDEX ux_articles_publisher ON articles (source_id, publisher_id) WHERE publisher_id IS NOT NULL;
        CREATE UNIQUE INDEX ux_articles_url ON articles (source_id, canonical_url) WHERE publisher_id IS NULL;
        CREATE INDEX ix_articles_canonical ON articles (canonical_url);
        CREATE INDEX ix_articles_rankdate ON articles (source_id, COALESCE(published_utc, updated_utc, discovered_utc));
        CREATE TABLE article_revisions (
            id TEXT PRIMARY KEY,
            article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
            content_hash TEXT NOT NULL,
            capture_level TEXT NOT NULL,
            excerpt TEXT NOT NULL,
            text TEXT NOT NULL,
            captured_utc TEXT NOT NULL,
            UNIQUE (article_id, content_hash)
        );
        CREATE TABLE project_items (
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
            is_read INTEGER NOT NULL DEFAULT 0,
            is_kept INTEGER NOT NULL DEFAULT 0,
            is_dismissed INTEGER NOT NULL DEFAULT 0,
            dismissed_hash TEXT,
            in_inbox INTEGER NOT NULL DEFAULT 0,
            last_delivered_hash TEXT,
            match_reason TEXT NOT NULL DEFAULT '',
            score INTEGER NOT NULL DEFAULT 0,
            first_matched_utc TEXT NOT NULL,
            PRIMARY KEY (project_id, article_id)
        );
        CREATE TABLE project_item_tags (
            project_id TEXT NOT NULL,
            article_id TEXT NOT NULL,
            tag TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY (project_id, article_id, tag),
            FOREIGN KEY (project_id, article_id) REFERENCES project_items(project_id, article_id) ON DELETE CASCADE
        );
        CREATE TABLE collections (
            id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            UNIQUE (project_id, name)
        );
        CREATE TABLE collection_items (
            collection_id TEXT NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
            article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
            added_utc TEXT NOT NULL,
            PRIMARY KEY (collection_id, article_id)
        );
        CREATE TABLE annotations (
            id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
            revision_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            text TEXT NOT NULL DEFAULT '',
            quote TEXT,
            start_offset INTEGER,
            end_offset INTEGER,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        CREATE TABLE runs (
            id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            run_key TEXT NOT NULL UNIQUE,
            rule_version_id TEXT NOT NULL,
            trigger TEXT NOT NULL,
            started_utc TEXT NOT NULL,
            completed_utc TEXT,
            status TEXT NOT NULL,
            summary TEXT NOT NULL DEFAULT '',
            source_outcomes_json TEXT NOT NULL DEFAULT '[]'
        );
        CREATE INDEX ix_runs_project ON runs (project_id, started_utc);
        CREATE TABLE briefing_entries (
            run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
            rank INTEGER NOT NULL,
            article_id TEXT NOT NULL REFERENCES articles(id) ON DELETE CASCADE,
            group_key TEXT,
            score INTEGER NOT NULL,
            reason TEXT NOT NULL,
            components_json TEXT NOT NULL,
            supporting_json TEXT NOT NULL DEFAULT '[]',
            PRIMARY KEY (run_id, rank)
        );
        CREATE VIRTUAL TABLE article_fts USING fts5 (article_id UNINDEXED, title, text, tokenize = 'unicode61 remove_diacritics 2');
        """,
        // v2: retention support
        """
        CREATE TABLE article_tombstones (
            source_id TEXT NOT NULL,
            identity TEXT NOT NULL,
            deleted_utc TEXT NOT NULL,
            PRIMARY KEY (source_id, identity)
        );
        CREATE TABLE app_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE INDEX ix_project_items_first ON project_items (project_id, first_matched_utc);
        CREATE INDEX ix_briefing_article ON briefing_entries (article_id);
        CREATE INDEX ix_collection_items_article ON collection_items (article_id);
        """,
        // v3: structured records (e.g. CISA KEV fields) kept alongside the text, with field-level provenance
        """
        ALTER TABLE article_revisions ADD COLUMN structured_json TEXT;
        """,
    ];

    /// <summary>Schema version this build creates and understands.</summary>
    public static int SchemaVersion => Migrations.Length;

    public int CurrentVersion()
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(q.ExecuteScalar());
    }
}
