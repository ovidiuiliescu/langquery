# LangQuery

LangQuery turns a C# solution into a local SQLite knowledge base that you query with SQL.

It is useful for both humans and coding agents, especially when you want repeatable, data-backed answers about a codebase instead of ad-hoc grep scripts.

## What it is (and why agent workflows love it)

- LangQuery scans C# code and stores code facts in SQLite.
- You query stable public views (`v1_*`) plus metadata entities (`meta_*`).
- Queries are read-only (`SELECT`, `WITH`, `EXPLAIN`), so exploration is safe by default.
- Agents can use the same command surface repeatedly, which makes answers more consistent and auditable.
- Saves on tokens, and (past the initial scan) is much faster than grep and regular code exploration.

## Agent-first usage

LangQuery is especially strong when paired with coding agents.

### Generate agent skill files

```bash
langquery installskill codex
```

Other targets: `claude`, `opencode`, `all`.

Sample output (trimmed):

```json
{
  "command": "installskill",
  "success": true,
  "data": {
    "target": "codex",
    "files": [".../.codex/skills/langquery/SKILL.md"]
  }
}
```

Sample screenshots:

![Agent using LangQuery after installskill](docs/images/agent-tool-install-sample.png)

![Agent usage scenario with LangQuery](docs/images/agent-usage-scenario.png)

### Useful agent prompts / inquiries

- "Use LangQuery to list the top 20 longest methods and identify 5 refactor candidates."
- "Use LangQuery to find invocation hotspots in classes inheriting from `ComputationBase`."
- "Use LangQuery to show where `sumOf*` int variables are declared and reused."
- "Use LangQuery to list all local functions/lambdas and map each to its parent method."
- "Use LangQuery to summarize abstract/sealed declarations and potential architecture smells."
- "Use LangQuery to find high-arity methods and list their parameter signatures."

### Recommended agent workflow

1. After any add/edit/delete/rename, prefer a full `langquery scan` (safer and preferred). Use `langquery scan --changed-only` only as an experimental faster path.
2. `langquery installskill <target>` to refresh the skill after an upgrade.
3. Ask the agent to answer with SQL evidence and returned rows.

### Advanced coding-agent example (constraint-heavy)

Prompt:

```text
How many usages of:

- a property whose name ends with "eters"
- whose type name starts with "Sq"

Occur on:

- a line where a Dictionary value or variable is also used
- the dictionary has a value type of long.

Show me the query and each of the usages.
```

Query used by the agent:

```sql
WITH prop_refs AS (
    SELECT
        sr.file_path,
        sr.method_key,
        sr.line_number,
        sr.symbol_name,
        sr.symbol_type_name
    FROM v1_symbol_refs sr
    WHERE sr.symbol_kind = 'Property'
      AND sr.symbol_name LIKE '%eters'
      AND LOWER(COALESCE(sr.symbol_type_name, '')) LIKE 'sq%'
),
dict_vars_on_line AS (
    SELECT DISTINCT
        lv.file_path,
        lv.method_key,
        lv.line_number,
        v.name AS dict_variable_name,
        v.type_name AS dict_variable_type
    FROM v1_line_variables lv
    JOIN v1_variables v ON v.variable_id = lv.variable_id
    WHERE LOWER(COALESCE(v.type_name, '')) LIKE '%dictionary<%, long>%'
       OR LOWER(COALESCE(v.type_name, '')) LIKE '%dictionary<%,long>%'
       OR LOWER(COALESCE(v.type_name, '')) LIKE '%dictionary<%, system.int64>%'
       OR LOWER(COALESCE(v.type_name, '')) LIKE '%dictionary<%,system.int64>%'
)
SELECT
    p.file_path,
    p.line_number,
    m.name AS method_name,
    p.symbol_name AS property_name,
    p.symbol_type_name AS property_type_name,
    d.dict_variable_name,
    d.dict_variable_type,
    l.text AS line_text
FROM prop_refs p
JOIN dict_vars_on_line d
    ON d.file_path = p.file_path
   AND d.line_number = p.line_number
   AND (d.method_key = p.method_key OR (d.method_key IS NULL AND p.method_key IS NULL))
LEFT JOIN v1_methods m ON m.method_key = p.method_key
LEFT JOIN v1_lines l
    ON l.file_path = p.file_path
   AND l.line_number = p.line_number
   AND (l.method_key = p.method_key OR (l.method_key IS NULL AND p.method_key IS NULL))
ORDER BY p.file_path, p.line_number;
```

