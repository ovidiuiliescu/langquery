using System.Text;
using System.Text.RegularExpressions;
using LangQuery.Core.Abstractions;
using LangQuery.Core.Models;

namespace LangQuery.Query.Validation;

public sealed class ReadOnlySqlSafetyValidator : ISqlSafetyValidator
{
    private static readonly HashSet<string> AllowedFirstKeywords =
    [
        "SELECT",
        "WITH",
        "EXPLAIN"
    ];

    private static readonly Regex ForbiddenTokens = new(
        @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|REPLACE|VACUUM|ATTACH|DETACH|PRAGMA|REINDEX|ANALYZE)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public SqlValidationResult Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqlValidationResult.Invalid("SQL query is empty.");
        }

        var sanitized = StripCommentsAndStringLiterals(sql);
        var statement = NormalizeSingleStatement(sanitized, out var statementError);
        if (statementError is not null)
        {
            return SqlValidationResult.Invalid(statementError);
        }

        if (ForbiddenTokens.IsMatch(statement))
        {
            return SqlValidationResult.Invalid("Query contains mutating or unsafe SQL tokens. Only read-only SELECT/CTE/EXPLAIN is allowed.");
        }

        var firstKeyword = GetFirstKeyword(statement);
        if (firstKeyword is null || !AllowedFirstKeywords.Contains(firstKeyword))
        {
            return SqlValidationResult.Invalid("Only SELECT, WITH, or EXPLAIN statements are allowed.");
        }

        return SqlValidationResult.Valid();
    }

    private static string StripCommentsAndStringLiterals(string sql)
    {
        var builder = new StringBuilder(sql.Length);
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inBacktickQuote = false;
        var inBracketIdentifier = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                    builder.Append(c);
                }
                else
                {
                    builder.Append(' ');
                }

                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    builder.Append("  ");
                    i++;
                }
                else
                {
                    builder.Append(' ');
                }

                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && !inBacktickQuote && !inBracketIdentifier)
            {
                if (c == '-' && next == '-')
                {
                    inLineComment = true;
                    builder.Append("  ");
                    i++;
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    inBlockComment = true;
                    builder.Append("  ");
                    i++;
                    continue;
                }
            }

            if (!inDoubleQuote && !inBacktickQuote && !inBracketIdentifier && c == '\'')
            {
                if (inSingleQuote && next == '\'')
                {
                    builder.Append("  ");
                    i++;
                    continue;
                }

                inSingleQuote = !inSingleQuote;
                builder.Append(' ');
                continue;
            }

            if (!inSingleQuote && !inBacktickQuote && !inBracketIdentifier && c == '"')
            {
                inDoubleQuote = !inDoubleQuote;
                builder.Append(' ');
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && !inBracketIdentifier && c == '`')
            {
                if (inBacktickQuote && next == '`')
                {
                    builder.Append("  ");
                    i++;
                    continue;
                }

                inBacktickQuote = !inBacktickQuote;
                builder.Append(' ');
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && !inBacktickQuote)
            {
                if (!inBracketIdentifier && c == '[')
                {
                    inBracketIdentifier = true;
                    builder.Append(' ');
                    continue;
                }

                if (inBracketIdentifier && c == ']')
                {
                    if (next == ']')
                    {
                        builder.Append("  ");
                        i++;
                        continue;
                    }

                    inBracketIdentifier = false;
                    builder.Append(' ');
                    continue;
                }
            }

            builder.Append(inSingleQuote || inDoubleQuote || inBacktickQuote || inBracketIdentifier ? ' ' : c);
        }

        return builder.ToString();
    }

    private static string NormalizeSingleStatement(string sql, out string? error)
    {
        error = null;
        var trimmed = sql.Trim();
        if (trimmed.Length == 0)
        {
            error = "SQL query is empty after removing comments and string literals.";
            return string.Empty;
        }

        var lastSemicolon = trimmed.LastIndexOf(';');
        if (lastSemicolon >= 0)
        {
            if (trimmed[..lastSemicolon].Contains(';', StringComparison.Ordinal))
            {
                error = "Only a single SQL statement is allowed.";
                return string.Empty;
            }

            if (trimmed[(lastSemicolon + 1)..].Trim().Length > 0)
            {
                error = "Only a single SQL statement is allowed.";
                return string.Empty;
            }

            trimmed = trimmed[..lastSemicolon].TrimEnd();
        }

        return trimmed;
    }

    private static string? GetFirstKeyword(string sql)
    {
        var idx = 0;
        while (idx < sql.Length && char.IsWhiteSpace(sql[idx]))
        {
            idx++;
        }

        if (idx >= sql.Length)
        {
            return null;
        }

        var end = idx;
        while (end < sql.Length && (char.IsLetter(sql[end]) || sql[end] == '_'))
        {
            end++;
        }

        if (end == idx)
        {
            return null;
        }

        return sql[idx..end].ToUpperInvariant();
    }
}
