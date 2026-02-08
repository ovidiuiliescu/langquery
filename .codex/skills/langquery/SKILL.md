---
name: langquery
description: Query C# codebases with LangQuery using read-only SQL over `v1_*` views and `meta_*` metadata.
---

# LangQuery Skill

## Quick summary
LangQuery indexes a C# solution into a local SQLite database and exposes stable `v1_*` views plus `meta_*` metadata so AI agents can answer codebase questions with read-only SQL.

## Command line parameters (`langquery help --pretty`)
```json
{
  "command": "help",
  "success": true,
  "data": {
    "description": "LangQuery CLI",
    "default_usage": "langquery <sql>",
    "notes": [
      "If '--db' is omitted, the CLI uses '<solution-folder>/.langquery.<solution-name>.db.sqlite'.",
      "If '--solution' is omitted, the current folder is used and exactly one .sln file must be present.",
      "'--changed-only' is experimental for partial/incremental updates; a full 'scan' is safer and preferred.",
      "For SQL queries, if the DB file does not exist, the CLI runs a scan first and then executes the query.",
      "Use 'examples' to print sample SQL queries from simple to advanced scenarios.",
      "Use 'exportjson [file-name]' to rebuild and export the full SQLite database as one JSON file.",
      "Use 'info' to print tool/runtime metadata including the installed version.",
      "Use 'installskill <claude|codex|opencode|all>' to generate reusable AI skill files in the current project.",
      "Short query form ('langquery <sql>') enables pretty JSON output by default."
    ],
    "global_options": [
      {
        "name": "--pretty",
        "description": "Pretty-print JSON payloads."
      }
    ],
    "commands": [
      {
        "name": "scan",
        "usage": "langquery scan [--solution <folder-or-.sln>] [--db <path>] [--changed-only] [--pretty]",
        "description": "Scan C# files and persist extracted facts into SQLite ('--changed-only' is experimental)"
      },
      {
        "name": "sql",
        "usage": "langquery sql --query <sql> [--solution <folder-or-.sln>] [--db <path>] [--max-rows <n>] [--timeout-ms <n>] [--pretty]",
        "description": "Execute read-only SQL against LangQuery views"
      },
      {
        "name": "schema",
        "usage": "langquery schema [--solution <folder-or-.sln>] [--db <path>] [--pretty]",
        "description": "Describe available `v1_*` and `meta_*` entities"
      },
      {
        "name": "simpleschema",
        "usage": "langquery simpleschema [--solution <folder-or-.sln>] [--db <path>] [--pretty]",
        "description": "Describe query-focused schema fields and known constants"
      },
      {
        "name": "help",
        "usage": "langquery help [--pretty]",
        "description": "Show command help"
      },
      {
        "name": "info",
        "usage": "langquery info [--pretty]",
        "description": "Show installed version and environment metadata"
      },
      {
        "name": "installskill",
        "usage": "langquery installskill <claude|codex|opencode|all> [--pretty]",
        "description": "Generate LangQuery skill files for AI coding agents"
      },
      {
        "name": "examples",
        "usage": "langquery examples [--pretty]",
        "description": "Show example SQL queries with explanations"
      },
      {
        "name": "exportjson",
        "usage": "langquery exportjson [file-name] [--solution <folder-or-.sln>] [--db <path>] [--pretty]",
        "description": "Rebuild and export the entire LangQuery database as JSON"
      }
    ]
  }
}
```