Sample findings (2 usages found):

- `src/LangQuery.Storage.Sqlite/Storage/SqliteStorageEngine.cs:324` in `InsertMethodsAsync` (`command.Parameters` + `typeIds : IReadOnlyDictionary<string, long>`)
- `src/LangQuery.Storage.Sqlite/Storage/SqliteStorageEngine.cs:379` in `InsertLinesAsync` (`command.Parameters` + `methodIds : IReadOnlyDictionary<string, long>`)

Why this is a strong agent example:

- It translates fuzzy natural-language constraints into explicit SQL predicates.
- It combines symbol refs, variables, methods, and source lines to produce evidence-rich answers.
- It returns both the exact query and concrete line-level usages, so results are auditable and repeatable.

### Advanced coding-agent example (file complexity ranking)

Prompt:

```text
Sort all files by average variable density per line of code. Show results and query.
```

Query used by the agent:

```sql
SELECT
  file_path,
  COUNT(*) AS line_count,
  SUM(variable_count) AS total_variable_mentions,
  ROUND(AVG(CAST(variable_count AS REAL)), 4) AS avg_variable_density
FROM v1_lines
GROUP BY file_path
ORDER BY avg_variable_density DESC, line_count DESC, file_path;
```

Sample results (sorted by average variable density per line):

| file | lines | total vars | avg density |
|---|---:|---:|---:|
| src/LangQuery.Storage.Sqlite/Storage/SqliteStorageEngine.cs | 822 | 596 | 0.7251 |
| src/LangQuery.Core/Services/LangQueryService.cs | 518 | 255 | 0.4923 |
| src/LangQuery.Query/Validation/ReadOnlySqlSafetyValidator.cs | 237 | 116 | 0.4895 |
| src/LangQuery.Roslyn/Extraction/CSharpCodeFactsExtractor.cs | 1089 | 474 | 0.4353 |
| tests/LangQuery.IntegrationTests/CliUsabilityTests.cs | 1228 | 526 | 0.4283 |
| tests/LangQuery.IntegrationTests/EndToEndTests.cs | 700 | 267 | 0.3814 |
| tests/LangQuery.UnitTests/UnitTest1.cs | 931 | 340 | 0.3652 |
| tests/LangQuery.UnitTests/LangQueryServiceTests.cs | 747 | 258 | 0.3454 |
| tests/LangQuery.UnitTests/CSharpCodeFactsExtractorTests.cs | 611 | 152 | 0.2488 |
| tests/LangQuery.UnitTests/ReadOnlySqlSafetyValidatorTests.cs | 169 | 31 | 0.1834 |
| src/LangQuery.Core/Models/Results.cs | 40 | 1 | 0.0250 |
| src/LangQuery.Cli/Program.cs | 1397 | 0 | 0.0000 |
| src/LangQuery.Core/Models/Facts.cs | 83 | 0 | 0.0000 |
| src/LangQuery.Core/Abstractions/IStorageEngine.cs | 26 | 0 | 0.0000 |
| src/LangQuery.Core/Abstractions/ICodeFactsExtractor.cs | 11 | 0 | 0.0000 |
| src/LangQuery.Core/Abstractions/ISqlSafetyValidator.cs | 9 | 0 | 0.0000 |
| src/LangQuery.Core/Models/Options.cs | 8 | 0 | 0.0000 |

Why this is useful:

- It gives a fast, project-wide complexity signal for prioritizing review/refactoring.
- It combines raw size (`line_count`) with density (`avg_variable_density`) for better triage.
- It is deterministic and easy to rerun after a scan to track complexity drift over time.

## Quick useful commands (first)

These assume that `langquery` is already installed (steps to install are described below):

### 1) Index your solution

```bash
langquery scan --pretty
```

Sample output (trimmed):

