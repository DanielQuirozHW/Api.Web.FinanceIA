// Project : Api.Web.FinanceIA
// Created : 2025-01-01
// Modified: 2025-01-01

using System.Security.Cryptography;
using System.Text;
using Api.Web.FinanceIA.Clases.Exceptions;
using Microsoft.Extensions.Logging;

namespace Api.Web.FinanceIA.Clases;

public interface IEncryptionService
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
}

public sealed class EncryptionService : IEncryptionService
{
    private readonly byte[] _key;
    private readonly ILogger<EncryptionService> _logger;

    public EncryptionService(ILogger<EncryptionService> logger, IConfiguration configuration)
    {
        _logger = logger;

        var rawKey = configuration["ENCRYPTION_MASTER_KEY"];
        //var rawKey = Environment.GetEnvironmentVariable("ENCRYPTION_MASTER_KEY");

        if (string.IsNullOrWhiteSpace(rawKey))
        {
            throw new InvalidOperationException(
                "ENCRYPTION_MASTER_KEY environment variable is not configured.");
        }

        var keyBytes = Encoding.UTF8.GetBytes(rawKey);

        _key = new byte[32];
        var bytesToCopy = Math.Min(keyBytes.Length, 32);
        Buffer.BlockCopy(keyBytes, 0, _key, 0, bytesToCopy);
    }

    public string Encrypt(string plainText)
    {
        try
        {
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = _key;
            aes.GenerateIV();

            var iv = aes.IV;
            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            var result = new byte[iv.Length + cipherBytes.Length];
            Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
            Buffer.BlockCopy(cipherBytes, 0, result, iv.Length, cipherBytes.Length);

            return Convert.ToBase64String(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Encryption failed. Operation: {Operation}",
                nameof(Encrypt));

            throw new EncryptionException(ex);
        }
    }

    public string Decrypt(string cipherText)
    {
        try
        {
            var allBytes = Convert.FromBase64String(cipherText);

            var iv = new byte[16];
            var cipherBytes = new byte[allBytes.Length - 16];

            Buffer.BlockCopy(allBytes, 0, iv, 0, 16);
            Buffer.BlockCopy(allBytes, 16, cipherBytes, 0, cipherBytes.Length);

            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = _key;
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Decryption failed. Operation: {Operation}",
                nameof(Decrypt));

            throw new EncryptionException(ex);
        }
    }
}
