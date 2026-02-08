# New Tests Implementation Plan

This plan expands test coverage for LangQuery with a focus on:
- richer fixture realism (`tests/sample_solution`),
- CLI/scan behavior tied to real `.sln` + `.csproj` structure,
- extractor/storage/service edge cases not currently validated,
- regression protection for both happy paths and failure paths.

## Progress Tracking

- [x] Created implementation plan document.
- [x] Updated `tests/sample_solution/SampleSolution.sln` to a real project-backed solution layout.
- [x] Added `tests/sample_solution/src/Sample.App/Sample.App.csproj`.
- [x] Added advanced fixture sources:
  - `tests/sample_solution/src/Sample.App/AdvancedForecastEngine.cs`
  - `tests/sample_solution/src/Sample.App/FileScopedSignal.cs`
  - `tests/sample_solution/src/Sample.App/ModifierPlayground.cs`
- [x] Added ReadOnlySqlSafetyValidator unit tests.
- [x] Added SqliteStorageEngine unit tests.
- [x] Added CSharpCodeFactsExtractor unit tests.
- [x] Added LangQueryService orchestration unit tests.
- [x] Added EndToEnd integration tests for new fixture entities/behaviors.
- [x] Updated CLI integration tests for non-empty sample solution and added temp-empty-sln coverage.
- [x] Run full `dotnet test LangQuery.sln` and fix regressions.

## Completion Snapshot

- New tests added exceed the 50+ target (current suite now passes with 112 tests total).
- Sample fixture now uses a real `.sln` + `.csproj` layout.
- Existing tests that previously depended on an empty solution were preserved by adding a temp empty-solution test path.

## Post-Commit Bug Hardening (Top 15)

- [x] 1. Untrusted project evaluation can execute arbitrary code.
- [x] 2. `--db` can corrupt non-LangQuery SQLite databases.
- [x] 3. Unknown options are silently ignored.
- [x] 4. Missing option values degrade into flags and unsafe defaults.
- [x] 5. SQL duplicate column names overwrite row values.
- [x] 6. Project references can escape solution root.
- [x] 7. `exportjson` loads everything in memory.
- [x] 8. Migrations are not atomic.
- [x] 9. Missing project file aborts whole solution scan.
- [x] 10. Nested namespaces are flattened incorrectly.
- [x] 11. Method default access inside class nested in interface is wrong.
- [x] 12. Nested type default access inside interfaces is wrong.
- [x] 13. Variable resolution ignores lexical scope boundaries.
- [x] 14. Ignored directory filtering is case-sensitive.
- [x] 15. `timeout-ms` is rounded to whole seconds.

## Fixture Enhancements

1. Update `tests/sample_solution/SampleSolution.sln` to include a real project entry (`src/Sample.App/Sample.App.csproj`).
2. Add `tests/sample_solution/src/Sample.App/Sample.App.csproj` so solution/project discovery uses real MSBuild items.
3. Add advanced fixture source files with additional language constructs:
   - constructors,
   - record structs,
   - enums,
   - interface inheritance chains,
   - file-scoped types (`file` modifier),
   - `try/catch` and `foreach` variables,
   - events/indexers/generic helper methods,
   - extra modifier/access combinations (`protected internal`, `private protected`, etc.).

## New Test Matrix (50+ new tests)

### A) ReadOnlySqlSafetyValidator (14 new tests)

1. Rejects empty SQL string.
2. Rejects whitespace-only SQL string.
3. Rejects comment-only SQL (line comments).
4. Rejects comment-only SQL (block comments).
5. Accepts trailing semicolon + trailing whitespace.
6. Accepts `EXPLAIN QUERY PLAN SELECT ...`.
7. Rejects disallowed first keyword (`BEGIN`).
8. Rejects disallowed first keyword (`PRAGMA`) with mixed casing.
9. Rejects mutation token `INSERT`.
10. Rejects mutation token `UPDATE`.
11. Rejects mutation token `DELETE`.
12. Rejects mutation token `DROP`.
13. Accepts escaped single quotes inside string literals.
14. Accepts forbidden words inside quoted identifiers/strings/comments.

### B) SqliteStorageEngine (16 new tests)

