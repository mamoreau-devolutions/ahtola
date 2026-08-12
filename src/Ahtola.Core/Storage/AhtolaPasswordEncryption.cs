using System.Security.Cryptography;
using System.Text;

namespace Ahtola.Core.Storage;

/// <summary>
/// Derives Ahtola AES-256-GCM page keys from connection-string passphrases.
/// </summary>
/// <remarks>
/// Version 1 uses a fixed domain salt and PBKDF2-HMAC-SHA256. This is an Ahtola
/// facade contract for consumers such as RDM; it is not SEE/SQLCipher compatible
/// and does not claim Turso passphrase interop.
/// </remarks>
public static class AhtolaPasswordEncryption
{
    /// <summary>Stable domain separation label for passphrase derivation v1.</summary>
    public const string DomainSaltV1 = "Ahtola.Password.v1";

    /// <summary>
    /// PBKDF2 iteration count for v1. Chosen as a modern floor; desktop open
    /// latency on large DBs may warrant a documented lower value later.
    /// </summary>
    public const int Pbkdf2IterationsV1 = 210_000;

    /// <summary>
    /// Substring RDM and SDS-shaped consumers look for when detecting a
    /// password-protected or non-database file.
    /// </summary>
    public const string EncryptedOrNotDatabaseMessage =
        "file is encrypted or is not a database";

    /// <summary>
    /// Derives a disposable AES-256-GCM <see cref="AhtolaEncryptionOptions"/> from
    /// a UTF-8 passphrase using PBKDF2-HMAC-SHA256 v1 parameters.
    /// </summary>
    public static AhtolaEncryptionOptions FromPassword(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var salt = Encoding.UTF8.GetBytes(DomainSaltV1);
            try
            {
                var key = Rfc2898DeriveBytes.Pbkdf2(
                    passwordBytes,
                    salt,
                    Pbkdf2IterationsV1,
                    HashAlgorithmName.SHA256,
                    outputLength: 32);
                try
                {
                    return new AhtolaEncryptionOptions(AhtolaEncryptionCipher.Aes256Gcm, key);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="message"/> already carries the SDS-shaped
    /// encrypted-or-not-database detection phrase.
    /// </summary>
    public static bool ContainsEncryptedOrNotDatabasePhrase(string? message)
        => !string.IsNullOrEmpty(message)
           && message.Contains(EncryptedOrNotDatabaseMessage, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ensures failure text includes the SDS-shaped detection phrase so RDM
    /// <c>IsPasswordProtected</c> / open sniffing keep working.
        /// The classic phrase is placed first so substring detectors and
        /// <c>StartsWith</c>-style checks stay reliable.
        /// </summary>
        public static string EnsureEncryptedOrNotDatabasePhrase(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return EncryptedOrNotDatabaseMessage;
            if (ContainsEncryptedOrNotDatabasePhrase(message))
                return message;
            return $"{EncryptedOrNotDatabaseMessage}: {message}";
        }
    }
