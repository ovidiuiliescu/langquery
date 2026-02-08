using LangQuery.Core.Models;

namespace LangQuery.Core.Abstractions;

public interface ISqlSafetyValidator
{
    SqlValidationResult Validate(string sql);
}
