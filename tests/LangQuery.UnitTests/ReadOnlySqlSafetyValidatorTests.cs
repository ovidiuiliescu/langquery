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
}
