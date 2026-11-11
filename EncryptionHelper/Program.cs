using System.Security.Cryptography;
using System.Text;

namespace TenantSeeder;

internal class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== Tenant Credential Encryption Tool ===\n");

        while (true)
        {
            Console.WriteLine("\nOptions:");
            Console.WriteLine("1. Encrypt single value");
            Console.WriteLine("2. Encrypt all tenant credentials (interactive)");
            Console.WriteLine("3. Exit");
            Console.Write("\nSelect option: ");

            var option = Console.ReadLine();

            switch (option)
            {
                case "1":
                    EncryptSingleValue();
                    break;
                case "2":
                    EncryptTenantCredentials();
                    break;
                case "3":
                    return;
                default:
                    Console.WriteLine("Invalid option.");
                    break;
            }
        }
    }

    static void EncryptSingleValue()
    {
        Console.Write("\nEnter ENCRYPTION_MASTER_KEY: ");
        var masterKey = Console.ReadLine();

        Console.Write("Enter value to encrypt: ");
        var plainText = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(masterKey) || string.IsNullOrWhiteSpace(plainText))
        {
            Console.WriteLine("ERROR: Both values are required.");
            return;
        }

        var encrypted = Encrypt(masterKey, plainText);

        Console.WriteLine("\n=== ENCRYPTED VALUE (Base64) ===");
        Console.WriteLine(encrypted);
        Console.WriteLine("\nCopy this value to your database.");
    }

    static void EncryptTenantCredentials()
    {
        Console.Write("\nEnter ENCRYPTION_MASTER_KEY: ");
        var masterKey = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(masterKey))
        {
            Console.WriteLine("ERROR: Master key is required.");
            return;
        }

        Console.WriteLine("\n=== Enter Tenant Credentials ===");

        Console.Write("Cliente (tenant identifier): ");
        var cliente = Console.ReadLine();

        Console.Write("Dynamics URL: ");
        var dynamicsUrl = Console.ReadLine();

        Console.Write("Dynamics Client ID: ");
        var clientId = Console.ReadLine();

        Console.Write("Dynamics Client Secret: ");
        var clientSecret = Console.ReadLine();

        Console.Write("Azure AD Tenant ID (GUID): ");
        var azureTenantId = Console.ReadLine();

        Console.Write("Firebase Project ID: ");
        var firebaseProjectId = Console.ReadLine();

        Console.WriteLine("Firebase Credential JSON (paste entire JSON, then press Enter twice):");
        var firebaseJson = ReadMultilineInput();

        Console.Write("reCAPTCHA Secret Key: ");
        var recaptchaKey = Console.ReadLine();

        // Tenant profile (§7.0). Only "non_sgr" produces a row; "sgr" is the in-code default and
        // needs nothing. See Api.Web.FinanceIA/docs/guides/tenant-profile.md.
        string entityType;
        while (true)
        {
            Console.Write("Entity type (sgr / non_sgr) [sgr]: ");
            entityType = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();

            if (entityType.Length == 0)
                entityType = "sgr";

            if (entityType is "sgr" or "non_sgr")
                break;

            Console.WriteLine("Please answer sgr or non_sgr.");
        }

        // Encrypt secrets
        var encryptedClientSecret = Encrypt(masterKey, clientSecret!);
        var encryptedFirebaseJson = Encrypt(masterKey, firebaseJson);
        var encryptedRecaptchaKey = Encrypt(masterKey, recaptchaKey!);

        Console.WriteLine("\n=== ENCRYPTED VALUES - Copy to SQL ===\n");

        Console.WriteLine("-- INSERT INTO Credenciales");
        Console.WriteLine($"INSERT INTO Credenciales (url, clientid, clientsecret, tenantid, cliente)");
        Console.WriteLine($"VALUES (");
        Console.WriteLine($"    '{dynamicsUrl}',");
        Console.WriteLine($"    '{clientId}',");
        Console.WriteLine($"    '{encryptedClientSecret}',");
        Console.WriteLine($"    '{azureTenantId}',");
        Console.WriteLine($"    '{cliente}'");
        Console.WriteLine($");");

        Console.WriteLine("\n-- INSERT INTO TenantFirebaseConfig");
        Console.WriteLine($"INSERT INTO TenantFirebaseConfig (Cliente, ProjectId, CredentialJson, RecaptchaSecretKey, IsActive)");
        Console.WriteLine($"VALUES (");
        Console.WriteLine($"    '{cliente}',");
        Console.WriteLine($"    '{firebaseProjectId}',");
        Console.WriteLine($"    '{encryptedFirebaseJson}',");
        Console.WriteLine($"    '{encryptedRecaptchaKey}',");
        Console.WriteLine($"    1");
        Console.WriteLine($");");

        Console.WriteLine("\n=== Copy and execute these SQL statements in your database ===");

        // The profile row lives in the TENANT BUSINESS database, not in the master database the
        // two statements above target. Printed separately so it is not pasted into the wrong one.
        Console.WriteLine("\n=== Tenant profile (run on the TENANT BUSINESS database, after the DDL scripts) ===\n");

        if (entityType == "non_sgr")
        {
            Console.WriteLine("-- INSERT INTO TenantSettings (entity.type = non_sgr)");
            Console.WriteLine("INSERT INTO [dbo].[TenantSettings] ([TenantId], [SettingKey], [SettingValue], [Description])");
            Console.WriteLine($"VALUES (N'{cliente}', N'entity.type', N'non_sgr',");
            Console.WriteLine("        N'Entidad no-SGR: aplica el preset non_sgr (CASFOG deshabilitado).');");
        }
        else
        {
            Console.WriteLine("-- Entity type sgr: no TenantSettings row needed, the code default is the SGR preset.");
        }
    }

    static string Encrypt(string masterKey, string plainText)
    {
        // Normalize key to 32 bytes
        var keyBytes = Encoding.UTF8.GetBytes(masterKey);
        if (keyBytes.Length < 32)
        {
            var temp = new byte[32];
            Array.Copy(keyBytes, temp, keyBytes.Length);
            keyBytes = temp;
        }
        else if (keyBytes.Length > 32)
        {
            var temp = new byte[32];
            Array.Copy(keyBytes, 0, temp, 0, 32);
            keyBytes = temp;
        }

        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Array.Copy(aes.IV, 0, result, 0, aes.IV.Length);
        Array.Copy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    static string ReadMultilineInput()
    {
        var lines = new List<string>();
        string? line;

        while (!string.IsNullOrWhiteSpace(line = Console.ReadLine()))
        {
            lines.Add(line);
        }

        return string.Join(Environment.NewLine, lines);
    }
}