## Usage examples (`langquery examples --pretty`)
```json
{
  "command": "examples",
  "success": true,
  "data": [
    {
      "title": "List all indexed files",
      "query": "SELECT file_path, language, indexed_utc FROM v1_files ORDER BY file_path",
      "explanation": "Returns one row per indexed source file with language and indexing timestamp."
    },
    {
      "title": "List all variables",
      "query": "SELECT file_path, method_name, name AS variable_name, kind, type_name, declaration_line FROM v1_variables ORDER BY file_path, method_name, declaration_line",
      "explanation": "Shows every variable declaration with owning file/method and inferred type information."
    },
    {
      "title": "Integer sumOf* variables used in at least two places",
      "query": "SELECT v.file_path, v.method_name, v.name AS variable_name, LOWER(COALESCE(v.type_name, '')) AS normalized_type, COUNT(DISTINCT lv.line_number) AS usage_line_count FROM v1_variables v JOIN v1_line_variables lv ON lv.variable_id = v.variable_id WHERE v.name LIKE 'sumOf%' AND LOWER(COALESCE(v.type_name, '')) IN ('int', 'int32', 'system.int32') GROUP BY v.variable_id, v.file_path, v.method_name, v.name, normalized_type HAVING COUNT(DISTINCT lv.line_number) >= 2 ORDER BY usage_line_count DESC, v.file_path, v.method_name, v.name",
      "explanation": "Finds variables named like 'sumOf*' whose type is integer and that are referenced on at least two distinct lines."
    },
    {
      "title": "Type count by kind",
      "query": "SELECT kind AS type_kind, COUNT(*) AS type_count FROM v1_types GROUP BY kind ORDER BY type_count DESC, type_kind",
      "explanation": "Summarizes how many classes, interfaces, records, and other type kinds are indexed."
    },
    {
      "title": "Methods with highest parameter arity",
      "query": "SELECT file_path, name AS method_name, parameter_count, parameters FROM v1_methods WHERE implementation_kind IN ('Method', 'Constructor', 'LocalFunction') ORDER BY parameter_count DESC, file_path, method_name LIMIT 25",
      "explanation": "Surfaces high-arity methods with parameter signatures so you can spot refactor candidates and verify call-shape assumptions."
    },
    {
      "title": "Abstract and sealed type declarations",
      "query": "SELECT file_path, name AS type_name, access_modifier, modifiers FROM v1_types WHERE modifiers LIKE '%Abstract%' OR modifiers LIKE '%Sealed%' ORDER BY file_path, type_name",
      "explanation": "Finds type declarations carrying key modifiers like abstract or sealed and shows their effective accessibility."
    },
    {
      "title": "Nested implementations (local/lambda/anonymous)",
      "query": "SELECT file_path, name AS implementation_name, implementation_kind, access_modifier, parent_method_key FROM v1_methods WHERE implementation_kind IN ('LocalFunction', 'Lambda', 'AnonymousMethod') ORDER BY file_path, implementation_kind, implementation_name",
      "explanation": "Lists nested implementation forms and their parent method keys so you can trace local behavior boundaries."
    },
    {
      "title": "Longest methods by span",
      "query": "SELECT file_path, name AS method_name, (line_end - line_start + 1) AS line_span FROM v1_methods ORDER BY line_span DESC, file_path, method_name LIMIT 25",
      "explanation": "Highlights large methods by counting source lines from declaration start to end."
    },
    {
      "title": "Most reused variables",
      "query": "SELECT v.file_path, v.method_name, v.name AS variable_name, COUNT(DISTINCT lv.line_number) AS usage_line_count FROM v1_variables v JOIN v1_line_variables lv ON lv.variable_id = v.variable_id GROUP BY v.variable_id, v.file_path, v.method_name, v.name ORDER BY usage_line_count DESC, v.file_path, v.method_name, v.name LIMIT 25",
      "explanation": "Shows variables that appear on the most distinct lines, useful for spotting high-churn state."
    },
    {
      "title": "Invocation hotspots",
      "query": "SELECT file_path, target_name, COUNT(*) AS call_count FROM v1_invocations GROUP BY file_path, target_name ORDER BY call_count DESC, file_path, target_name LIMIT 25",
      "explanation": "Finds the most frequently called targets per file to reveal call concentration points."
    },
    {
      "title": "Property/member access hotspots",
      "query": "SELECT file_path, symbol_name AS member_name, symbol_kind, COUNT(*) AS reference_count FROM v1_symbol_refs WHERE symbol_kind IN ('Property', 'Method') GROUP BY file_path, symbol_name, symbol_kind ORDER BY reference_count DESC, file_path, member_name LIMIT 50",
      "explanation": "Highlights frequently referenced member names and distinguishes property access from method invocations (for example, `Parameters` vs `AddWithValue`)."
    },
    {
      "title": "Variable-dense lines",
      "query": "SELECT file_path, line_number, variable_count, text FROM v1_lines WHERE variable_count >= 3 ORDER BY variable_count DESC, file_path, line_number LIMIT 50",
      "explanation": "Lists lines with heavy variable usage, a useful signal for readability and complexity review."
    }
  ]
}
```