1. `InitializeAsync` creates parent directory for DB path.
2. `InitializeAsync` is idempotent and keeps schema version stable.
3. `GetIndexedFileHashesAsync` returns empty dictionary after init.
4. `PersistFactsAsync` full rebuild inserts file rows.
5. `PersistFactsAsync` full rebuild replaces previous files.
6. `PersistFactsAsync` changed-only removes deleted paths.
7. `PersistFactsAsync` updates existing file hash on upsert.
8. `PersistFactsAsync` stores `meta_scan_state.scanned_files`.
9. `PersistFactsAsync` stores `meta_scan_state.removed_files`.
10. `PersistFactsAsync` persists type inheritance rows.
11. `PersistFactsAsync` persists nested method parent key.
12. `PersistFactsAsync` persists symbol container/type columns.
13. `ExecuteReadOnlyQueryAsync` maps `NULL` DB values to `null`.
14. `ExecuteReadOnlyQueryAsync` enforces minimum `MaxRows` of 1.
15. `ExecuteReadOnlyQueryAsync` row dictionary is case-insensitive.
16. `DescribeSchemaAsync` includes metadata entities (`meta_scan_state`, etc.).

### C) CSharpCodeFactsExtractor (18 new tests)

1. Extracts constructor with `ImplementationKind=Constructor` and `ReturnType=ctor`.
2. Interface method default access resolves to `Public`.
3. Top-level type with no modifier defaults to `Internal`.
4. Nested type with no modifier defaults to `Private`.
5. Captures `file` access modifier for file-scoped type.
6. Captures `private protected` method access.
7. Captures `protected internal` method access.
8. Captures `foreach` variable kind and type.
9. Captures `catch` variable kind and type.
10. Captures interface inheritance relation as `BaseInterface`.
11. Captures struct interface relation as `Interface`.
12. Captures record declaration kind as `Record`.
13. Captures enum declaration kind as `Enum`.
14. Captures nested full type names (`Outer.Inner`).
15. Captures method parameter modifiers (`ref`, `out`, `in`, `params`) in signature.
16. Resolves field reference symbol kind as `Property` with type metadata.
17. Resolves event reference symbol kind as `Property` with type metadata.
18. Resolves variable shadowing to nearest declaration line.

### D) LangQueryService orchestration (12 new tests)

1. `QueryAsync` initializes storage before executing query.
2. `GetSchemaAsync` initializes storage before describing schema.
3. `ScanAsync` full rebuild sets `fullRebuild=true` in persist call.
4. `ScanAsync` changed-only sets `fullRebuild=false` in persist call.
5. `ScanAsync` changed-only scans only changed files.
6. `ScanAsync` changed-only reports unchanged count.
7. `ScanAsync` changed-only reports removed files from old index.
8. `ScanAsync` directory scan ignores `bin` directories.
9. `ScanAsync` directory scan ignores `obj` directories.
10. `ScanAsync` directory scan ignores `.git` directories.
11. `ScanAsync` file path input scans containing folder.
12. `ScanAsync` skips extractor-ineligible files while preserving discovery count.

### E) End-to-end integration (12 new tests)

1. Real sample solution scan indexes expected project files via `.sln`/`.csproj`.
2. Query returns constructor rows from fixture (`v1_methods`).
3. Query returns record kind rows from fixture (`v1_types.kind='Record'`).
4. Query returns enum kind rows from fixture (`v1_types.kind='Enum'`).
5. Query returns file-scoped type access modifier (`File`).
6. Query returns interface inheritance edges with `BaseInterface`.
7. Query returns `foreach` and `catch` variable kinds in `v1_variables`.
8. Query returns symbol refs for event/field property-like references.
9. Changed-only scan rescans modified source file and reports one scanned file.
10. Changed-only scan includes newly added source file in scanned count.
11. Changed-only scan preserves unchanged rows and updates hash for changed file.
12. Query rejected for mutation SQL at service layer with read-only error.

### F) CLI usability/regression (14 new tests)

1. Explicit sample `.sln` now indexes project files (non-zero `v1_files`).
2. Explicit empty `.sln` (temp fixture) yields zero indexed files (legacy expectation preserved via temp setup).
3. `sql` fails when `--query` missing.
4. `sql` fails for invalid `--max-rows` value.
5. `sql` fails for invalid `--timeout-ms` value.
6. Unknown command returns failure and help payload.
7. `scan --solution` with nonexistent path returns clear error.
8. `scan --solution` with non-`.sln` file returns clear error.
9. `installskill` rejects unsupported option.
10. `installskill` rejects unsupported flag.
11. `installskill opencode` writes `.opencode/skills/langquery/SKILL.md` only.
12. `schema` with explicit `--db` succeeds even when `--solution` omitted.
13. Short-form SQL with trailing options parses query correctly.
14. No-args invocation returns `help` success payload.

## Execution Notes

- Any previous tests relying on empty `tests/sample_solution/SampleSolution.sln` will be rewritten to use a temp empty solution fixture.
- Assertions will favor behavior invariants over brittle hardcoded totals when fixture size grows.
- Full validation run after implementation: `dotnet test LangQuery.sln`.
