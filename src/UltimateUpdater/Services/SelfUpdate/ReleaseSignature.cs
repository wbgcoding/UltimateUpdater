using System.IO;
using System.Security.Cryptography;

namespace UltimateUpdater.Services.SelfUpdate;

/// <summary>
/// The release signature check: ECDSA P-256 over SHA-256, the signature as base64 text of the raw 64 byte
/// IEEE P1363 form. It sits on top of the checksum file because that file lives in the same release as the
/// artifact: whoever can swap the artifact can swap the checksums too, but not sign for our key.
/// </summary>
internal static class ReleaseSignature
{
    /// <summary>The public half of the release key, SubjectPublicKeyInfo PEM. The private half never leaves
    /// the release machine; with no usable key every update is refused (fail closed).</summary>
    internal const string ReleasePublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEFSBPLo9ErPUjI53wBqjpc2ANmpnY\n" +
        "b98sJVo6nA5WjBCR0BrIHAfZ02623gbvFHFn5sr9dAXu9HrD8v2SGpKxAA==\n" +
        "-----END PUBLIC KEY-----";

    private const int SignatureBytes = 64;

    /// <summary>nistP256 / secp256r1; a 256 bit key on another curve is not ours.</summary>
    private const string NistP256Oid = "1.2.840.10045.3.1.7";

    /// <summary>True when <paramref name="publicKeyPem"/> is a P-256 public key this check can use.</summary>
    internal static bool IsUsableKey(string? publicKeyPem)
    {
        using var key = TryLoadKey(publicKeyPem);
        return key is not null;
    }

    /// <summary>True only for a well-formed signature that matches every byte of <paramref name="data"/>
    /// under the given key. Reads the stream from the start; anything unreadable counts as invalid.</summary>
    internal static bool Verify(Stream data, string? signatureText, string? publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(signatureText))
            return false;

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureText.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        if (signature.Length != SignatureBytes)
            return false;

        using var key = TryLoadKey(publicKeyPem);
        if (key is null)
            return false;

        try
        {
            data.Position = 0;
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            return false;
        }
    }

    private static ECDsa? TryLoadKey(string? publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
            return null;

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(publicKeyPem);
            if (key.ExportParameters(false).Curve.Oid.Value != NistP256Oid)
            {
                key.Dispose();
                return null;
            }

            return key;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            key.Dispose();
            return null;
        }
    }
}
