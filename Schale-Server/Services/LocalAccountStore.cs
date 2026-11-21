using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Schale_Server.Controllers.SDK;

namespace Schale_Server.Services;

/// <summary>Private-server username/password accounts and persistent device-to-game-account links.</summary>
public sealed class LocalAccountStore
{
    private const int PasswordIterations = 310_000;
    private readonly string _connectionString;
    private readonly object _sync = new();

    public LocalAccountStore()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "identity.sqlite3");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Initialize();
    }

    private void Initialize()
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS LocalAccounts (
                    Username TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                    PublisherId INTEGER NOT NULL,
                    PasswordSalt BLOB NOT NULL,
                    PasswordHash BLOB NOT NULL,
                    CreatedUtc INTEGER NOT NULL,
                    HmacPasswordHash BLOB,
                    Sha512PasswordHash BLOB
                );
                CREATE TABLE IF NOT EXISTS LocalAccountDevices (
                    DeviceKey TEXT NOT NULL PRIMARY KEY,
                    Username TEXT NOT NULL COLLATE NOCASE REFERENCES LocalAccounts(Username) ON DELETE CASCADE,
                    LinkedUtc INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS LocalAccountSessions (
                    TokenHash TEXT NOT NULL PRIMARY KEY,
                    Username TEXT NOT NULL COLLATE NOCASE REFERENCES LocalAccounts(Username) ON DELETE CASCADE,
                    ExpiresUtc INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_LocalAccountDevices_Username ON LocalAccountDevices(Username);
                CREATE INDEX IF NOT EXISTS IX_LocalAccountSessions_ExpiresUtc ON LocalAccountSessions(ExpiresUtc);
                """;
            command.ExecuteNonQuery();

            EnsureColumns(db);

            var links = new List<(string DeviceKey, long PublisherId)>();
            command.CommandText = """
                SELECT d.DeviceKey, a.PublisherId
                FROM LocalAccountDevices d
                JOIN LocalAccounts a ON a.Username = d.Username;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                links.Add((reader.GetString(0), reader.GetInt64(1)));
            GuestDeviceIdentity.LoadDeviceAccounts(links);
        }
    }

    private static void EnsureColumns(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(LocalAccounts);";
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                cols.Add(reader.GetString(1));
        }
        if (!cols.Contains("HmacPasswordHash"))
        {
            using var alterCmd = db.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE LocalAccounts ADD COLUMN HmacPasswordHash BLOB;";
            alterCmd.ExecuteNonQuery();
        }
        if (!cols.Contains("Sha512PasswordHash"))
        {
            using var alterCmd = db.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE LocalAccounts ADD COLUMN Sha512PasswordHash BLOB;";
            alterCmd.ExecuteNonQuery();
        }
    }

    public static string ComputeNexonHmac(string rawPassword)
    {
        var key = System.Text.Encoding.UTF8.GetBytes("NexonUser");
        var data = System.Text.Encoding.UTF8.GetBytes(rawPassword);
        var hmac = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(hmac).ToLowerInvariant();
    }

    public static string ComputeSha512Hex(string rawPassword)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(rawPassword);
        var hash = SHA512.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public bool Register(string username, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = HashPassword(password, salt);
        var hmacHex = ComputeNexonHmac(password);
        var hmacHash = HashPassword(hmacHex, salt);
        var sha512Hex = ComputeSha512Hex(password);
        var sha512Hash = HashPassword(sha512Hex, salt);
        var publisherId = CreatePublisherId();
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO LocalAccounts(Username, PublisherId, PasswordSalt, PasswordHash, CreatedUtc, HmacPasswordHash, Sha512PasswordHash)
                VALUES ($username, $publisherId, $salt, $hash, $createdUtc, $hmacHash, $sha512Hash);
                """;
            command.Parameters.AddWithValue("$username", username);
            command.Parameters.AddWithValue("$publisherId", publisherId);
            command.Parameters.AddWithValue("$salt", salt);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$createdUtc", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$hmacHash", hmacHash);
            command.Parameters.AddWithValue("$sha512Hash", sha512Hash);
            try
            {
                command.ExecuteNonQuery();
                return true;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return false;
            }
        }
    }

    public LocalAccount? Authenticate(string username, string password, Action<string>? debugLog = null)
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT Username, PublisherId, PasswordSalt, PasswordHash, HmacPasswordHash, Sha512PasswordHash FROM LocalAccounts WHERE Username = $username;";
            command.Parameters.AddWithValue("$username", username);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                debugLog?.Invoke($"User '{username}' not found in LocalAccounts");
                return null;
            }

            var canonicalName = reader.GetString(0);
            var publisherId = reader.GetInt64(1);
            var salt = (byte[])reader[2];
            var passwordHash = (byte[])reader[3];
            var hmacHash = reader.IsDBNull(4) ? null : (byte[])reader[4];
            var sha512Hash = reader.IsDBNull(5) ? null : (byte[])reader[5];
            debugLog?.Invoke($"Found user '{canonicalName}', publisherId={publisherId}, hasSha512={sha512Hash != null}, hasHmac={hmacHash != null}");

            var account = new LocalAccount(canonicalName, publisherId, salt, passwordHash, hmacHash, sha512Hash);

            // 1. Direct raw password check
            var candidateRaw = HashPassword(password, salt);
            if (CryptographicOperations.FixedTimeEquals(candidateRaw, passwordHash))
            {
                debugLog?.Invoke("Matched candidateRaw with passwordHash");
                return account;
            }

            // 2. Client sent SHA-512 hex string (Android NXPArenaLoginDialog sends sha512(password))
            var candidateLower = HashPassword(password.ToLowerInvariant(), salt);
            if (sha512Hash != null && CryptographicOperations.FixedTimeEquals(candidateLower, sha512Hash))
            {
                debugLog?.Invoke("Matched candidateLower with sha512Hash");
                return account;
            }

            // 3. Client sent raw password, check derived SHA512 against sha512Hash
            if (sha512Hash != null)
            {
                var candidateSha512FromRaw = HashPassword(ComputeSha512Hex(password), salt);
                if (CryptographicOperations.FixedTimeEquals(candidateSha512FromRaw, sha512Hash))
                {
                    debugLog?.Invoke("Matched candidateSha512FromRaw with sha512Hash");
                    return account;
                }
            }

            // 4. Client sent raw password, check derived HMAC against HmacPasswordHash
            if (hmacHash != null)
            {
                var candidateHmacFromRaw = HashPassword(ComputeNexonHmac(password), salt);
                if (CryptographicOperations.FixedTimeEquals(candidateHmacFromRaw, hmacHash))
                {
                    debugLog?.Invoke("Matched candidateHmacFromRaw with hmacHash");
                    return account;
                }
            }

            // 5. Client sent pre-hashed HMAC hex
            if (hmacHash != null && CryptographicOperations.FixedTimeEquals(candidateLower, hmacHash))
            {
                debugLog?.Invoke("Matched candidateLower with hmacHash");
                return account;
            }

            // 6. In case passwordHash itself was stored as hex
            if (CryptographicOperations.FixedTimeEquals(candidateLower, passwordHash))
            {
                debugLog?.Invoke("Matched candidateLower with passwordHash");
                return account;
            }

            debugLog?.Invoke($"No hash matched. password.Length={password.Length}, passwordPrefix='{(password.Length >= 8 ? password[..8] : password)}'");
            return null;
        }
    }

    public LocalAccount? GetAccount(string username)
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT Username, PublisherId FROM LocalAccounts WHERE Username = $username;";
            command.Parameters.AddWithValue("$username", username);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new LocalAccount(reader.GetString(0), reader.GetInt64(1), Array.Empty<byte>(), Array.Empty<byte>())
                : null;
        }
    }

    public string CreateSession(string username)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tokenHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO LocalAccountSessions(TokenHash, Username, ExpiresUtc)
                VALUES ($tokenHash, $username, $expiresUtc);
                """;
            command.Parameters.AddWithValue("$tokenHash", tokenHash);
            command.Parameters.AddWithValue("$username", username);
            command.Parameters.AddWithValue("$expiresUtc", DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
        return token;
    }

    public LocalAccount? GetSessionAccount(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        var tokenHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT a.Username, a.PublisherId
                FROM LocalAccountSessions s JOIN LocalAccounts a ON a.Username = s.Username
                WHERE s.TokenHash = $tokenHash AND s.ExpiresUtc > $now;
                """;
            command.Parameters.AddWithValue("$tokenHash", tokenHash);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new LocalAccount(reader.GetString(0), reader.GetInt64(1), Array.Empty<byte>(), Array.Empty<byte>())
                : null;
        }
    }

    public void RevokeSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;
        var tokenHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM LocalAccountSessions WHERE TokenHash = $tokenHash;";
            command.Parameters.AddWithValue("$tokenHash", tokenHash);
            command.ExecuteNonQuery();
        }
    }

    public void LinkDevice(string username, string deviceKey, long publisherId)
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO LocalAccountDevices(DeviceKey, Username, LinkedUtc)
                VALUES ($deviceKey, $username, $linkedUtc)
                ON CONFLICT(DeviceKey) DO UPDATE SET Username = excluded.Username, LinkedUtc = excluded.LinkedUtc;
                """;
            command.Parameters.AddWithValue("$deviceKey", deviceKey);
            command.Parameters.AddWithValue("$username", username);
            command.Parameters.AddWithValue("$linkedUtc", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
        GuestDeviceIdentity.BindDeviceAccount(deviceKey, publisherId);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static byte[] HashPassword(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);

    private static long CreatePublisherId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var suffix = 1_000_000_000L + (long)(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes) % 9_000_000_000UL);
        // GuestDeviceIdentity and the game's IAS schema map a local id's final 13 digits
        // to a GUID of the form 2079 + 119 + ten-digit suffix.
        return long.Parse("2079119" + suffix.ToString("D10", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed record LocalAccount(string Username, long PublisherId, byte[] PasswordSalt, byte[] PasswordHash, byte[]? HmacPasswordHash = null, byte[]? Sha512PasswordHash = null);
}
