using System.Text.Json;
using Microsoft.Data.Sqlite;
using NewsDesk.Core.Collection;
using NewsDesk.Core.Curation;
using NewsDesk.Core.Model;
using static NewsDesk.Infrastructure.Storage.SqlHelpers;

namespace NewsDesk.Infrastructure.Storage;

/// <summary>All persistence. Each public method is one short transaction so the UI and worker can share the database.</summary>
public sealed class NewsDeskStore(Database db)
{
    public Database Database => db;

    // ---------------------------------------------------------------- projects

    public Project CreateProject(Project project, ProjectRules rules, DateTimeOffset nowUtc)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var ruleVersionId = Guid.NewGuid();
        project.RuleVersionId = ruleVersionId;
        project.Rules = rules;
        c.Exec("""
            INSERT INTO projects (id, name, description, time_zone, enabled, schedule_json, retention_days, rule_version_id, next_due_utc, created_utc)
            VALUES ($id, $name, $desc, $tz, $enabled, $schedule, $retention, $rv, $due, $now)
            """, tx, ("$id", project.Id), ("$name", project.Name.Trim()), ("$desc", project.Description), ("$tz", project.TimeZoneId),
            ("$enabled", project.Enabled), ("$schedule", JsonSerializer.Serialize(project.Schedule, Json)), ("$retention", project.RetentionDays),
            ("$rv", ruleVersionId), ("$due", project.NextDueUtc), ("$now", nowUtc));
        InsertRuleVersion(c, tx, project.Id, ruleVersionId, 1, rules, nowUtc);
        tx.Commit();
        return project;
    }

    /// <summary>Saves rules as a new immutable version. Applies to future runs only.</summary>
    public Guid SaveRules(Guid projectId, ProjectRules rules, DateTimeOffset nowUtc)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var q = c.Cmd("SELECT COALESCE(MAX(version), 0) FROM rule_versions WHERE project_id = $p", tx, ("$p", projectId));
        var next = Convert.ToInt32(q.ExecuteScalar()) + 1;
        var id = Guid.NewGuid();
        InsertRuleVersion(c, tx, projectId, id, next, rules, nowUtc);
        c.Exec("UPDATE projects SET rule_version_id = $rv WHERE id = $p", tx, ("$rv", id), ("$p", projectId));
        tx.Commit();
        return id;
    }

    private static void InsertRuleVersion(SqliteConnection c, SqliteTransaction tx, Guid projectId, Guid id, int version, ProjectRules rules, DateTimeOffset now) =>
        c.Exec("INSERT INTO rule_versions (id, project_id, version, rules_json, created_utc) VALUES ($id, $p, $v, $json, $now)", tx,
            ("$id", id), ("$p", projectId), ("$v", version), ("$json", JsonSerializer.Serialize(rules, Json)), ("$now", now));

    public void UpdateProject(Project p)
    {
        using var c = db.Open();
        c.Exec("""
            UPDATE projects SET name = $name, description = $desc, time_zone = $tz, enabled = $enabled, schedule_json = $schedule,
                retention_days = $retention, next_due_utc = $due, last_run_utc = $last WHERE id = $id
            """, null, ("$id", p.Id), ("$name", p.Name.Trim()), ("$desc", p.Description), ("$tz", p.TimeZoneId), ("$enabled", p.Enabled),
            ("$schedule", JsonSerializer.Serialize(p.Schedule, Json)), ("$retention", p.RetentionDays), ("$due", p.NextDueUtc), ("$last", p.LastRunUtc));
    }

    private const string ProjectSelect = """
        SELECT p.*, rv.rules_json FROM projects p JOIN rule_versions rv ON rv.id = p.rule_version_id
        """;

    private static Project MapProject(SqliteDataReader r) => new()
    {
        Id = r.G("id"), Name = r.S("name"), Description = r.S("description"), TimeZoneId = r.S("time_zone"), Enabled = r.B("enabled"),
        Schedule = JsonSerializer.Deserialize<Schedule>(r.S("schedule_json"), Json) ?? new(), RetentionDays = r.I("retention_days"),
        RuleVersionId = r.G("rule_version_id"), Rules = JsonSerializer.Deserialize<ProjectRules>(r.S("rules_json"), Json) ?? new(),
        NextDueUtc = r.ND("next_due_utc"), LastRunUtc = r.ND("last_run_utc"),
    };

    public IReadOnlyList<Project> GetProjects()
    {
        using var c = db.Open();
        return c.Query(ProjectSelect + " ORDER BY p.name", MapProject);
    }

    public Project? GetProject(Guid id)
    {
        using var c = db.Open();
        return c.Query(ProjectSelect + " WHERE p.id = $id", MapProject, null, ("$id", id)).SingleOrDefault();
    }

    public Project? FindProject(string name)
    {
        using var c = db.Open();
        return c.Query(ProjectSelect + " WHERE p.name = $n", MapProject, null, ("$n", name.Trim())).SingleOrDefault();
    }

    public ProjectRules GetRules(Guid ruleVersionId)
    {
        using var c = db.Open();
        return c.Query("SELECT rules_json FROM rule_versions WHERE id = $id",
            r => JsonSerializer.Deserialize<ProjectRules>(r.S("rules_json"), Json)!, null, ("$id", ruleVersionId)).Single();
    }

    public void DeleteProject(Guid id)
    {
        using var c = db.Open();
        c.Exec("DELETE FROM projects WHERE id = $id", null, ("$id", id));
    }

    // ---------------------------------------------------------------- sources

    public Source GetOrAddSource(string feedUrl, string? title, string? siteUrl, string connector = "feed")
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var existing = c.Query("SELECT * FROM sources WHERE feed_url = $u", MapSource, tx, ("$u", feedUrl)).SingleOrDefault();
        if (existing is not null) { tx.Commit(); return existing; }
        var s = new Source { FeedUrl = feedUrl, Title = title ?? new Uri(feedUrl).Host, SiteUrl = siteUrl, Connector = connector };
        c.Exec("INSERT INTO sources (id, feed_url, site_url, title, connector, health) VALUES ($id, $u, $site, $t, $conn, $h)", tx,
            ("$id", s.Id), ("$u", s.FeedUrl), ("$site", s.SiteUrl), ("$t", s.Title), ("$conn", s.Connector), ("$h", s.Health));
        tx.Commit();
        return s;
    }

    public void Subscribe(ProjectSource link)
    {
        using var c = db.Open();
        c.Exec("""
            INSERT INTO project_sources (project_id, source_id, priority, is_primary) VALUES ($p, $s, $pr, $prim)
            ON CONFLICT (project_id, source_id) DO UPDATE SET priority = excluded.priority, is_primary = excluded.is_primary
            """, null, ("$p", link.ProjectId), ("$s", link.SourceId), ("$pr", Math.Clamp(link.Priority, 0, 100)), ("$prim", link.IsPrimary));
    }

    public void Unsubscribe(Guid projectId, Guid sourceId)
    {
        using var c = db.Open();
        c.Exec("DELETE FROM project_sources WHERE project_id = $p AND source_id = $s", null, ("$p", projectId), ("$s", sourceId));
    }

    public IReadOnlyList<(Source Source, ProjectSource Link)> GetProjectSources(Guid projectId)
    {
        using var c = db.Open();
        return c.Query("SELECT s.*, ps.priority, ps.is_primary FROM sources s JOIN project_sources ps ON ps.source_id = s.id WHERE ps.project_id = $p ORDER BY s.title",
            r => (MapSource(r), new ProjectSource(projectId, r.G("id"), r.I("priority"), r.B("is_primary"))), null, ("$p", projectId));
    }

    public IReadOnlyList<Source> GetSources()
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM sources ORDER BY title", MapSource);
    }

    public Source? GetSource(Guid id)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM sources WHERE id = $id", MapSource, null, ("$id", id)).SingleOrDefault();
    }

    public void UpdateSource(Source s)
    {
        using var c = db.Open();
        c.Exec("""
            UPDATE sources SET site_url = $site, title = $t, health = $h, last_attempt_utc = $la, last_success_utc = $ls, last_error = $err,
                next_retry_utc = $nr, etag = $etag, last_modified = $lm WHERE id = $id
            """, null, ("$id", s.Id), ("$site", s.SiteUrl), ("$t", s.Title), ("$h", s.Health), ("$la", s.LastAttemptUtc), ("$ls", s.LastSuccessUtc),
            ("$err", s.LastError), ("$nr", s.NextRetryUtc), ("$etag", s.ETag), ("$lm", s.LastModified));
    }

    private static Source MapSource(SqliteDataReader r) => new()
    {
        Id = r.G("id"), FeedUrl = r.S("feed_url"), SiteUrl = r.NS("site_url"), Title = r.S("title"), Connector = r.S("connector"),
        Health = r.E<SourceHealth>("health"), LastAttemptUtc = r.ND("last_attempt_utc"), LastSuccessUtc = r.ND("last_success_utc"),
        LastError = r.NS("last_error"), NextRetryUtc = r.ND("next_retry_utc"), ETag = r.NS("etag"), LastModified = r.NS("last_modified"),
    };

    // ---------------------------------------------------------------- articles

    /// <summary>
    /// Idempotent ingest. Identity is the publisher ID within a source, else the canonical URL. Changed content creates a new
    /// revision rather than a new article.
    /// </summary>
    public IngestStats Ingest(Guid sourceId, IEnumerable<NormalizedItem> items, DateTimeOffset nowUtc)
    {
        int added = 0, updated = 0, unchanged = 0, skipped = 0;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var item in items)
        {
            var existing = item.PublisherId is not null
                ? c.Query("SELECT id FROM articles WHERE source_id = $s AND publisher_id = $pid", r => r.G("id"), tx, ("$s", sourceId), ("$pid", item.PublisherId))
                : c.Query("SELECT id FROM articles WHERE source_id = $s AND canonical_url = $u AND publisher_id IS NULL", r => r.G("id"), tx, ("$s", sourceId), ("$u", item.CanonicalUrl));

            if (existing.Count == 0)
            {
                // Removed by retention: don't bring it back as if it were new.
                var tomb = c.Query("SELECT 1 AS x FROM article_tombstones WHERE source_id = $s AND identity = $id", r => true, tx,
                    ("$s", sourceId), ("$id", Identity(item.PublisherId, item.CanonicalUrl)));
                if (tomb.Count > 0) { skipped++; continue; }

                var articleId = Guid.NewGuid();
                var revisionId = Guid.NewGuid();
                c.Exec("""
                    INSERT INTO articles (id, source_id, publisher_id, url, canonical_url, title, published_utc, updated_utc, discovered_utc, current_revision_id)
                    VALUES ($id, $s, $pid, $url, $curl, $title, $pub, $upd, $now, $rev)
                    """, tx, ("$id", articleId), ("$s", sourceId), ("$pid", item.PublisherId), ("$url", item.Url), ("$curl", item.CanonicalUrl),
                    ("$title", item.Title), ("$pub", item.PublishedUtc), ("$upd", item.UpdatedUtc), ("$now", nowUtc), ("$rev", revisionId));
                InsertRevision(c, tx, articleId, revisionId, item, nowUtc);
                added++;
                continue;
            }

            var id = existing[0];
            var hasRevision = c.Query("SELECT id FROM article_revisions WHERE article_id = $a AND content_hash = $h", r => r.G("id"), tx,
                ("$a", id), ("$h", item.ContentHash));
            if (hasRevision.Count > 0) { unchanged++; continue; }

            var newRevision = Guid.NewGuid();
            InsertRevision(c, tx, id, newRevision, item, nowUtc);
            c.Exec("UPDATE articles SET title = $t, updated_utc = COALESCE($upd, updated_utc), published_utc = COALESCE(published_utc, $pub), current_revision_id = $rev WHERE id = $id",
                tx, ("$id", id), ("$t", item.Title), ("$upd", item.UpdatedUtc), ("$pub", item.PublishedUtc), ("$rev", newRevision));
            updated++;
        }
        tx.Commit();
        return new IngestStats(added, updated, unchanged, skipped);
    }

    private static void InsertRevision(SqliteConnection c, SqliteTransaction tx, Guid articleId, Guid revisionId, NormalizedItem item, DateTimeOffset now)
    {
        c.Exec("""
            INSERT INTO article_revisions (id, article_id, content_hash, capture_level, excerpt, text, captured_utc, structured_json)
            VALUES ($id, $a, $h, $lvl, $ex, $txt, $now, $json)
            """, tx, ("$id", revisionId), ("$a", articleId), ("$h", item.ContentHash), ("$lvl", item.CaptureLevel), ("$ex", item.Excerpt),
            ("$txt", item.Text), ("$now", now), ("$json", item.StructuredJson));
        c.Exec("DELETE FROM article_fts WHERE article_id = $a", tx, ("$a", articleId));
        c.Exec("INSERT INTO article_fts (article_id, title, text) VALUES ($a, $t, $x)", tx, ("$a", articleId), ("$t", item.Title), ("$x", item.Text));
    }

    /// <summary>Articles from a project's sources whose rank date (published, else updated, else discovered) is on or after <paramref name="sinceUtc"/>.</summary>
    public IReadOnlyList<Candidate> GetCandidates(Guid projectId, DateTimeOffset sinceUtc)
    {
        using var c = db.Open();
        return c.Query("""
            SELECT a.id, a.source_id, a.publisher_id, a.url, a.canonical_url, a.title, a.published_utc, a.updated_utc, a.discovered_utc, a.current_revision_id,
                   r.content_hash, r.capture_level, r.excerpt, r.text, r.captured_utc, r.structured_json,
                   ps.priority, ps.is_primary, src.connector,
                   pi.article_id AS pi_article, pi.is_read, pi.is_kept, pi.is_dismissed, pi.dismissed_hash, pi.in_inbox, pi.last_delivered_hash,
                   pi.match_reason, pi.score, pi.first_matched_utc
            FROM project_sources ps
            JOIN articles a ON a.source_id = ps.source_id
            JOIN article_revisions r ON r.id = a.current_revision_id
            JOIN sources src ON src.id = a.source_id
            LEFT JOIN project_items pi ON pi.project_id = ps.project_id AND pi.article_id = a.id
            WHERE ps.project_id = $p AND COALESCE(a.published_utc, a.updated_utc, a.discovered_utc) >= $since
            """, r =>
        {
            var article = new Article
            {
                Id = r.G("id"), SourceId = r.G("source_id"), PublisherId = r.NS("publisher_id"), Url = r.S("url"), CanonicalUrl = r.S("canonical_url"),
                Title = r.S("title"), PublishedUtc = r.ND("published_utc"), UpdatedUtc = r.ND("updated_utc"), DiscoveredUtc = r.D("discovered_utc"),
                CurrentRevisionId = r.G("current_revision_id"),
            };
            return new Candidate
            {
                Article = article,
                Revision = new ArticleRevision
                {
                    Id = article.CurrentRevisionId, ArticleId = article.Id, ContentHash = r.S("content_hash"), CaptureLevel = r.E<CaptureLevel>("capture_level"),
                    Excerpt = r.S("excerpt"), Text = r.S("text"), CapturedUtc = r.D("captured_utc"), StructuredJson = r.NS("structured_json"),
                },
                SourcePriority = r.I("priority"),
                SourceIsPrimary = r.B("is_primary"),
                SourceConnector = r.S("connector"),
                State = r.NS("pi_article") is null ? null : new ProjectItem
                {
                    ProjectId = projectId, ArticleId = article.Id, IsRead = r.B("is_read"), IsKept = r.B("is_kept"), IsDismissed = r.B("is_dismissed"),
                    DismissedHash = r.NS("dismissed_hash"), InInbox = r.B("in_inbox"), LastDeliveredHash = r.NS("last_delivered_hash"),
                    MatchReason = r.S("match_reason"), Score = r.I("score"), FirstMatchedUtc = r.D("first_matched_utc"),
                },
            };
        }, null, ("$p", projectId), ("$since", sinceUtc));
    }

    // ---------------------------------------------------------------- project items

    private const string ItemSelect = """
        SELECT a.id AS article_id, a.title, a.url, a.published_utc, a.updated_utc, a.discovered_utc, s.title AS source_title,
               r.id AS revision_id, r.capture_level, r.excerpt, r.text, r.content_hash, r.captured_utc,
               pi.is_read, pi.is_kept, pi.is_dismissed, pi.in_inbox, pi.match_reason, pi.score,
               (SELECT group_concat(tag, ',') FROM project_item_tags t WHERE t.project_id = pi.project_id AND t.article_id = pi.article_id) AS tags,
               (SELECT text FROM annotations n WHERE n.project_id = pi.project_id AND n.article_id = pi.article_id AND n.kind = 'note'
                ORDER BY n.updated_utc DESC LIMIT 1) AS note,
               (SELECT json_group_array(json_object('q', quote, 'c', text)) FROM
                  (SELECT h.quote, h.text FROM annotations h WHERE h.project_id = pi.project_id AND h.article_id = pi.article_id
                   AND h.kind = 'highlight' ORDER BY h.created_utc)) AS highlights
        FROM project_items pi
        JOIN articles a ON a.id = pi.article_id
        JOIN article_revisions r ON r.id = a.current_revision_id
        JOIN sources s ON s.id = a.source_id
        """;

    private static ItemRow MapItem(SqliteDataReader r) => new()
    {
        ArticleId = r.G("article_id"), RevisionId = r.G("revision_id"), Title = r.S("title"), Url = r.S("url"), SourceTitle = r.S("source_title"),
        PublishedUtc = r.ND("published_utc"), UpdatedUtc = r.ND("updated_utc"), DiscoveredUtc = r.D("discovered_utc"), CapturedUtc = r.D("captured_utc"),
        CaptureLevel = r.E<CaptureLevel>("capture_level"), Excerpt = r.S("excerpt"), Text = r.S("text"), ContentHash = r.S("content_hash"),
        IsRead = r.B("is_read"), IsKept = r.B("is_kept"), IsDismissed = r.B("is_dismissed"), InInbox = r.B("in_inbox"),
        MatchReason = r.S("match_reason"), Score = r.I("score"),
        Tags = r.NS("tags")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [], Notes = r.NS("note") ?? "",
        Highlights = ParseHighlights(r.NS("highlights")),
    };

    private sealed record HighlightJson(string? Q, string? C);

    private static IReadOnlyList<ExportHighlight> ParseHighlights(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return [];
        return (JsonSerializer.Deserialize<List<HighlightJson>>(json, Json) ?? [])
            .Where(h => h.Q is not null).Select(h => new ExportHighlight(h.Q!, h.C ?? "")).ToList();
    }

    // ---------------------------------------------------------------- highlights

    /// <summary>
    /// Saves a highlight of the current revision's anchor text (full text, else excerpt). The selection is trimmed of
    /// surrounding whitespace. Returns null if the selection is empty, too long, or outside the text.
    /// </summary>
    public HighlightRow? AddHighlight(Guid projectId, Guid articleId, int start, int length, string comment, DateTimeOffset nowUtc)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var current = c.Query("""
            SELECT r.id, r.text, r.excerpt FROM project_items pi JOIN articles a ON a.id = pi.article_id
            JOIN article_revisions r ON r.id = a.current_revision_id WHERE pi.project_id = $p AND pi.article_id = $a
            """, r => (Id: r.G("id"), Text: r.S("text"), Excerpt: r.S("excerpt")), tx, ("$p", projectId), ("$a", articleId)).SingleOrDefault();
        if (current.Id == Guid.Empty) return null;
        var anchorText = current.Text.Length > 0 ? current.Text : current.Excerpt;
        if (Core.Text.HighlightAnchor.Normalize(anchorText, start, length) is not { } sel) return null;

        var id = Guid.NewGuid();
        c.Exec("""
            INSERT INTO annotations (id, project_id, article_id, revision_id, kind, text, quote, start_offset, end_offset, created_utc, updated_utc)
            VALUES ($id, $p, $a, $rev, 'highlight', $comment, $quote, $start, $end, $now, $now)
            """, tx, ("$id", id), ("$p", projectId), ("$a", articleId), ("$rev", current.Id), ("$comment", comment.Trim()),
            ("$quote", sel.Quote), ("$start", sel.Start), ("$end", sel.Start + sel.Quote.Length), ("$now", nowUtc));
        tx.Commit();
        return new HighlightRow(id, current.Id, sel.Quote, sel.Start, comment.Trim(), nowUtc, sel.Start);
    }

    /// <summary>Highlights for an item, each located in the current text (or marked stale if its quote no longer occurs).</summary>
    public IReadOnlyList<HighlightRow> GetHighlights(Guid projectId, Guid articleId)
    {
        using var c = db.Open();
        var anchorText = c.Query("""
            SELECT r.text, r.excerpt FROM articles a JOIN article_revisions r ON r.id = a.current_revision_id WHERE a.id = $a
            """, r => r.S("text").Length > 0 ? r.S("text") : r.S("excerpt"), null, ("$a", articleId)).SingleOrDefault() ?? "";
        return c.Query("""
            SELECT id, revision_id, quote, start_offset, text, created_utc FROM annotations
            WHERE project_id = $p AND article_id = $a AND kind = 'highlight' ORDER BY start_offset, created_utc
            """, r =>
        {
            var quote = r.S("quote");
            var start = r.I("start_offset");
            return new HighlightRow(r.G("id"), r.G("revision_id"), quote, start, r.S("text"), r.D("created_utc"),
                Core.Text.HighlightAnchor.Resolve(anchorText, quote, start));
        }, null, ("$p", projectId), ("$a", articleId));
    }

    public void SetHighlightComment(Guid projectId, Guid highlightId, string comment, DateTimeOffset nowUtc)
    {
        using var c = db.Open();
        c.Exec("UPDATE annotations SET text = $t, updated_utc = $now WHERE id = $id AND project_id = $p AND kind = 'highlight'", null,
            ("$t", comment.Trim()), ("$now", nowUtc), ("$id", highlightId), ("$p", projectId));
    }

    public void DeleteHighlight(Guid projectId, Guid highlightId)
    {
        using var c = db.Open();
        c.Exec("DELETE FROM annotations WHERE id = $id AND project_id = $p AND kind = 'highlight'", null, ("$id", highlightId), ("$p", projectId));
    }

    public IReadOnlyList<ItemRow> GetItems(Guid projectId, ItemView view, string? search = null, int limit = 500)
    {
        var filter = view switch
        {
            ItemView.Inbox => "pi.in_inbox = 1 AND pi.is_dismissed = 0",
            ItemView.AllMatches => "pi.is_dismissed = 0",
            ItemView.Kept => "pi.is_kept = 1",
            ItemView.Dismissed => "pi.is_dismissed = 1",
            _ => "1 = 1",
        };
        var fts = FtsQuery(search);
        var sql = ItemSelect + $" WHERE pi.project_id = $p AND {filter}"
                  + (fts is null ? "" : " AND a.id IN (SELECT article_id FROM article_fts WHERE article_fts MATCH $q)")
                  + " ORDER BY COALESCE(a.published_utc, a.updated_utc, a.discovered_utc) DESC LIMIT $limit";
        using var c = db.Open();
        return c.Query(sql, MapItem, null, ("$p", projectId), ("$q", fts), ("$limit", limit));
    }

    public ItemRow? GetItem(Guid projectId, Guid articleId)
    {
        using var c = db.Open();
        return c.Query(ItemSelect + " WHERE pi.project_id = $p AND pi.article_id = $a", MapItem, null, ("$p", projectId), ("$a", articleId)).SingleOrDefault();
    }

    /// <summary>Quotes each word so user input cannot inject FTS syntax. Words are combined with AND.</summary>
    internal static string? FtsQuery(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var words = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => "\"" + w.Replace("\"", "\"\"") + "\"");
        return string.Join(" ", words);
    }

    public void SetRead(Guid projectId, Guid articleId, bool read) => SetFlag(projectId, articleId, "is_read", read);

    public void SetKept(Guid projectId, Guid articleId, bool kept) => SetFlag(projectId, articleId, "is_kept", kept);

    private void SetFlag(Guid projectId, Guid articleId, string column, bool value)
    {
        using var c = db.Open();
        c.Exec($"UPDATE project_items SET {column} = $v WHERE project_id = $p AND article_id = $a", null, ("$v", value), ("$p", projectId), ("$a", articleId));
    }

    /// <summary>Hides the item in this project only. The current content hash is recorded so unchanged content stays hidden.</summary>
    public void Dismiss(Guid projectId, Guid articleId)
    {
        using var c = db.Open();
        c.Exec("""
            UPDATE project_items SET is_dismissed = 1, in_inbox = 0,
                dismissed_hash = (SELECT r.content_hash FROM articles a JOIN article_revisions r ON r.id = a.current_revision_id WHERE a.id = $a)
            WHERE project_id = $p AND article_id = $a
            """, null, ("$p", projectId), ("$a", articleId));
    }

    public void Restore(Guid projectId, Guid articleId)
    {
        using var c = db.Open();
        c.Exec("UPDATE project_items SET is_dismissed = 0, dismissed_hash = NULL, in_inbox = 1 WHERE project_id = $p AND article_id = $a",
            null, ("$p", projectId), ("$a", articleId));
    }

    public void SetTags(Guid projectId, Guid articleId, IEnumerable<string> tags)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Exec("DELETE FROM project_item_tags WHERE project_id = $p AND article_id = $a", tx, ("$p", projectId), ("$a", articleId));
        foreach (var tag in tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            c.Exec("INSERT INTO project_item_tags (project_id, article_id, tag) VALUES ($p, $a, $t)", tx, ("$p", projectId), ("$a", articleId), ("$t", tag));
        tx.Commit();
    }

    /// <summary>One free-text note per project item, tied to the revision it was written against.</summary>
    public void SetNote(Guid projectId, Guid articleId, string text, DateTimeOffset nowUtc)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var existing = c.Query("SELECT id FROM annotations WHERE project_id = $p AND article_id = $a AND kind = 'note'", r => r.G("id"), tx,
            ("$p", projectId), ("$a", articleId));
        if (existing.Count > 0)
            c.Exec("UPDATE annotations SET text = $t, updated_utc = $now WHERE id = $id", tx, ("$t", text), ("$now", nowUtc), ("$id", existing[0]));
        else
            c.Exec("""
                INSERT INTO annotations (id, project_id, article_id, revision_id, kind, text, created_utc, updated_utc)
                SELECT $id, $p, $a, current_revision_id, 'note', $t, $now, $now FROM articles WHERE id = $a
                """, tx, ("$id", Guid.NewGuid()), ("$p", projectId), ("$a", articleId), ("$t", text), ("$now", nowUtc));
        tx.Commit();
    }

    private static string Identity(string? publisherId, string canonicalUrl) =>
        publisherId is not null ? "pid:" + publisherId : "url:" + canonicalUrl;

    // ---------------------------------------------------------------- retention

    public const int MinimumArticleAgeDays = 30;
    public const int KeepRecentRunsPerProject = 10;
    public const int TombstoneDays = 365;

    /// <summary>
    /// Applies retention in one transaction:
    /// 1. Project items older than the project's retention period are removed unless kept, dismissed, annotated, tagged,
    ///    or in a collection. Dismissed items stay so unchanged content remains hidden.
    /// 2. Briefings older than the retention period are removed, except each project's most recent ten.
    /// 3. Stored articles that no project or retained briefing references are removed once older than the longest
    ///    retention period (at least 30 days). A small identity record prevents them being re-added as new.
    /// Kept items, notes, tags, and collections are never removed.
    /// </summary>
    public RetentionStats ApplyRetention(DateTimeOffset nowUtc)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var items = 0;
        var runs = 0;
        var projects = c.Query("SELECT id, retention_days FROM projects", r => (Id: r.G("id"), Days: Math.Max(1, r.I("retention_days"))), tx);
        foreach (var (id, days) in projects)
        {
            var cutoff = nowUtc.AddDays(-days);
            items += c.Exec("""
                DELETE FROM project_items
                WHERE project_id = $p AND is_kept = 0 AND is_dismissed = 0 AND first_matched_utc < $cutoff
                  AND article_id IN (SELECT id FROM articles WHERE COALESCE(published_utc, updated_utc, discovered_utc) < $cutoff)
                  AND NOT EXISTS (SELECT 1 FROM annotations n WHERE n.project_id = project_items.project_id AND n.article_id = project_items.article_id)
                  AND NOT EXISTS (SELECT 1 FROM project_item_tags t WHERE t.project_id = project_items.project_id AND t.article_id = project_items.article_id)
                  AND NOT EXISTS (SELECT 1 FROM collection_items ci JOIN collections col ON col.id = ci.collection_id
                                  WHERE col.project_id = project_items.project_id AND ci.article_id = project_items.article_id)
                """, tx, ("$p", id), ("$cutoff", cutoff));

            runs += c.Exec("""
                DELETE FROM runs WHERE project_id = $p AND started_utc < $cutoff
                  AND id NOT IN (SELECT id FROM runs WHERE project_id = $p ORDER BY started_utc DESC LIMIT $keep)
                """, tx, ("$p", id), ("$cutoff", nowUtc.AddDays(-Math.Max(days, MinimumArticleAgeDays))), ("$keep", KeepRecentRunsPerProject));
        }

        var articleDays = Math.Max(MinimumArticleAgeDays, projects.Count == 0 ? 0 : projects.Max(p => p.Days));
        const string orphanFilter = """
            discovered_utc < $cutoff
            AND NOT EXISTS (SELECT 1 FROM project_items pi WHERE pi.article_id = articles.id)
            AND NOT EXISTS (SELECT 1 FROM briefing_entries be WHERE be.article_id = articles.id)
            AND NOT EXISTS (SELECT 1 FROM collection_items ci WHERE ci.article_id = articles.id)
            AND NOT EXISTS (SELECT 1 FROM annotations n WHERE n.article_id = articles.id)
            """;
        var articleCutoff = nowUtc.AddDays(-articleDays);
        c.Exec($"""
            INSERT OR REPLACE INTO article_tombstones (source_id, identity, deleted_utc)
            SELECT source_id, CASE WHEN publisher_id IS NOT NULL THEN 'pid:' || publisher_id ELSE 'url:' || canonical_url END, $now
            FROM articles WHERE {orphanFilter}
            """, tx, ("$cutoff", articleCutoff), ("$now", nowUtc));
        c.Exec($"DELETE FROM article_fts WHERE article_id IN (SELECT id FROM articles WHERE {orphanFilter})", tx, ("$cutoff", articleCutoff));
        var articles = c.Exec($"DELETE FROM articles WHERE {orphanFilter}", tx, ("$cutoff", articleCutoff));
        var tombs = c.Exec("DELETE FROM article_tombstones WHERE deleted_utc < $t", tx, ("$t", nowUtc.AddDays(-TombstoneDays)));

        SetMeta(c, tx, "last_retention_utc", Iso(nowUtc)!);
        tx.Commit();
        return new RetentionStats(items, articles, runs, tombs);
    }

    // ---------------------------------------------------------------- app metadata and storage

    public string? GetMeta(string key)
    {
        using var c = db.Open();
        return c.Query("SELECT value FROM app_meta WHERE key = $k", r => r.S("value"), null, ("$k", key)).SingleOrDefault();
    }

    public void SetMeta(string key, string value)
    {
        using var c = db.Open();
        SetMeta(c, null, key, value);
    }

    private static void SetMeta(SqliteConnection c, SqliteTransaction? tx, string key, string value) =>
        c.Exec("INSERT INTO app_meta (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value", tx, ("$k", key), ("$v", value));

    public DateTimeOffset? GetMetaTime(string key) =>
        GetMeta(key) is { } v && DateTimeOffset.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    public StorageStats GetStorageStats()
    {
        using var c = db.Open();
        var stats = c.Query("""
            SELECT (SELECT COUNT(*) FROM projects) AS projects, (SELECT COUNT(*) FROM sources) AS sources,
                   (SELECT COUNT(*) FROM articles) AS articles, (SELECT COUNT(*) FROM project_items WHERE is_kept = 1) AS kept,
                   (SELECT COUNT(*) FROM collections) AS collections, (SELECT COUNT(*) FROM annotations) AS notes
            """, r => (r.I("projects"), r.I("sources"), r.I("articles"), r.I("kept"), r.I("collections"), r.I("notes"))).Single();
        long bytes = 0;
        foreach (var suffix in new[] { "", "-wal" })
            if (File.Exists(db.Path + suffix)) bytes += new FileInfo(db.Path + suffix).Length;
        return new StorageStats(stats.Item1, stats.Item2, stats.Item3, stats.Item4, stats.Item5, stats.Item6, bytes);
    }

    // ---------------------------------------------------------------- collections

    /// <summary>Creates a project-owned collection. Names are unique per project, ignoring case.</summary>
    public CollectionRow CreateCollection(Guid projectId, string name, DateTimeOffset nowUtc)
    {
        name = ValidateCollectionName(name);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        EnsureCollectionNameFree(c, tx, projectId, name, null);
        var id = Guid.NewGuid();
        c.Exec("INSERT INTO collections (id, project_id, name, created_utc) VALUES ($id, $p, $n, $now)", tx,
            ("$id", id), ("$p", projectId), ("$n", name), ("$now", nowUtc));
        tx.Commit();
        return new CollectionRow(id, projectId, name, 0, nowUtc);
    }

    public void RenameCollection(Guid collectionId, string name)
    {
        name = ValidateCollectionName(name);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var projectId = c.Query("SELECT project_id FROM collections WHERE id = $id", r => r.G("project_id"), tx, ("$id", collectionId)).Single();
        EnsureCollectionNameFree(c, tx, projectId, name, collectionId);
        c.Exec("UPDATE collections SET name = $n WHERE id = $id", tx, ("$n", name), ("$id", collectionId));
        tx.Commit();
    }

    /// <summary>Deletes the grouping only. Articles, notes, and kept state are unaffected.</summary>
    public void DeleteCollection(Guid collectionId)
    {
        using var c = db.Open();
        c.Exec("DELETE FROM collections WHERE id = $id", null, ("$id", collectionId));
    }

    private static string ValidateCollectionName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A collection needs a name.");
        if (name.Length > 120) throw new ArgumentException("Collection names are limited to 120 characters.");
        return name;
    }

    private static void EnsureCollectionNameFree(SqliteConnection c, SqliteTransaction tx, Guid projectId, string name, Guid? except)
    {
        var clash = c.Query("SELECT id FROM collections WHERE project_id = $p AND lower(name) = lower($n)", r => r.G("id"), tx,
            ("$p", projectId), ("$n", name)).Any(id => id != except);
        if (clash) throw new InvalidOperationException($"This project already has a collection named “{name}”.");
    }

    public IReadOnlyList<CollectionRow> GetCollections(Guid projectId)
    {
        using var c = db.Open();
        return c.Query("""
            SELECT c.id, c.project_id, c.name, c.created_utc, COUNT(ci.article_id) AS n
            FROM collections c LEFT JOIN collection_items ci ON ci.collection_id = c.id
            WHERE c.project_id = $p GROUP BY c.id ORDER BY c.name COLLATE NOCASE
            """, r => new CollectionRow(r.G("id"), r.G("project_id"), r.S("name"), r.I("n"), r.D("created_utc")), null, ("$p", projectId));
    }

    /// <summary>Adds a project item to one of that project's collections. Adding twice has no effect.</summary>
    public void AddToCollection(Guid projectId, Guid collectionId, Guid articleId, DateTimeOffset nowUtc)
    {
        using var c = db.Open();
        var inserted = c.Exec("""
            INSERT OR IGNORE INTO collection_items (collection_id, article_id, added_utc)
            SELECT c.id, pi.article_id, $now FROM collections c
            JOIN project_items pi ON pi.project_id = c.project_id AND pi.article_id = $a
            WHERE c.id = $c AND c.project_id = $p
            """, null, ("$c", collectionId), ("$a", articleId), ("$p", projectId), ("$now", nowUtc));
        if (inserted == 0 && !IsInCollection(c, collectionId, articleId))
            throw new InvalidOperationException("The item or collection does not belong to this project.");
    }

    private static bool IsInCollection(SqliteConnection c, Guid collectionId, Guid articleId) =>
        c.Query("SELECT 1 AS x FROM collection_items WHERE collection_id = $c AND article_id = $a", r => true, null,
            ("$c", collectionId), ("$a", articleId)).Count > 0;

    public void RemoveFromCollection(Guid collectionId, Guid articleId)
    {
        using var c = db.Open();
        c.Exec("DELETE FROM collection_items WHERE collection_id = $c AND article_id = $a", null, ("$c", collectionId), ("$a", articleId));
    }

    public IReadOnlySet<Guid> GetCollectionIdsForItem(Guid projectId, Guid articleId)
    {
        using var c = db.Open();
        return c.Query("""
            SELECT ci.collection_id FROM collection_items ci JOIN collections c ON c.id = ci.collection_id
            WHERE c.project_id = $p AND ci.article_id = $a
            """, r => r.G("collection_id"), null, ("$p", projectId), ("$a", articleId)).ToHashSet();
    }

    /// <summary>Items in a collection in the order they were added, including dismissed ones.</summary>
    public IReadOnlyList<ItemRow> GetCollectionItems(Guid projectId, Guid collectionId, string? search = null)
    {
        var fts = FtsQuery(search);
        var sql = ItemSelect + " JOIN collection_items ci ON ci.article_id = pi.article_id AND ci.collection_id = $c WHERE pi.project_id = $p"
                  + (fts is null ? "" : " AND a.id IN (SELECT article_id FROM article_fts WHERE article_fts MATCH $q)")
                  + " ORDER BY ci.added_utc";
        using var c = db.Open();
        return c.Query(sql, MapItem, null, ("$p", projectId), ("$c", collectionId), ("$q", fts));
    }

    public ProjectStats GetProjectStats(Guid projectId)
    {
        using var c = db.Open();
        return c.Query("""
            SELECT
              (SELECT COUNT(*) FROM project_items WHERE project_id = $p AND in_inbox = 1 AND is_dismissed = 0 AND is_read = 0) AS unread,
              (SELECT COUNT(*) FROM project_items WHERE project_id = $p AND in_inbox = 1 AND is_dismissed = 0) AS inbox,
              (SELECT COUNT(*) FROM project_items WHERE project_id = $p AND is_kept = 1) AS kept,
              (SELECT COUNT(*) FROM project_sources ps JOIN sources s ON s.id = ps.source_id WHERE ps.project_id = $p) AS sources,
              (SELECT COUNT(*) FROM project_sources ps JOIN sources s ON s.id = ps.source_id
                 WHERE ps.project_id = $p AND s.health NOT IN ('Healthy', 'Unknown')) AS warnings
            """, r => new ProjectStats(r.I("unread"), r.I("inbox"), r.I("kept"), r.I("sources"), r.I("warnings")), null, ("$p", projectId)).Single();
    }

    // ---------------------------------------------------------------- runs and briefings

    /// <summary>Inserts a run unless one with the same key already exists. Returns false for a duplicate key.</summary>
    public bool TryStartRun(RunRecord run)
    {
        using var c = db.Open();
        try
        {
            c.Exec("""
                INSERT INTO runs (id, project_id, run_key, rule_version_id, trigger, started_utc, status)
                VALUES ($id, $p, $key, $rv, $trig, $start, $status)
                """, null, ("$id", run.Id), ("$p", run.ProjectId), ("$key", run.RunKey), ("$rv", run.RuleVersionId), ("$trig", run.Trigger),
                ("$start", run.StartedUtc), ("$status", RunStatus.Running));
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            return false;
        }
    }

    /// <summary>Records curation results atomically: project memberships, delivery marks, briefing entries, and run status.</summary>
    public void CompleteRun(Guid runId, Guid projectId, RunStatus status, string summary, IReadOnlyList<SourceOutcome> outcomes,
        CurationResult? result, DateTimeOffset nowUtc)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        if (result is not null)
        {
            var selectedIds = result.Selections.SelectMany(s => s.Supporting.Prepend(s.Lead)).Select(s => s.Candidate.Article.Id).ToHashSet();
            foreach (var m in result.Matches)
            {
                var selected = selectedIds.Contains(m.Candidate.Article.Id);
                c.Exec("""
                    INSERT INTO project_items (project_id, article_id, in_inbox, match_reason, score, first_matched_utc, last_delivered_hash)
                    VALUES ($p, $a, $inbox, $reason, $score, $now, $delivered)
                    ON CONFLICT (project_id, article_id) DO UPDATE SET
                        match_reason = excluded.match_reason,
                        score = excluded.score,
                        in_inbox = MAX(project_items.in_inbox, excluded.in_inbox),
                        last_delivered_hash = COALESCE(excluded.last_delivered_hash, project_items.last_delivered_hash),
                        is_dismissed = CASE WHEN excluded.in_inbox = 1 THEN 0 ELSE project_items.is_dismissed END
                    """, tx, ("$p", projectId), ("$a", m.Candidate.Article.Id), ("$inbox", selected), ("$reason", m.Match.Describe()),
                    ("$score", m.Score.Total), ("$now", nowUtc), ("$delivered", selected ? m.Candidate.Revision.ContentHash : null));
            }
            foreach (var s in result.Selections)
            {
                c.Exec("""
                    INSERT INTO briefing_entries (run_id, rank, article_id, group_key, score, reason, components_json, supporting_json)
                    VALUES ($run, $rank, $a, $g, $score, $reason, $comp, $sup)
                    """, tx, ("$run", runId), ("$rank", s.Rank), ("$a", s.Lead.Candidate.Article.Id), ("$g", s.GroupKey), ("$score", s.GroupScore),
                    ("$reason", s.Reason), ("$comp", JsonSerializer.Serialize(s.Lead.Score, Json)),
                    ("$sup", JsonSerializer.Serialize(s.Supporting.Select(x => x.Candidate.Article.Id), Json)));
            }
        }
        c.Exec("UPDATE runs SET status = $st, completed_utc = $now, summary = $sum, source_outcomes_json = $out WHERE id = $id", tx,
            ("$id", runId), ("$st", status), ("$now", nowUtc), ("$sum", summary), ("$out", JsonSerializer.Serialize(outcomes, Json)));
        tx.Commit();
    }

    private static RunRecord MapRun(SqliteDataReader r) => new()
    {
        Id = r.G("id"), ProjectId = r.G("project_id"), RunKey = r.S("run_key"), RuleVersionId = r.G("rule_version_id"), Trigger = r.S("trigger"),
        StartedUtc = r.D("started_utc"), CompletedUtc = r.ND("completed_utc"), Status = r.E<RunStatus>("status"), Summary = r.S("summary"),
        SourceOutcomes = JsonSerializer.Deserialize<List<SourceOutcome>>(r.S("source_outcomes_json"), Json) ?? [],
    };

    public IReadOnlyList<RunRecord> GetRuns(Guid projectId, int limit = 50)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM runs WHERE project_id = $p ORDER BY started_utc DESC LIMIT $n", MapRun, null, ("$p", projectId), ("$n", limit));
    }

    /// <summary>Most recent finished run (complete, partial, or failed) for a project.</summary>
    public RunRecord? GetLatestBriefingRun(Guid projectId)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM runs WHERE project_id = $p AND status <> 'Running' ORDER BY started_utc DESC LIMIT 1", MapRun, null, ("$p", projectId))
            .SingleOrDefault();
    }

    public IReadOnlyList<BriefingRow> GetBriefing(Guid projectId, Guid runId)
    {
        using var c = db.Open();
        var entries = c.Query("SELECT * FROM briefing_entries WHERE run_id = $r ORDER BY rank",
            r => (Rank: r.I("rank"), ArticleId: r.G("article_id"), Group: r.NS("group_key"), Score: r.I("score"), Reason: r.S("reason"),
                Components: r.S("components_json"), Supporting: JsonSerializer.Deserialize<List<Guid>>(r.S("supporting_json"), Json) ?? []),
            null, ("$r", runId));
        var rows = new List<BriefingRow>();
        foreach (var e in entries)
        {
            var lead = c.Query(ItemSelect + " WHERE pi.project_id = $p AND pi.article_id = $a", MapItem, null, ("$p", projectId), ("$a", e.ArticleId)).SingleOrDefault();
            if (lead is null) continue;
            var supporting = e.Supporting
                .Select(id => c.Query(ItemSelect + " WHERE pi.project_id = $p AND pi.article_id = $a", MapItem, null, ("$p", projectId), ("$a", id)).SingleOrDefault())
                .OfType<ItemRow>().ToList();
            rows.Add(new BriefingRow(e.Rank, lead, supporting, e.Group, e.Score, e.Reason, e.Components));
        }
        return rows;
    }
}