```json
{
  "command": "scan",
  "success": true,
  "data": {
    "FilesDiscovered": 16,
    "FilesScanned": 16,
    "DatabasePath": ".../.langquery.MySolution.db.sqlite"
  }
}
```

### 2) Find high-risk large methods quickly

```bash
langquery "
SELECT
  file_path,
  name AS method_name,
  (line_end - line_start + 1) AS line_span
FROM v1_methods
ORDER BY line_span DESC
LIMIT 20
"
```

### 3) Spot invocation hotspots

```bash
langquery "
SELECT
  file_path,
  target_name,
  COUNT(*) AS call_count
FROM v1_invocations
GROUP BY file_path, target_name
ORDER BY call_count DESC, file_path, target_name
LIMIT 15
"
```

This is a fast way to find high-traffic calls and likely coupling hotspots.

### 4) Find dense lines that are hard to read

```bash
langquery "
SELECT
  file_path,
  line_number,
  variable_count,
  text
FROM v1_lines
WHERE variable_count >= 3
ORDER BY variable_count DESC, file_path, line_number
LIMIT 25
"
```

Great for quickly identifying lines worth simplification/refactoring.

### 5) Track local functions and lambdas

```bash
langquery "
SELECT
  file_path,
  name,
  implementation_kind,
  parent_method_key
FROM v1_methods
WHERE implementation_kind IN ('LocalFunction', 'Lambda', 'AnonymousMethod')
ORDER BY file_path, implementation_kind, name
"
```

Useful when you need to understand nested behavior boundaries.

### BONUS: Health check

```bash
langquery info --pretty
```

Sample output (trimmed):

```json
{
  "command": "info",
  "success": true,
  "data": {
    "tool": "LangQuery.Cli",
    "version": "1.0.0+...",
    "framework": ".NET 8.0.22"
  }
}
```

### BONUS: Get schema constants for better queries

```bash
langquery simpleschema --pretty
```

Use this when you need exact values like `implementation_kind`, `kind`, or `access_modifier`.
Also provides a full list of queryable tables that you can use in your SQL queries.



## Advanced usage examples

Assume your current directory is a solution folder, or pass `--solution` explicitly.

### Inheritance graph

```bash
langquery "
SELECT
  type_name,
  base_type_name,
  relation_kind
FROM v1_type_inheritances
ORDER BY type_name, base_type_name
"
```

### Invocation hotspots inside a hierarchy

```bash
langquery "
SELECT
  t.name AS class_name,
  i.target_name,
  COUNT(*) AS call_count
FROM v1_invocations i
JOIN v1_methods m ON m.method_id = i.method_id
JOIN v1_types t ON t.type_id = m.type_id
JOIN v1_type_inheritances ti ON ti.type_id = t.type_id
WHERE ti.base_type_name IN ('ComputationBase', 'RevenueCalculator')
GROUP BY t.name, i.target_name
ORDER BY call_count DESC, class_name, target_name
LIMIT 10
"
```

### `sumOf*` integer references by line

```bash
langquery "
SELECT
  l.file_path,
  l.line_number,
  lv.variable_name,
  v.type_name AS variable_type,
  t.name AS class_name
FROM v1_line_variables lv
JOIN v1_variables v ON v.variable_id = lv.variable_id
JOIN v1_lines l ON l.line_id = lv.line_id
JOIN v1_methods m ON m.method_id = l.method_id
JOIN v1_types t ON t.type_id = m.type_id
JOIN v1_type_inheritances ti ON ti.type_id = t.type_id
WHERE lv.variable_name LIKE 'sumOf%'
  AND LOWER(COALESCE(v.type_name, '')) IN ('int', 'int32', 'system.int32')
  AND ti.base_type_name IN ('ComputationBase', 'RevenueCalculator')
ORDER BY l.file_path, l.line_number
"
```

### Nested implementations with parent linkage

```bash
langquery "
SELECT
  file_path,
  name,
  implementation_kind,
  access_modifier,
  parent_method_key
FROM v1_methods
WHERE implementation_kind IN ('LocalFunction', 'Lambda', 'AnonymousMethod')
ORDER BY file_path, implementation_kind, name
"
```

