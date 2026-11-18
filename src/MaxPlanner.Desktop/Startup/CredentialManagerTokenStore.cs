using System.Runtime.InteropServices;
using MaxPlanner.Application.PullRequests;
using Windows.Security.Credentials;

namespace MaxPlanner.Desktop.Startup;

/// <summary>
/// The Azure DevOps token in Windows Credential Manager through <see cref="PasswordVault"/> (ADR 0031): one entry
/// (<see cref="Resource"/>, user <see cref="UserName"/>) readable only by the current Windows user. It works in this
/// unpackaged, self-contained app (spike, Nov 2026). The token is never logged; nothing here logs at all.
/// </summary>
internal sealed class CredentialManagerTokenStore : IAzureDevOpsTokenStore
{
    public const string Resource = "MaxPlanner.AzureDevOps";
    public const string UserName = "pat";

    // ERROR_NOT_FOUND: the vault has no entry for the resource. Only this is "no token"; any other failure is real.
    private const int ElementNotFound = unchecked((int)0x80070490);

    public string? Read()
    {
        if (Find() is not { } credential)
        {
            return null;
        }

        // The password is empty until it is retrieved explicitly.
        credential.RetrievePassword();
        return string.IsNullOrEmpty(credential.Password) ? null : credential.Password;
    }

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // Add with the same resource and user name replaces the saved password.
        new PasswordVault().Add(new PasswordCredential(Resource, UserName, token));
    }

    public void Remove()
    {
        if (Find() is { } credential)
        {
            new PasswordVault().Remove(credential);
        }
    }

    private static PasswordCredential? Find()
    {
        try
        {
            return new PasswordVault().Retrieve(Resource, UserName);
        }
        catch (COMException ex) when (ex.HResult == ElementNotFound)
        {
            return null;
        }
    }
}
