using PyRunner.Services;
using PyRunner.Models;

namespace PyRunner.Services.AI;

public sealed class AiFileContextService : IAiFileContextService
{
    private readonly ICodeDocumentService _documents;
    public AiFileContextService(ICodeDocumentService documents) => _documents = documents;

    public async Task<AiContextFile> LoadAsync(string path, bool isPrimary,
        CancellationToken cancellationToken = default)
    {
        var document = await _documents.LoadAsync(path, cancellationToken);
        return new(document.Fingerprint.Path, Path.GetFileName(document.Fingerprint.Path), document.Text,
            document.Fingerprint.Length, document.Fingerprint.Sha256, document.EncodingName,
            document.HasBom, document.NewLine, isPrimary);
    }
}
