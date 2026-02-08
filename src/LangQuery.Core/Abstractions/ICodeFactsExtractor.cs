using LangQuery.Core.Models;

namespace LangQuery.Core.Abstractions;

public interface ICodeFactsExtractor
{
    bool CanHandle(string filePath);

    FileFacts Extract(string filePath, string content, string hash);
}
