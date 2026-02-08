using LangQuery.Query.Validation;

namespace LangQuery.UnitTests;

public sealed class ReadOnlySqlSafetyValidatorTests
{
    private readonly ReadOnlySqlSafetyValidator _validator = new();

    [Fact]
    public void Validate_AllowsReadOnlyStatements()
    {
        var select = _validator.Validate("SELECT 1;");
        var withCte = _validator.Validate("WITH t AS (SELECT 1 AS x) SELECT x FROM t");
        var explain = _validator.Validate("EXPLAIN SELECT 1");

        Assert.True(select.IsValid);
        Assert.True(withCte.IsValid);
        Assert.True(explain.IsValid);
    }

    [Fact]
    public void Validate_RejectsMutationStatements()
    {
        var result = _validator.Validate("DELETE FROM files");

        Assert.False(result.IsValid);
        Assert.Contains("read-only", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsMultipleStatements()
    {
        var result = _validator.Validate("SELECT 1; SELECT 2;");

        Assert.False(result.IsValid);
        Assert.Contains("single", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_IgnoresForbiddenTokensInsideCommentsAndStrings()
    {
        const string sql = "-- DELETE FROM files\nSELECT 'DROP TABLE users' AS note;";

        var result = _validator.Validate(sql);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsEmptySql()
    {
        var result = _validator.Validate(string.Empty);

        Assert.False(result.IsValid);
        Assert.Contains("empty", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsWhitespaceOnlySql()
    {
        var result = _validator.Validate("   \t\r\n  ");

        Assert.False(result.IsValid);
        Assert.Contains("empty", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsLineCommentOnlySql()
    {
        var result = _validator.Validate("-- this query has no statements");

        Assert.False(result.IsValid);
        Assert.Contains("empty", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsBlockCommentOnlySql()
    {
        var result = _validator.Validate("/* no query here */");

        Assert.False(result.IsValid);
        Assert.Contains("empty", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllowsTrailingSemicolonAndWhitespace()
    {
        var result = _validator.Validate("SELECT 1;   \r\n\t");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_AllowsExplainQueryPlanStatements()
    {
        var result = _validator.Validate("EXPLAIN QUERY PLAN SELECT 1");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsDisallowedFirstKeyword()
    {
        var result = _validator.Validate("BEGIN TRANSACTION");

        Assert.False(result.IsValid);
        Assert.Contains("only select", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsPragmaKeywordRegardlessOfCase()
    {
        var result = _validator.Validate("pRaGmA table_info('files')");

        Assert.False(result.IsValid);
        Assert.Contains("read-only", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllowsEscapedSingleQuotesInsideStringLiterals()
    {
        var result = _validator.Validate("SELECT 'it''s safe' AS note");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_AllowsForbiddenWordsInsideQuotedIdentifiers()
    {
        var result = _validator.Validate("SELECT 1 AS \"DROP\"");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsMultipleStatementsWhenTrailingContentExists()
    {
        var result = _validator.Validate("SELECT 1; -- done\nSELECT 2");

        Assert.False(result.IsValid);
        Assert.Contains("single", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsMutationTokenInsideWithStatement()
    {
        var result = _validator.Validate("WITH x AS (SELECT 1) INSERT INTO files(path, hash, language, indexed_utc) VALUES ('a', 'b', 'c', 'd')");

        Assert.False(result.IsValid);
        Assert.Contains("read-only", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }
}