## Best practices
- Re-scan after every code change (add/edit/delete/rename): prefer a full `langquery scan` (safer and preferred). Use `langquery scan --solution <folder-or-.sln> --db <path> --changed-only` only as an experimental faster path.
- Prefer querying `v1_*` views and `meta_*` entities from the public contract instead of private/internal SQLite tables.
- Keep SQL read-only (`SELECT`, `WITH`, `EXPLAIN`) and set `--max-rows`/`--timeout-ms` for predictable results.
- Start with broad discovery queries (`COUNT`, grouped summaries, `LIMIT`) before deep joins.
- Use `langquery simpleschema --solution <folder-or-.sln> --db <path> --pretty` to refresh field names and known constants.

## Current simple schema description (`langquery simpleschema --db <temp-db> --pretty`)
```json
{
  "command": "simpleschema",
  "success": true,
  "data": {
    "SchemaVersion": 5,
    "Entities": [
      {
        "Name": "meta_capabilities",
        "Kind": "table",
        "Columns": [
          {
            "Name": "key",
            "Type": "TEXT"
          },
          {
            "Name": "value",
            "Type": "TEXT"
          }
        ]
      },
      {
        "Name": "meta_scan_state",
        "Kind": "table",
        "Columns": [
          {
            "Name": "key",
            "Type": "TEXT"
          },
          {
            "Name": "value",
            "Type": "TEXT"
          }
        ]
      },
      {
        "Name": "meta_schema_version",
        "Kind": "table",
        "Columns": [
          {
            "Name": "version",
            "Type": "INTEGER"
          },
          {
            "Name": "applied_utc",
            "Type": "TEXT"
          }
        ]
      },
      {
        "Name": "v1_files",
        "Kind": "view",
        "Columns": [
          {
            "Name": "file_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_path",
            "Type": "TEXT"
          },
          {
            "Name": "hash",
            "Type": "TEXT"
          },
          {
            "Name": "language",
            "Type": "TEXT"
          },
          {
            "Name": "indexed_utc",
            "Type": "TEXT"
          }
        ]
      },
      {
        "Name": "v1_invocations",
        "Kind": "view",
        "Columns": [
          {
            "Name": "invocation_id",
            "Type": "INTEGER"
          },
          {
            "Name": "method_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_path",
            "Type": "TEXT"
          },
          {
            "Name": "method_key",
            "Type": "TEXT"
          },
          {
            "Name": "line_number",
            "Type": "INTEGER"
          },
          {
            "Name": "expression",
            "Type": "TEXT"
          },
          {
            "Name": "target_name",
            "Type": "TEXT"
          }
        ]
      },
      {
        "Name": "v1_line_variables",
        "Kind": "view",
        "Columns": [
          {
            "Name": "line_variable_id",
            "Type": "INTEGER"
          },
          {
            "Name": "line_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_path",
            "Type": "TEXT"
          },
          {
            "Name": "method_id",
            "Type": "INTEGER"
          },
          {
            "Name": "method_key",
            "Type": "TEXT"
          },
          {
            "Name": "line_number",
            "Type": "INTEGER"
          },
          {
            "Name": "variable_name",
            "Type": "TEXT"
          },
          {
            "Name": "variable_id",
            "Type": "INTEGER"
          },
          {
            "Name": "variable_key",
            "Type": "TEXT"
          }
        ]
      },
      {
        "Name": "v1_lines",
        "Kind": "view",
        "Columns": [
          {
            "Name": "line_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_path",
            "Type": "TEXT"
          },
          {
            "Name": "method_id",
            "Type": "INTEGER"
          },
          {
            "Name": "method_key",
            "Type": "TEXT"
          },
          {
            "Name": "method_name",
            "Type": "TEXT"
          },
          {
            "Name": "line_number",
            "Type": "INTEGER"
          },
          {
            "Name": "text",
            "Type": "TEXT"
          },
          {
            "Name": "block_depth_in_method",
            "Type": "INTEGER"
          },
          {
            "Name": "variable_count",
            "Type": "INTEGER"
          }
        ]
      },
      {
        "Name": "v1_methods",
        "Kind": "view",
        "Columns": [
          {
            "Name": "method_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_path",
            "Type": "TEXT"
          },
          {
            "Name": "type_id",
            "Type": "INTEGER"
          },
          {
            "Name": "method_key",
            "Type": "TEXT"
          },
          {
            "Name": "name",
            "Type": "TEXT"
          },
          {
            "Name": "return_type",
            "Type": "TEXT"
          },
          {
            "Name": "parameters",
            "Type": "TEXT"
          },
          {
            "Name": "parameter_count",
            "Type": "INTEGER"
          },
          {
            "Name": "access_modifier",
            "Type": "TEXT"
          },
          {
            "Name": "modifiers",
            "Type": "TEXT"
          },
          {
            "Name": "implementation_kind",
            "Type": "TEXT"
          },
          {
            "Name": "parent_method_key",
            "Type": "TEXT"
          },
          {
            "Name": "line_start",
            "Type": "INTEGER"
          },
          {
            "Name": "line_end",
            "Type": "INTEGER"
          },
          {
            "Name": "column_start",
            "Type": "INTEGER"
          },
          {
            "Name": "column_end",
            "Type": "INTEGER"
          }
        ]
      },
      {
        "Name": "v1_symbol_refs",
        "Kind": "view",
        "Columns": [
          {
            "Name": "symbol_ref_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_path",
            "Type": "TEXT"
          },
          {
            "Name": "method_id",
            "Type": "INTEGER"
          },
          {
            "Name": "method_key",
            "Type": "TEXT"
          },
          {
            "Name": "line_number",
            "Type": "INTEGER"
          },
          {
            "Name": "symbol_name",
            "Type": "TEXT"
          },
          {
            "Name": "symbol_kind",
            "Type": "TEXT"
          },
          {
            "Name": "symbol_container_type_name",
            "Type": "TEXT"
          },
          {
            "Name": "symbol_type_name",
            "Type": "TEXT"
          }
        ]
      },
      {
        "Name": "v1_type_inheritances",
        "Kind": "view",
        "Columns": [
          {
            "Name": "type_inheritance_id",
            "Type": "INTEGER"
          },
          {
            "Name": "type_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_path",
            "Type": "TEXT"
          },
          {
            "Name": "type_key",
            "Type": "TEXT"
          },
          {
            "Name": "type_name",
            "Type": "TEXT"
          },
          {
            "Name": "type_full_name",
            "Type": "TEXT"
          },
          {
            "Name": "base_type_name",
            "Type": "TEXT"
          },
          {
            "Name": "relation_kind",
            "Type": "TEXT"
          }
        ]
      },
      {
        "Name": "v1_types",
        "Kind": "view",
        "Columns": [
          {
            "Name": "type_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_path",
            "Type": "TEXT"
          },
          {
            "Name": "type_key",
            "Type": "TEXT"
          },
          {
            "Name": "name",
            "Type": "TEXT"
          },
          {
            "Name": "kind",
            "Type": "TEXT"
          },
          {
            "Name": "access_modifier",
            "Type": "TEXT"
          },
          {
            "Name": "modifiers",
            "Type": "TEXT"
          },
          {
            "Name": "full_name",
            "Type": "TEXT"
          },
          {
            "Name": "line",
            "Type": "INTEGER"
          },
          {
            "Name": "column",
            "Type": "INTEGER"
          }
        ]
      },
      {
        "Name": "v1_variables",
        "Kind": "view",
        "Columns": [
          {
            "Name": "variable_id",
            "Type": "INTEGER"
          },
          {
            "Name": "method_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_id",
            "Type": "INTEGER"
          },
          {
            "Name": "file_path",
            "Type": "TEXT"
          },
          {
            "Name": "method_key",
            "Type": "TEXT"
          },
          {
            "Name": "method_name",
            "Type": "TEXT"
          },
          {
            "Name": "variable_key",
            "Type": "TEXT"
          },
          {
            "Name": "name",
            "Type": "TEXT"
          },
          {
            "Name": "kind",
            "Type": "TEXT"
          },
          {
            "Name": "type_name",
            "Type": "TEXT"
          },
          {
            "Name": "declaration_line",
            "Type": "INTEGER"
          }
        ]
      }
    ],
    "Constants": [
      {
        "Location": "v1_files.language",
        "Usage": "Filter by source language.",
        "Values": [
          {
            "Value": "csharp",
            "Meaning": "C# source files indexed by the current extractor."
          }
        ]
      },
      {
        "Location": "v1_types.kind",
        "Usage": "Filter by declaration type category.",
        "Values": [
          {
            "Value": "Class",
            "Meaning": "class declarations."
          },
          {
            "Value": "Struct",
            "Meaning": "struct declarations."
          },
          {
            "Value": "Interface",
            "Meaning": "interface declarations."
          },
          {
            "Value": "Record",
            "Meaning": "record declarations."
          },
          {
            "Value": "Enum",
            "Meaning": "enum declarations."
          }
        ]
      },
      {
        "Location": "v1_types.access_modifier",
        "Usage": "Filter type declarations by effective access level.",
        "Values": [
          {
            "Value": "Public",
            "Meaning": "Publicly accessible type declaration."
          },
          {
            "Value": "Internal",
            "Meaning": "Assembly-scoped type declaration."
          },
          {
            "Value": "Private",
            "Meaning": "Nested private type declaration."
          },
          {
            "Value": "Protected",
            "Meaning": "Nested protected type declaration."
          },
          {
            "Value": "ProtectedInternal",
            "Meaning": "Nested protected internal type declaration."
          },
          {
            "Value": "PrivateProtected",
            "Meaning": "Nested private protected type declaration."
          },
          {
            "Value": "File",
            "Meaning": "File-local type declaration."
          }
        ]
      },
      {
        "Location": "v1_types.modifiers",
        "Usage": "Comma-separated non-access declaration modifiers for types.",
        "Values": [
          {
            "Value": "Abstract",
            "Meaning": "Type has the abstract modifier."
          },
          {
            "Value": "Sealed",
            "Meaning": "Type has the sealed modifier."
          },
          {
            "Value": "Static",
            "Meaning": "Type has the static modifier."
          },
          {
            "Value": "Partial",
            "Meaning": "Type has the partial modifier."
          },
          {
            "Value": "ReadOnly",
            "Meaning": "Type has the readonly modifier (for structs)."
          },
          {
            "Value": "Ref",
            "Meaning": "Type has the ref modifier (for ref structs)."
          }
        ]
      },
      {
        "Location": "v1_type_inheritances.relation_kind",
        "Usage": "Filter inheritance edges by relation kind.",
        "Values": [
          {
            "Value": "BaseType",
            "Meaning": "The inherited class/base type."
          },
          {
            "Value": "Interface",
            "Meaning": "An implemented interface (or struct interface)."
          },
          {
            "Value": "BaseInterface",
            "Meaning": "An interface inheriting another interface."
          }
        ]
      },
      {
        "Location": "v1_variables.kind",
        "Usage": "Filter by how the variable is introduced in a method.",
        "Values": [
          {
            "Value": "Parameter",
            "Meaning": "Method or constructor parameter."
          },
          {
            "Value": "Local",
            "Meaning": "Local variable declaration."
          },
          {
            "Value": "ForEach",
            "Meaning": "foreach loop variable."
          },
          {
            "Value": "Catch",
            "Meaning": "catch exception variable."
          }
        ]
      },
      {
        "Location": "v1_symbol_refs.symbol_kind",
        "Usage": "Filter coarse symbol-reference categories.",
        "Values": [
          {
            "Value": "Variable",
            "Meaning": "Identifier resolved to a known variable in the method scope."
          },
          {
            "Value": "Method",
            "Meaning": "Identifier used as an invoked method name."
          },
          {
            "Value": "Property",
            "Meaning": "Identifier used as a member/property access (non-invocation)."
          },
          {
            "Value": "Identifier",
            "Meaning": "Any other identifier usage."
          }
        ]
      },
      {
        "Location": "v1_methods.return_type",
        "Usage": "Constructor rows use a predefined marker.",
        "Values": [
          {
            "Value": "ctor",
            "Meaning": "Constructor methods (not regular methods)."
          }
        ]
      },
      {
        "Location": "v1_methods.access_modifier",
        "Usage": "Filter method and nested implementation rows by effective access level.",
        "Values": [
          {
            "Value": "Public",
            "Meaning": "Public method/member declaration."
          },
          {
            "Value": "Internal",
            "Meaning": "Internal method/member declaration."
          },
          {
            "Value": "Private",
            "Meaning": "Private method/member declaration."
          },
          {
            "Value": "Protected",
            "Meaning": "Protected method/member declaration."
          },
          {
            "Value": "ProtectedInternal",
            "Meaning": "Protected internal method/member declaration."
          },
          {
            "Value": "PrivateProtected",
            "Meaning": "Private protected method/member declaration."
          },
          {
            "Value": "Local",
            "Meaning": "Local function, lambda, or anonymous method."
          }
        ]
      },
      {
        "Location": "v1_methods.modifiers",
        "Usage": "Comma-separated non-access declaration modifiers for method rows.",
        "Values": [
          {
            "Value": "Abstract",
            "Meaning": "Method has the abstract modifier."
          },
          {
            "Value": "Virtual",
            "Meaning": "Method has the virtual modifier."
          },
          {
            "Value": "Override",
            "Meaning": "Method has the override modifier."
          },
          {
            "Value": "Sealed",
            "Meaning": "Method has the sealed modifier."
          },
          {
            "Value": "Static",
            "Meaning": "Method has the static modifier."
          },
          {
            "Value": "Async",
            "Meaning": "Method has the async modifier."
          }
        ]
      },
      {
        "Location": "v1_methods.implementation_kind",
        "Usage": "Distinguish top-level methods from nested implementation forms.",
        "Values": [
          {
            "Value": "Method",
            "Meaning": "Regular method declaration."
          },
          {
            "Value": "Constructor",
            "Meaning": "Constructor declaration."
          },
          {
            "Value": "LocalFunction",
            "Meaning": "Nested local function declaration."
          },
          {
            "Value": "Lambda",
            "Meaning": "Lambda expression implementation."
          },
          {
            "Value": "AnonymousMethod",
            "Meaning": "delegate(...) anonymous method implementation."
          }
        ]
      },
      {
        "Location": "meta_capabilities.key",
        "Usage": "Capability keys available for filtering metadata.",
        "Values": [
          {
            "Value": "sql_mode",
            "Meaning": "Read-only SQL mode metadata key."
          },
          {
            "Value": "public_views",
            "Meaning": "Public schema version metadata key."
          },
          {
            "Value": "languages",
            "Meaning": "Supported language metadata key."
          }
        ]
      },
      {
        "Location": "meta_capabilities.value (key = 'sql_mode')",
        "Usage": "Known values when key is 'sql_mode'.",
        "Values": [
          {
            "Value": "read-only",
            "Meaning": "Only read-oriented SQL statements are allowed."
          }
        ]
      },
      {
        "Location": "meta_capabilities.value (key = 'public_views')",
        "Usage": "Known values when key is 'public_views'.",
        "Values": [
          {
            "Value": "v1",
            "Meaning": "Public query surface version."
          }
        ]
      },
      {
        "Location": "meta_capabilities.value (key = 'languages')",
        "Usage": "Known values when key is 'languages'.",
        "Values": [
          {
            "Value": "csharp",
            "Meaning": "Current extractor language support."
          }
        ]
      }
    ]
  }
}
```