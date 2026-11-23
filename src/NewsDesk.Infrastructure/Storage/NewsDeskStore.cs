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

    public Source GetOrAddSource(string feedUrl, string? title, string? siteUrl)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var existing = c.Query("SELECT * FROM sources WHERE feed_url = $u", MapSource, tx, ("$u", feedUrl)).SingleOrDefault();
        if (existing is not null) { tx.Commit(); return existing; }
        var s = new Source { FeedUrl = feedUrl, Title = title ?? new Uri(feedUrl).Host, SiteUrl = siteUrl };
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
            INSERT INTO article_revisions (id, article_id, content_hash, capture_level, excerpt, text, captured_utc)
            VALUES ($id, $a, $h, $lvl, $ex, $txt, $now)
            """, tx, ("$id", revisionId), ("$a", articleId), ("$h", item.ContentHash), ("$lvl", item.CaptureLevel), ("$ex", item.Excerpt),
            ("$txt", item.Text), ("$now", now));
        c.Exec("DELETE FROM article_fts WHERE article_id = $a", tx, ("$a", articleId));
        c.Exec("INSERT INTO article_fts (article_id, title, text) VALUES ($a, $t, $x)", tx, ("$a", articleId), ("$t", item.Title), ("$x", item.Text));
    }

    /// <summary>Articles from a project's sources whose rank date (published, else updated, else discovered) is on or after <paramref name="sinceUtc"/>.</summary>
    public IReadOnlyList<Candidate> GetCandidates(Guid projectId, DateTimeOffset sinceUtc)
    {
        using var c = db.Open();
        return c.Query("""
            SELECT a.id, a.source_id, a.publisher_id, a.url, a.canonical_url, a.title, a.published_utc, a.updated_utc, a.discovered_utc, a.current_revision_id,
                   r.content_hash, r.capture_level, r.excerpt, r.text, r.captured_utc,
                   ps.priority, ps.is_primary,
                   pi.article_id AS pi_article, pi.is_read, pi.is_kept, pi.is_dismissed, pi.dismissed_hash, pi.in_inbox, pi.last_delivered_hash,
                   pi.match_reason, pi.score, pi.first_matched_utc
            FROM project_sources ps
            JOIN articles a ON a.source_id = ps.source_id
            JOIN article_revisions r ON r.id = a.current_revision_id
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
                    Excerpt = r.S("excerpt"), Text = r.S("text"), CapturedUtc = r.D("captured_utc"),
                },
                SourcePriority = r.I("priority"),
                SourceIsPrimary = r.B("is_primary"),
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
                ORDER BY n.updated_utc DESC LIMIT 1) AS note
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
    };

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
                    """, tx, ("$run", runId), ("$rank", s.Rank), ("$a", s.Lead.Candidate.Article.Id), ("$g", s.GroupKey), ("$score", s.Lead.Score.Total),
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
