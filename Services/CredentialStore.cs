using Windows.Security.Credentials;

namespace TrayTraffic.Services;

/// <summary>
/// Stores the user's own credentials (API keys they paste in, OAuth refresh or session tokens) in
/// Windows Credential Manager via <see cref="PasswordVault"/>, encrypted per user. Values are
/// capped at about 1,200 characters (2,560 bytes as UTF-16); for anything larger, such as an OAuth
/// session carrying a DPoP key, encrypt a file in LocalFolder with DPAPI instead (see the skill's
/// storage reference).
/// </summary>
internal static class CredentialStore
{
    private const string ResourcePrefix = "TrayTraffic.";
    private const int NotFound = unchecked((int)0x80070490);

    public static string? Get(string name)
    {
        try
        {
            PasswordCredential credential = new PasswordVault().Retrieve(ResourcePrefix + name, name);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception exception) when (exception.HResult == NotFound)
        {
            return null;
        }
    }

    public static void Set(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Remove(name);
            return;
        }

        new PasswordVault().Add(new PasswordCredential(ResourcePrefix + name, name, value));
    }

    public static void Remove(string name)
    {
        try
        {
            PasswordVault vault = new();
            PasswordCredential credential = vault.Retrieve(ResourcePrefix + name, name);
            vault.Remove(credential);
        }
        catch (Exception exception) when (exception.HResult == NotFound)
        {
            // Removing a credential that is already absent is harmless.
        }
    }
}
