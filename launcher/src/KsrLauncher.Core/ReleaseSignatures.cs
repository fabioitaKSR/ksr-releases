using System.Security.Cryptography;
using System.Text.Json;

namespace KsrLauncher.Core;

public sealed class ReleaseSignatures(string? publicKeyBase64 = null)
{
    // Public trust anchor only. The private key is held outside the repository.
    private const string PublicKey = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA2/D1owW2Kp2t+Ss0MCrTI8e1+0uBA4ShMZji5BJbeM9V9ifMyx6WyiHh4FyoLCWSSQnHgR3dN/9y2ZJL83cnhwwBTUX29nG6LTrDxfpz2vTsYP3tAXyahoVyjMxotFIwsFbERAY1/rMDz2K+vvW+casPunnL9kJkT3wlO8Ky9xwO+AayGOxzsgpAD6ctClSOSQtQiGz9YS89sBOcTPAnSmGz5PWcGZaOamIRfNUgOFt7WoIw4YiFjwJfgnplG34lXCvkW7tGmlDCyKazw1ZtIjXMHjmpjlA6trkFt0eP2odTwhE7vsAMSucDmDPNT9LpAfXfEekyo3X0bI3mF+4YCDhhHCyiBXnIt7AULwjG3f1WB3uJtgaZXqN1UZKAROQRdCEOP9AYNmm2ucGmT3YWfFIfJr+yBoGYgJ9JHFbc6GCJadswvmF2F/47nARX9yPMrdASIVjED/wLmBrKAk2ryNd9/TkglWSkMsWHspyXtOF/LBsNWSOy0hZC6RNpiWo1AgMBAAE=";
    private readonly byte[] _publicKey = Convert.FromBase64String(publicKeyBase64 ?? PublicKey);

    public void Verify(byte[] payload, byte[] signatureDocument)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<SignatureEnvelope>(signatureDocument, ManifestService.JsonOptions)
                ?? throw new CryptographicException("Missing release signature.");
            var keyId = Convert.ToHexString(SHA256.HashData(_publicKey)).ToLowerInvariant();
            if (envelope.Algorithm != "RSA-PSS-SHA256" || envelope.KeyId != keyId)
                throw new CryptographicException("The release signing key is not trusted.");
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(_publicKey, out _);
            if (!rsa.VerifyData(payload, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new CryptographicException("The release signature is invalid.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("Release authenticity verification failed. No update was installed.", ex);
        }
    }

    public static async Task<byte[]> DownloadBoundedAsync(HttpClient http, string url, int maximumBytes, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Release metadata requires HTTPS.");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if ((response.RequestMessage?.RequestUri is Uri finalUri && finalUri.Scheme != Uri.UriSchemeHttps) || response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("Unsafe release metadata response.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maximumBytes) throw new InvalidDataException("Release metadata is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private sealed record SignatureEnvelope(string Algorithm, string KeyId, string Signature);
}

public sealed record SignedLauncherMetadata(int SchemaVersion, string Product, string Version, string AssetName, string AssetUrl, string Sha256, long Size);
