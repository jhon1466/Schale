using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Schale_Server.Controllers.SDK;

/// <summary>
/// Stable device identity with optional private-account linking. The local session id follows the game's
/// platform-id format, and the account GUID is kept consistent for both guest and credential-linked devices.
/// </summary>
internal static class GuestDeviceIdentity
{
    private const string LegacyLocalId = "76561198260711461";
    private const string LegacyGuid = "20790000041274554";
    private const string LegacyPublisherId = LegacyGuid;
    private const ulong LocalSuffixRange = 10_000_000_000UL;
    private static readonly ConcurrentDictionary<string, (string Seed, DateTimeOffset SeenAt)> SeedsByClientAddress = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> PublisherIdsByDeviceKey = new(StringComparer.Ordinal);
    private static volatile string? OfflineFallbackLocalId;

    public static void SetOfflineFallbackPublisherId(long? publisherId)
    {
        if (publisherId == null || publisherId <= 0)
        {
            OfflineFallbackLocalId = null;
            return;
        }

        var text = publisherId.Value.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(text, LegacyPublisherId, StringComparison.Ordinal))
        {
            OfflineFallbackLocalId = LegacyLocalId;
            return;
        }

        OfflineFallbackLocalId = text.Length >= 10
            ? "7656119" + text[^10..]
            : null;
    }

    public static bool IsLegacyLocalSessionUserId(string? value)
        => string.Equals(value, LegacyLocalId, StringComparison.Ordinal);

    public static string LocalSessionUserId(HttpRequest request)
    {
        var seed = ReadStableDeviceSeed(request);
        if (seed == null)
        {
            // Android sends its stable device identifiers to the Toy SDK first, but its
            // subsequent native IAS request omits them. Bridge those calls by source IP.
            // When there was no earlier Toy request, use the address as a last-resort
            // guest key instead of assigning every anonymous install the same account.
            seed = ReadClientAddress(request);
        }

        if (seed != null)
        {
            var deviceKey = DeviceKey(seed);
            if (PublisherIdsByDeviceKey.TryGetValue(deviceKey, out var publisherId))
            {
                // Private account recovery can bind a device to the pre-existing legacy game profile.
                // Keep that profile's original local id/GUID instead of deriving a new publisher id.
                if (string.Equals(publisherId, LegacyPublisherId, StringComparison.Ordinal))
                    return LegacyLocalId;

                return "7656119" + publisherId[^10..];
            }
        }

        // In Full Offline Mode on PC, disabling Wi-Fi/Ethernet changes the active NIC MAC address
        // that Nexon's Toy SDK hashes into its uuid header. Reuse the existing primary account's
        // Steam local ID so offline mode logs into the same Sensei account instead of creating a new one.
        if (!string.IsNullOrWhiteSpace(OfflineFallbackLocalId))
            return OfflineFallbackLocalId;

        if (seed == null)
            return LegacyLocalId;

        return "7656119" + Suffix(seed, LocalSuffixRange, 10);
    }

    public static void RememberClientDevice(HttpRequest request)
    {
        var seed = ReadHeaderSeed(request);
        var address = ReadClientAddress(request);
        if (seed != null && address != null)
            SeedsByClientAddress[address] = (seed, DateTimeOffset.UtcNow);
    }

    public static string? RecentDeviceKey(HttpRequest request)
    {
        var seed = ReadHeaderSeed(request);
        if (seed == null)
        {
            var address = ReadClientAddress(request);
            if (address != null)
            {
                if (SeedsByClientAddress.TryGetValue(address, out var remembered)
                    && DateTimeOffset.UtcNow - remembered.SeenAt <= TimeSpan.FromMinutes(15))
                    seed = remembered.Seed;
                else
                    seed = "address:" + address;
            }
        }

        return seed == null ? null : DeviceKey(seed);
    }

    public static void BindDeviceAccount(string deviceKey, long publisherId)
        => PublisherIdsByDeviceKey[deviceKey] = publisherId.ToString(CultureInfo.InvariantCulture);

    public static void LoadDeviceAccounts(IEnumerable<(string DeviceKey, long PublisherId)> accounts)
    {
        PublisherIdsByDeviceKey.Clear();
        foreach (var (deviceKey, publisherId) in accounts)
            BindDeviceAccount(deviceKey, publisherId);
    }

    public static string AccountGuid(HttpRequest request)
    {
        var localId = LocalSessionUserId(request);
        if (localId == LegacyLocalId)
            return LegacyGuid;

        // IasController.BuildAccountGuid derives the IAS primary-link GUID from the last 13
        // digits of local_session_user_id. Keep signIn.nx and IAS on exactly the same GUID.
        return "2079" + localId[^13..];
    }

    public static long AccountNpsn(HttpRequest request)
    {
        // Existing Android sessions persist their guid on-device. Use the exact NPSN
        // the SDK sent on this request; deriving it again from uuid can select a new
        // account while the client is still decrypting with its previously saved GUID.
        var requestGuid = request.Headers["guid"].FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(requestGuid)
            && long.TryParse(requestGuid, NumberStyles.None, CultureInfo.InvariantCulture, out var npsn))
            return npsn;

        return long.Parse(AccountGuid(request), CultureInfo.InvariantCulture);
    }

    private static string? ReadStableDeviceSeed(HttpRequest request)
    {
        var seed = ReadHeaderSeed(request);
        if (seed != null)
        {
            RememberClientDevice(request);
            return seed;
        }

        var address = ReadClientAddress(request);
        if (address != null && SeedsByClientAddress.TryGetValue(address, out var remembered)
            && DateTimeOffset.UtcNow - remembered.SeenAt <= TimeSpan.FromMinutes(15))
            return remembered.Seed;

        return address == null ? null : "address:" + address;
    }

    private static string? ReadHeaderSeed(HttpRequest request)
    {
        foreach (var header in new[] { "uuid", "appset-id", "uuid2", "gaid" })
        {
            var value = request.Headers[header].FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value.ToLowerInvariant();
        }

        return null;
    }

    private static string? ReadClientAddress(HttpRequest request)
    {
        var forwarded = request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (System.Net.IPAddress.TryParse(first, out var parsed))
                return parsed.MapToIPv4().ToString();
        }

        var address = request.HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString();
        return string.IsNullOrWhiteSpace(address) || address == "0.0.0.0" ? null : address;
    }

    private static string Suffix(string seed, ulong range, int digits)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(IdentityPrefix.Value + "-guest-device:" + seed));
        var number = BinaryPrimitives.ReadUInt64LittleEndian(hash) % range;
        return number.ToString($"D{digits}", CultureInfo.InvariantCulture);
    }

    public static void RememberClientSeed(HttpRequest request, string seed)
    {
        var address = ReadClientAddress(request);
        if (address != null)
            SeedsByClientAddress[address] = (seed.ToLowerInvariant(), DateTimeOffset.UtcNow);
    }

    public static string DeviceKey(string seed)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IdentityPrefix.Value + "-device-key:" + seed))).ToLowerInvariant();
}