### High-arity methods with parameter signatures

```bash
langquery "
SELECT
  file_path,
  name,
  parameter_count,
  parameters
FROM v1_methods
WHERE implementation_kind IN ('Method', 'Constructor', 'LocalFunction')
ORDER BY parameter_count DESC, file_path, name
LIMIT 25
"
```

### Distinguish property access vs method calls

```bash
langquery "
SELECT
  file_path,
  symbol_name,
  symbol_kind,
  COUNT(*) AS reference_count
FROM v1_symbol_refs
WHERE symbol_kind IN ('Property', 'Method')
GROUP BY file_path, symbol_name, symbol_kind
ORDER BY reference_count DESC, file_path, symbol_name
LIMIT 50
"
```

### Abstract/sealed declarations

```bash
langquery "
SELECT
  file_path,
  name,
  kind,
  access_modifier,
  modifiers
FROM v1_types
WHERE modifiers LIKE '%Abstract%'
   OR modifiers LIKE '%Sealed%'
ORDER BY file_path, name
"
```

## Install (if you do not have `langquery` yet)

Requirements:

- .NET SDK 8.0+

### Option A: install as global tool from this repo (PowerShell)

```powershell
./InstallAsTool.ps1
```

This packs and installs `LangQuery.Cli.Tool` and exposes the `langquery` command.

### Option B: run without global install

```bash
dotnet run --project src/LangQuery.Cli -- help --pretty
```

If you use this mode, replace `langquery ...` examples with:

```bash
dotnet run --project src/LangQuery.Cli -- <command>
```

## Command overview

- `langquery help [--pretty]`
- `langquery info [--pretty]`
- `langquery scan [--solution <folder-or-.sln>] [--db <path>] [--changed-only] [--pretty]`
- `langquery sql --query <sql> [--solution <folder-or-.sln>] [--db <path>] [--max-rows <n>] [--timeout-ms <n>] [--pretty]`
- `langquery schema [--solution <folder-or-.sln>] [--db <path>] [--pretty]`
- `langquery simpleschema [--solution <folder-or-.sln>] [--db <path>] [--pretty]`
- `langquery examples [--pretty]`
- `langquery exportjson [file-name] [--solution <folder-or-.sln>] [--db <path>] [--pretty]`
- `langquery installskill <claude|codex|opencode|all> [--pretty]`

`--changed-only` is experimental; a full `langquery scan` is safer and preferred.

## Arguments and flags reference (each one explained)

### `<sql>` (short query positional argument)

What it is:

- A positional SQL string when you run `langquery "SELECT ..."` without specifying `sql`.

What it does:

- Runs your SQL against the LangQuery DB.
- If the DB does not exist yet, LangQuery scans first.
- Pretty JSON is enabled by default in this short form.

How it looks:

```bash
langquery "
SELECT COUNT(*) AS method_count
FROM v1_methods
"
```

Small sample output:

```json
{
  "command": "sql",
  "success": true,
  "data": { "Columns": ["method_count"], "Rows": [{ "method_count": 259 }] }
}
```

### `--solution <folder-or-.sln>`

What it is:

- Path to either a solution folder or a specific `.sln` file.

What it does:

- Tells LangQuery exactly which solution to scan/query.
- If omitted, current directory is used and must contain exactly one `.sln`.

How it looks:

```bash
langquery scan --solution tests/sample_solution --pretty
```

Small sample output:

```json
{
  "command": "scan",
  "success": true,
  "data": { "DatabasePath": ".../.langquery.SampleSolution.db.sqlite" }
}
```

### `--db <path>`

What it is:

- Custom SQLite DB path.

What it does:

- Lets you control where the LangQuery database is written/read.
- If omitted, default is `<solution-folder>/.langquery.<solution-name>.db.sqlite`.

How it looks:

```bash
langquery scan --solution tests/sample_solution --db .tmp/readme-sample.db --pretty
```

Small sample output:

```json
{
  "command": "scan",
  "success": true,
  "data": { "DatabasePath": ".tmp/readme-sample.db" }
}
```

### `--query <sql>`

What it is:

- Explicit SQL argument for the `sql` command.

