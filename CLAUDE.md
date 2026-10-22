# Shiny EF Core Extensions — Working Notes

Guidance for maintaining this repo. Code lives in `src/`, tests in `tests/`, the published Claude Code
skill in `skills/`, and the public documentation site in a **separate** repo at
`~/Desktop/dev/documentation` (rendered to https://shinylib.net/efcore).

Feature packages for EF Core 10 (`net10.0` only), ported from Shiny.DocumentDb
(`~/Desktop/dev/DocumentDb`). Each package is independent — there is no shared core assembly.

| Package | What it does |
|---|---|
| `Shiny.EntityFrameworkCore.Sqlite.Mobile` | Spatial (R*Tree + NetTopologySuite, no `mod_spatialite`), sqlite-vec vectors, FTS5 full-text — works on iOS/Android |
| `Shiny.EntityFrameworkCore.Outbox` | Transactional outbox: enqueue in the SaveChanges transaction, background processor, dead letters |
| `Shiny.EntityFrameworkCore.Temporal` | Provider-agnostic history: History / AsOf / AsOfAll / Restore / diffs / ChangesByActor |
| `Shiny.EntityFrameworkCore.AI` | Microsoft.Extensions.AI tools over a DbContext with non-removable per-entity scopes |
| `Shiny.EntityFrameworkCore.Mcp` | MCP server over the AI tools |
| `Shiny.EntityFrameworkCore.Seeding` | Versioned run-once seeders |
| `Shiny.EntityFrameworkCore.Aspire.Hosting` | AppHost seed gate (first-time setup or destructive recreate; marker lives in the database) |

## Code sharing with DocumentDb

Engine code was **ported** (copied and adapted), not shared. Extracting common packages is a later step once
both sides are stable — don't add a reference to any `Shiny.DocumentDb*` package.

## Conventions

- Central package management (`Directory.Packages.props`). Library floor is EF Core 10.0.7 /
  Microsoft.Extensions 10.0.7; test projects may `VersionOverride` higher (the MySQL provider,
  `MySql.EntityFrameworkCore`, needs EF 10.0.12).
- Optional behavior is added through **extension methods over public EF hooks** (interceptors,
  `IDbContextOptionsExtension`, model conventions/annotations, migrations SQL generators) — never by
  requiring users to subclass a Shiny `DbContext`.
- Don't put internals in a `*.EntityFrameworkCore.*.Internal` namespace — EF's EF1001 analyzer flags it.
- No `continue` in C# loops — restructure with combined conditions.

## Removing or replacing code — no leftover cruft

This is an open-source library; keep the tree clean. When a member, type, or package is removed or
replaced, **delete it outright** — do not leave `[Obsolete]` shims, deprecated no-op methods, forwarding
stubs, dead flags, or "kept for back-compat" wrappers behind. A clean breaking change with a release note
is preferred over accumulating compatibility cruft. Update every call site, test, sample, and doc in the
same change so nothing references the removed thing.

## After every new feature or fix

A change is not "done" until the four artifacts below are in sync. Do all of them in the same
change unless there's a reason not to.

1. **Code + tests** (`src/`, `tests/`)
   - Relational features (Outbox, Temporal, AI, Seeding) must be proven on **SQLite, PostgreSQL,
     SQL Server and MySQL**, or explicitly scoped away from a provider with the scope stated in the
     release note. `Sqlite.Mobile` is SQLite-only by design.
   - **Always run the full suite** (`dotnet test EntityFrameworkCore.slnx`) — not a filtered subset.
   - **The full suite needs Docker** (PostgreSQL / SQL Server / MySQL via Testcontainers). If Docker is
     not running, **do not report success from a subset** — tell the user Docker is off, ask them to
     start it, and only claim "all tests pass" once the full suite has actually run.
   - Tests run on Microsoft.Testing.Platform (xunit v3): pass runner options after `--`
     (e.g. `dotnet test EntityFrameworkCore.slnx -- --report-trx`).

2. **Documentation site** (`~/Desktop/dev/documentation/src/content/docs/efcore/`)
   - Update the relevant feature page.
   - Add a **release note** — see the release-note rules below.
   - Pages are `.mdx`; release notes use the `<RN>` component
     (`import RN from '/src/components/ReleaseNote.astro'`), with `type="feature|enhancement|fix|breaking"`.
   - A brand new page also needs a sidebar entry in `src/sidebar-topics.mjs` in that repo (the
     "EF Core Extensions" group under Data & Storage), and a new package needs a row in the package
     table on `efcore/index.mdx`.

3. **Skill** (`skills/shiny-efcore/SKILL.md`)
   - This is the source of the published `shiny-efcore` Claude Code skill — the agent-facing
     "how to generate correct code" doc. It syncs to `shinyorg/skills` via `sync-skills.yml`.
   - Keep `SKILL.md` aligned with the code. Update the `triggers:` keyword list near the top when a
     new public type / extension method / package is introduced.
   - If the default or recommended pattern changes, the skill's default guidance must change too.

4. **readme.md** (repo root)
   - This file is packed into every NuGet package (`PackageReadmeFile` in `Directory.Build.props`).
     Update the feature list, the package table, and any inline guidance when behavior changes.

## Versioning & CI

- Nerdbank.GitVersioning: `version.json` is `1.0.0-beta.{height}` (SemVer 2 package versions). Work
  happens on `v{major}` branches (currently `v1`); `build.yml` builds on pushes to `v*`, publishes to
  NuGet via trusted publishing (OIDC), and tags `v1.0` / `v1.0.1` mark public releases.
- `build.yml` runs the full test suite on **ubuntu** (Docker for Testcontainers), then packs on
  **macOS** (the iOS sqlite-vec xcframework needs Xcode; the Android `.so` are compiled with the NDK
  for 16 KB page alignment). `build.slnf` lists only the shipping `src/` projects.
- sqlite-vec natives are fetched by `build/build-sqlite-vec.sh` (not committed). Bump the version in
  the Sqlite.Mobile csproj (`SqliteVecVersion`) to upgrade.

## Release notes

Release notes live in the documentation repo at
`~/Desktop/dev/documentation/src/content/docs/efcore/release-notes.mdx`.

**Which version does a note go against?** Use the `version` field in `version.json` — **the raw
version portion only** (strip any prerelease/build-metadata suffix, e.g. `1.0.0-beta.{height}` →
`1.0.0`).

**Heading style — match the existing file.** Feature/minor releases are headed by `major.minor`
(`## 1.1 - June 13, 2026`); patch releases use the full `major.minor.patch` (`## 1.0.2 - May 30,
2026`). Pick the heading that matches the kind of release you're cutting.

**If the version isn't released yet (beta / prerelease, or work-in-progress for the next version):**
- If a `## <version> TBD` heading already exists, **add the note under that existing section**. If
  you're modifying a feature that hasn't shipped yet (already an entry under a `TBD` section), edit
  that existing entry in place rather than adding a duplicate.
- If no section exists for that version yet, **create a new `## <version> TBD` heading** at the top
  and add the note there.

**If the version is a final release**, the section is dated (`## 1.1 - June 13, 2026`); add the note
under the matching dated section (or promote the `TBD` section to a dated one when cutting the
release).

Each note is a single `<RN>` line. Use `type="breaking"` for breaking changes (it's its own note
type here, not a flag). Newest version section stays at the top of the file.

## Blog posts (only when explicitly requested)

Do **not** write blog posts automatically as part of a fix/feature. Follow the blog-post rules in
`~/Desktop/dev/DocumentDb/CLAUDE.md` (docs-site post first, then the personal-blog adaptation with a
hero SVG) when the user asks for one.
