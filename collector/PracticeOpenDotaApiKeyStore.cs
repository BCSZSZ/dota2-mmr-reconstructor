using System.Security.Cryptography;
using System.Text;

namespace Dota2MmrCollector;

internal static class PracticeOpenDotaApiKeyStore
{
    private const string DirectoryName = "Dota2MmrReconstructor";
    private const string FileName = "opendota-api-key.bin";
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("Dota2MmrReconstructor/OpenDotaApiKey/v1");

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DirectoryName,
        FileName);

    public static string? TryLoad(string? path = null)
    {
        var resolvedPath = ResolvePath(path);
        if (!File.Exists(resolvedPath))
        {
            return null;
        }

        byte[]? encrypted = null;
        byte[]? plaintext = null;
        try
        {
            encrypted = File.ReadAllBytes(resolvedPath);
            plaintext = ProtectedData.Unprotect(
                encrypted,
                Entropy,
                DataProtectionScope.CurrentUser);
            var value = Encoding.UTF8.GetString(plaintext).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or CryptographicException)
        {
            return null;
        }
        finally
        {
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public static void Save(string apiKey, string? path = null)
    {
        var value = apiKey.Trim();
        if (value.Length == 0)
        {
            Delete(path);
            return;
        }

        var resolvedPath = ResolvePath(path);
        var directory = Path.GetDirectoryName(resolvedPath)
            ?? throw new IOException("OpenDota API key 保存路径无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = resolvedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[]? plaintext = null;
        byte[]? encrypted = null;
        try
        {
            plaintext = Encoding.UTF8.GetBytes(value);
            encrypted = ProtectedData.Protect(
                plaintext,
                Entropy,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(temporaryPath, encrypted);
            File.Move(temporaryPath, resolvedPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }

    public static void Delete(string? path = null)
    {
        var resolvedPath = ResolvePath(path);
        if (File.Exists(resolvedPath))
        {
            File.Delete(resolvedPath);
        }
    }

    private static string ResolvePath(string? path) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? DefaultPath : path);
}