What it does:

- Runs the provided SQL (read-only validator enforced).

How it looks:

```bash
langquery sql --query "
SELECT file_path
FROM v1_files
LIMIT 5
" --pretty
```

Small sample output:

```json
{
  "command": "sql",
  "success": true,
  "data": { "Columns": ["file_path"], "Rows": [{ "file_path": ".../Program.cs" }] }
}
```

### `--max-rows <n>`

What it is:

- Row limit guard for SQL results.

What it does:

- Caps returned row count to keep outputs bounded.
- Sets `Truncated: true` when more rows exist.

How it looks:

```bash
langquery sql --query "
SELECT file_path
FROM v1_files
ORDER BY file_path
" --max-rows 1 --pretty
```

Small sample output:

```json
{
  "command": "sql",
  "success": true,
  "data": { "Rows": [{ "file_path": ".../Program.cs" }], "Truncated": true }
}
```

### `--timeout-ms <n>`

What it is:

- Query timeout in milliseconds.

What it does:

- Limits SQL execution time for predictability.

How it looks:

```bash
langquery sql --query "
SELECT COUNT(*) AS method_count
FROM v1_methods
" --timeout-ms 15000 --pretty
```

Small sample output:

```json
{
  "command": "sql",
  "success": true,
  "data": { "Rows": [{ "method_count": 259 }], "Duration": "00:00:00.0018462" }
}
```

### `--changed-only`

What it is:

- Experimental incremental scan flag for `scan`.

What it does:

- Re-indexes only changed files (and tracks unchanged/removed counts).
- Can leave stale metadata in unchanged dependents; run a full `langquery scan` when you need maximum correctness.

How it looks:

```bash
langquery scan --changed-only --pretty
```

Small sample output:

```json
{
  "command": "scan",
  "success": true,
  "data": { "FilesScanned": 0, "FilesUnchanged": 16, "FilesRemoved": 0 }
}
```

### `--pretty`

What it is:

- Global formatting flag.

What it does:

- Pretty-prints JSON output for readability.

How it looks:

```bash
langquery help --pretty
```

Small sample output:

```json
{
  "command": "help",
  "success": true,
  "data": {
    "description": "LangQuery CLI"
  }
}
```

### `[file-name]` (positional argument for `exportjson`)

What it is:

- Optional export path for the JSON dump.

What it does:

- Writes full DB export to that file.
- If omitted, output path defaults to DB name with `.json` extension.

How it looks:

```bash
langquery exportjson .tmp/readme-export.json --pretty
```

Small sample output:

```json
{
  "command": "exportjson",
  "success": true,
  "data": {
    "database_path": ".../.langquery.LangQuery.db.sqlite",
    "export_path": ".../.tmp/readme-export.json",
    "entities": 21
  }
}
```

### `<claude|codex|opencode|all>` (positional target for `installskill`)

What it is:

- Agent target selector for skill generation.

What it does:

- Generates `SKILL.md` files in the target agent folder(s): `.claude`, `.codex`, `.opencode`.

How it looks:

```bash
langquery installskill all --pretty
```

Small sample output:

```json
{
  "command": "installskill",
  "success": true,
  "data": {
    "target": "all",
    "files": [
      ".../.claude/skills/langquery/SKILL.md",
      ".../.codex/skills/langquery/SKILL.md",
      ".../.opencode/skills/langquery/SKILL.md"
    ]
  }
}
```

## Safety model

LangQuery allows only read-oriented top-level SQL:

- `SELECT`
- `WITH`
- `EXPLAIN`

Mutation and DDL statements are rejected.

## Public schema

The public contract is the `v1_*` view set (plus metadata entities). For a full schema guide and additional SQL examples, see `docs/schema.md`.

## Project layout

- `src/LangQuery.Core` contracts, models, orchestration.
- `src/LangQuery.Roslyn` C# fact extraction.
- `src/LangQuery.Storage.Sqlite` SQLite persistence and schema views.
- `src/LangQuery.Query` SQL safety validator.
- `src/LangQuery.Cli` command-line interface.
- `tests/*` unit/integration tests and `tests/sample_solution` fixture.
