using PyRunner.Models;
using Windows.Security.Credentials;

namespace PyRunner.Services.AI;

public sealed class AiCredentialStore : IAiCredentialStore
{
    private readonly PasswordVault _vault = new();

    public bool HasCredential(AiCredentialKey key) => Retrieve(key) is not null;

    public void Save(AiCredentialKey key, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 4096)
            throw new ArgumentException("AI_Error_InvalidCredential", nameof(secret));
        Delete(key);
        _vault.Add(new PasswordCredential(key.Resource, "api-key", secret.Trim()));
    }

    public string? Retrieve(AiCredentialKey key)
    {
        try
        {
            var credential = _vault.Retrieve(key.Resource, "api-key");
            credential.RetrievePassword();
            return credential.Password;
        }
        catch { return null; }
    }

    public void Delete(AiCredentialKey key)
    {
        try { _vault.Remove(_vault.Retrieve(key.Resource, "api-key")); }
        catch { }
    }
}
