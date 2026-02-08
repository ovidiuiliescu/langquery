# LangQuery Schema Reference

LangQuery stores internal data in private tables and exposes a stable public query surface via `v1_*` views.

## Metadata tables

- `meta_schema_version` current schema version and applied timestamp.
- `meta_capabilities` runtime metadata such as SQL mode and language support.
- `meta_scan_state` latest scan details (time, scanned files, removed files).

## Public views (`v1_*`)

- `v1_files` indexed files and content hashes.
- `v1_types` extracted type declarations (`kind`, `access_modifier`, `modifiers`).
- `v1_type_inheritances` extracted type inheritance and interface implementation edges.
- `v1_methods` extracted implementation declarations (`Method`, `Constructor`, `LocalFunction`, `Lambda`, `AnonymousMethod`) plus `parameters` and `parameter_count`.
- `v1_lines` per-line facts (method mapping, nesting depth, variable count).
- `v1_variables` variable declarations per method.
- `v1_line_variables` line-to-variable usage mappings.
- `v1_invocations` method invocation expressions and targets.
- `v1_symbol_refs` symbol references with coarse kind mapping (`Variable`, `Method`, `Property`, `Identifier`) plus optional `symbol_container_type_name` and `symbol_type_name` from Roslyn semantic resolution.

## Example queries

### Count indexed files

```sql
SELECT COUNT(*) AS file_count
FROM v1_files;
```

### Methods with deepest nesting

```sql
SELECT
    method_key,
    method_name,
    MAX(block_depth_in_method) AS max_depth
FROM v1_lines
WHERE method_key IS NOT NULL
GROUP BY method_key, method_name
ORDER BY max_depth DESC, method_key;
```

### Abstract/virtual surface area

```sql
SELECT
    file_path,
    name,
    kind,
    access_modifier,
    modifiers
FROM v1_types
WHERE modifiers LIKE '%Abstract%'
   OR modifiers LIKE '%Sealed%'
ORDER BY file_path, name;
```

### Nested implementations with parent linkage

```sql
SELECT
    file_path,
    name,
    implementation_kind,
    access_modifier,
    parent_method_key
FROM v1_methods
WHERE implementation_kind IN ('LocalFunction', 'Lambda', 'AnonymousMethod')
ORDER BY file_path, implementation_kind, name;
```

### Methods with largest parameter lists

```sql
SELECT
    file_path,
    name,
    parameter_count,
    parameters
FROM v1_methods
WHERE implementation_kind IN ('Method', 'Constructor', 'LocalFunction')
ORDER BY parameter_count DESC, file_path, name
LIMIT 25;
```

### Inheritance edges

```sql
SELECT
    type_full_name,
    base_type_name,
    relation_kind
FROM v1_type_inheritances
ORDER BY type_full_name, base_type_name;
```

### Lines with many variable usages

```sql
SELECT
    file_path,
    line_number,
    variable_count,
    text
FROM v1_lines
WHERE variable_count >= 3
ORDER BY variable_count DESC, file_path, line_number;
```

### Most frequently invoked targets

```sql
SELECT
    target_name,
    COUNT(*) AS invocation_count
FROM v1_invocations
GROUP BY target_name
ORDER BY invocation_count DESC, target_name;
```

### Property access vs method call references

```sql
SELECT
    file_path,
    symbol_name,
    symbol_kind,
    COUNT(*) AS reference_count
FROM v1_symbol_refs
WHERE symbol_kind IN ('Property', 'Method')
GROUP BY file_path, symbol_name, symbol_kind
ORDER BY reference_count DESC, file_path, symbol_name;
```

### Property references filtered by underlying type prefix

```sql
SELECT COUNT(*) AS sqlite_parameter_property_refs
FROM v1_symbol_refs
WHERE symbol_kind = 'Property'
  AND symbol_name = 'Parameters'
  AND symbol_type_name LIKE 'Sqlite%';
```

### Variables grouped by method

```sql
SELECT
    method_key,
    method_name,
    COUNT(*) AS variable_count
FROM v1_variables
GROUP BY method_key, method_name
ORDER BY variable_count DESC, method_key;
```

### Lines with 3+ variables, including typed `sumOf*` usage, in derived classes

```sql
SELECT
    l.file_path,
    l.line_number,
    l.text,
    t.full_name AS class_name
FROM v1_lines l
JOIN v1_methods m ON m.method_id = l.method_id
JOIN v1_types t ON t.type_id = m.type_id
JOIN v1_type_inheritances ti ON ti.type_id = t.type_id
JOIN v1_line_variables lv ON lv.line_id = l.line_id
JOIN v1_variables v ON v.variable_id = lv.variable_id
WHERE ti.base_type_name = 'ComputationBase'
GROUP BY l.line_id, l.file_path, l.line_number, l.text, t.full_name
HAVING COUNT(DISTINCT lv.variable_name) >= 3
   AND SUM(
       CASE
           WHEN v.name LIKE 'sumOf%'
             AND LOWER(COALESCE(v.type_name, '')) IN ('int', 'int32', 'system.int32')
           THEN 1 ELSE 0
       END
   ) >= 1
ORDER BY l.file_path, l.line_number;
```

## Safety model

The CLI enforces read-only SQL by default. Allowed top-level statements are:

- `SELECT`
- `WITH`
- `EXPLAIN`

Mutation and DDL statements are rejected by the validator.
