# Personal News Desk

A Windows desktop application that gathers updates from websites you choose, curates them for named projects, and
presents a manageable news inbox you can read, keep, dismiss, annotate, and export.

**Status:** early build (design phases 1–2). The collection, curation, storage and export engine, a command-line worker,
and the WPF desktop app are working.

## Solution layout

| Project | Purpose |
|---|---|
| `src/NewsDesk.Core` | Domain model, normalization, rule matching, deterministic curation and scoring, schedule calculation. No I/O. |
| `src/NewsDesk.Connectors` | Hardened HTTP client (public-address guard, size caps), RSS/Atom/RDF parser, feed discovery, feed collector. |
| `src/NewsDesk.Infrastructure` | SQLite storage (WAL, FTS5 search, migrations) and `NewsDeskEngine`, which runs collection and briefings. |
| `src/NewsDesk.Exports` | CSV, Markdown, HTML, and plain-text exporters. |
| `src/NewsDesk.Worker` | Headless CLI used by Windows Task Scheduler and for source pilots. |
| `src/NewsDesk.Desktop` | WPF desktop app (`PersonalNewsDesk.exe`). Also runs headless with `--run-due` for the scheduled task. |
| `tests/NewsDesk.Tests` | xUnit tests, including acceptance scenarios A01–A08 from the design. |

## Build and test

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build NewsDesk.sln
dotnet test
```

## Run the desktop app

```powershell
dotnet run --project src/NewsDesk.Desktop
```

Keyboard: F5 refresh · Ctrl+K keep · Ctrl+D dismiss · Ctrl+Z undo dismiss · Ctrl+U toggle read · Ctrl+O open original ·
Ctrl+E export item · Ctrl+S save notes · Ctrl+F search · Ctrl+N new project · Ctrl+H home.

**Background collection** (left sidebar) registers a per-user scheduled task that runs `PersonalNewsDesk.exe --run-due`
every 15 minutes while you are signed in. No administrator rights; closing the window does not stop it.

## Try it from the command line

```powershell
$w = "dotnet run --project src/NewsDesk.Worker --"

# A project: top 5 distinct stories (grouped by CVE) from the last 7 days
iex "$w project-add 'Cyber Briefing' --term 'vulnerability | CVE' --term 'exploit | exploited' --window 168 --stories --schedule weekly:Mon,Tue,Wed,Thu,Fri@07:30"

# Add sources: a website or a feed URL; the app discovers feeds and previews entries
iex "$w source-add 'Cyber Briefing' https://www.cisa.gov/cybersecurity-advisories/all.xml --primary --priority 80"

iex "$w run 'Cyber Briefing'"        # refresh now and print the briefing
iex "$w sources 'Cyber Briefing'"    # source health
iex "$w export 'Cyber Briefing' --format md --out brief.md"
iex "$w run-due"                     # what the scheduled task calls
```

Data lives in `%LOCALAPPDATA%\PersonalNewsDesk\newsdesk.db` (override with `--db` or `NEWSDESK_DATA`).

## How curation works

1. **Hard filters:** include terms (any or all; `"Arcade1Up | A1Up"` lists aliases), exclusions, time window, and dismissals.
   A dismissed item stays hidden until its content changes.
2. **Exact duplicates** removed by canonical URL (tracking parameters stripped) and content hash.
3. **Score 0–100** = 50% relevance + 25% freshness + 15% source priority + 10% direct evidence (primary source).
   Default floor is 50. Ties break by newer date, then stable ID. This is a heuristic, not a probability.
4. **Top N** articles, or N distinct stories grouped by strong identifiers only (currently CVE IDs). Similar titles never merge.
5. Items already delivered and unchanged are not repeated; material updates are labelled. If fewer than N qualify,
   the briefing says why.

Every briefing records its rule version, source outcomes, and whether it was complete, partial, or failed.

## Security notes

- Only http/https. DNS is re-checked for every connection, including redirects; loopback, private, link-local, and
  CGNAT addresses are refused. The system proxy is not used, because it would bypass that check.
- XML DTDs are prohibited. HTML is reduced to plain text before storage or display. Exports encode all content, and CSV
  output neutralizes spreadsheet formulas.
- Access denials stop automatic retries; `Retry-After` is honoured. The app never tries to bypass a block.

## Known gaps / next steps

- Highlights, XLSX export, retention cleanup, backup/restore, run history screen.
- Narrow-window layout (reader as a separate view) and a guided first-run setup.
- Large captures are stored in the database for now. The design calls for a managed content folder.
