# LangQuery

LangQuery turns a C# solution into a local SQLite knowledge base that you query with SQL.

## Who it is for

- Humans: ask ad-hoc architecture and code-intelligence questions with concrete SQL evidence.
- Coding agents (recommended): get faster, more deterministic answers than grep-heavy exploration.
- Automation/scripts: reuse proven SQL queries in shell scripts, CI checks, or other utilities.

## What it is (and why agent workflows love it)

- LangQuery scans C# code and stores code facts in SQLite (e.g. what variables are declared where, what types they have, where each method is used, etc)
- This allows you to query your codebase with literal SQL queries.
- It allows agents to quickly answer REALLY complicated questions about your codebase (see [Advanced coding-agent example (constraint-heavy)](#advanced-coding-agent-example-constraint-heavy)).
- Queries are read-only (`SELECT`, `WITH`, `EXPLAIN`), so exploration is safe by default.
- Agents can use the same command surface repeatedly, which makes answers more consistent and auditable.
- Saves on tokens, and (past the initial scan) is much faster than grep and regular code exploration.

## Getting started for AI agents (quick)

```bash
git clone <this-repo-url>
cd langquery
pwsh ./InstallAsTool.ps1
```

Then in the project you want to analyze:

```bash
cd /path/to/your/project
langquery installskill codex # you can replace "codex" with "claude", "opencode" or "all"
```

Then .. just enjoy!

Sample screenshots (taken from OpenCode):

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
FROM (
  SELECT
    l.line_id,
    l.file_path,
    COUNT(DISTINCT lv.variable_id) AS variable_count
  FROM v1_lines l
  LEFT JOIN v1_line_variables lv ON lv.line_id = l.line_id
  GROUP BY l.line_id, l.file_path
) line_density
GROUP BY file_path
ORDER BY avg_variable_density DESC, line_count DESC, file_path;
```

Why this is useful:

- It gives a fast, project-wide complexity signal for prioritizing review/refactoring.
- It combines raw size (`line_count`) with density (`avg_variable_density`) for better triage.
- It is deterministic and easy to rerun after a scan to track complexity drift over time.

### Advanced coding-agent example (top 5 densest files + methodology)

Prompt:

```text
What are the top 5 most densest files (in terms of variable) in the codebase? Tell me how you got the answer.
```

Core SQL logic used:

```sql
WITH file_vars AS (
  SELECT file_id, file_path, COUNT(*) AS variable_count
  FROM v1_variables
  GROUP BY file_id, file_path
),
file_lines AS (
  SELECT file_id, file_path, COUNT(DISTINCT line_number) AS line_count
  FROM v1_lines
  GROUP BY file_id, file_path
)
SELECT
  l.file_path,
  COALESCE(v.variable_count, 0) AS variable_count,
  l.line_count,
  1.0 * COALESCE(v.variable_count, 0) / NULLIF(l.line_count, 0) AS variables_per_line
FROM file_lines l
LEFT JOIN file_vars v ON v.file_id = l.file_id
WHERE l.line_count > 0
ORDER BY variables_per_line DESC, variable_count DESC
LIMIT 5;
```

## Quick useful commands (first)

These assume that `langquery` is already installed (steps to install are described below):

### 1) Index your solution

```bash
langquery scan --pretty
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
  l.file_path,
  l.line_number,
  COUNT(DISTINCT lv.variable_id) AS variable_count,
  l.text
FROM v1_lines l
JOIN v1_line_variables lv ON lv.line_id = l.line_id
GROUP BY l.line_id, l.file_path, l.line_number, l.text
HAVING COUNT(DISTINCT lv.variable_id) >= 3
ORDER BY variable_count DESC, l.file_path, l.line_number
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

### References by line, filtered by reference partial name and type

```bash
langquery "
SELECT
  l.file_path,
  l.line_number,
  v.name AS variable_name,
  v.type_name AS variable_type,
  t.name AS class_name
FROM v1_line_variables lv
JOIN v1_variables v ON v.variable_id = lv.variable_id
JOIN v1_lines l ON l.line_id = lv.line_id
JOIN v1_methods m ON m.method_id = l.method_id
JOIN v1_types t ON t.type_id = m.type_id
JOIN v1_type_inheritances ti ON ti.type_id = t.type_id
WHERE v.name LIKE 'sumOf%'
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
  m.file_path,
  m.name,
  COUNT(v.variable_id) AS parameter_count,
  m.parameters
FROM v1_methods m
LEFT JOIN v1_variables v
  ON v.method_id = m.method_id
 AND v.kind = 'Parameter'
WHERE m.implementation_kind IN ('Method', 'Constructor', 'LocalFunction')
GROUP BY m.method_id, m.file_path, m.name, m.parameters
ORDER BY parameter_count DESC, m.file_path, m.name
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

## Arguments and flags reference

Use this table as a quick lookup for the most important inputs.

| Argument / flag | Used with | What it does | Default / notes | Example |
|---|---|---|---|---|
| `<sql>` (positional) | short form: `langquery "..."` | Runs a read-only SQL query directly (without `sql --query`). | If DB is missing, LangQuery scans first. Pretty JSON is enabled in this short form. | `langquery "SELECT COUNT(*) FROM v1_methods"` |
| `--query <sql>` | `sql` | Runs the provided read-only SQL query. | Equivalent in purpose to positional `<sql>`, but explicit. | `langquery sql --query "SELECT file_path FROM v1_files LIMIT 5"` |
| `--solution <folder-or-.sln>` | `scan`, `sql`, `schema`, `simpleschema`, `exportjson` | Selects the solution folder or `.sln` to use. | If omitted, current directory must resolve to exactly one `.sln`. | `langquery scan --solution tests/sample_solution --pretty` |
| `--db <path>` | `scan`, `sql`, `schema`, `simpleschema`, `exportjson` | Uses a specific SQLite DB path. | Default: `<solution-folder>/.langquery.<solution-name>.db.sqlite`. | `langquery scan --solution tests/sample_solution --db .tmp/readme.db --pretty` |
| `--max-rows <n>` | `sql` | Limits returned rows. | Sets `Truncated: true` when more rows exist. | `langquery sql --query "SELECT file_path FROM v1_files" --max-rows 10 --pretty` |
| `--timeout-ms <n>` | `sql` | Sets SQL execution timeout in milliseconds. | Helps keep queries predictable in automation/agents. | `langquery sql --query "SELECT COUNT(*) FROM v1_methods" --timeout-ms 15000 --pretty` |
| `--changed-only` | `scan` | Performs incremental scan of changed files only. | Experimental; prefer full `langquery scan` for maximum correctness. | `langquery scan --changed-only --pretty` |
| `--pretty` | most commands | Pretty-prints JSON output. | Best for human readability and docs/examples. | `langquery help --pretty` |
| `[file-name]` (positional) | `exportjson` | Output file path for exported JSON. | If omitted, uses DB name with `.json`. | `langquery exportjson .tmp/export.json --pretty` |
| `target` (positional: `claude`, `codex`, `opencode`, or `all`) | `installskill` | Chooses which agent target(s) to generate `SKILL.md` for. | Writes into `.claude`, `.codex`, `.opencode` as applicable. | `langquery installskill all --pretty` |

## Safety model

LangQuery allows only read-oriented top-level SQL:

- `SELECT`
- `WITH`
- `EXPLAIN`

Mutation and DDL statements are rejected.

## Database structure

LangQuery stores internal data in private tables and exposes a stable public query surface through `v1_*` views.

### Metadata tables

| Table | Purpose |
|---|---|
| `meta_schema_version` | Current schema version and when it was applied. |
| `meta_capabilities` | Runtime metadata (for example SQL mode and language support). |
| `meta_scan_state` | Latest scan details (time, scanned files, removed files). |

### Public query views (`v1_*`)

| View | Purpose |
|---|---|
| `v1_files` | Indexed files and content hashes. |
| `v1_types` | Type declarations (`kind`, `access_modifier`, `modifiers`). |
| `v1_type_inheritances` | Inheritance and interface implementation edges. |
| `v1_methods` | Implementations (`Method`, `Constructor`, `LocalFunction`, `Lambda`, `AnonymousMethod`) plus `parameters`. |
| `v1_lines` | Per-line facts (method mapping and nesting depth). |
| `v1_variables` | Variable declarations per method. |
| `v1_line_variables` | Line-to-variable usage mappings. |
| `v1_invocations` | Invocation expressions and targets. |
| `v1_symbol_refs` | Symbol references (`Variable`, `Method`, `Property`, `Identifier`) plus optional semantic type/container fields. |

## Project layout

- `src/LangQuery.Core` contracts, models, orchestration.
- `src/LangQuery.Roslyn` C# fact extraction.
- `src/LangQuery.Storage.Sqlite` SQLite persistence and schema views.
- `src/LangQuery.Query` SQL safety validator.
- `src/LangQuery.Cli` command-line interface.
- `tests/*` unit/integration tests and `tests/sample_solution` fixture.
