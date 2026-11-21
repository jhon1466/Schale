using System.Text;
using BlueArchiveAPI.Configuration;

namespace Schale_Server.Controllers.SDK
{
    /// <summary>
    /// Prefix mixed into every derived identity value (guest device suffix, device key, support UID, friend code) and into the
    /// opaque tokens the SDK hands out.
    /// The Android deployment's devices and identity.sqlite3 were created under the project's original seed: the NPSN, the
    /// device key and the NPSN-keyed Toy SDK encryption are all derived from it, so changing the seed hands an existing
    /// Android client a different identity than the one it holds and its NGS module aborts during login.
    /// Android therefore keeps the original seed; the PC build uses "schale".
    /// </summary>
    public static class IdentityPrefix
    {
        // original seed, kept byte-for-byte so existing Android identities still hash to the same values
        public static readonly string Legacy = Encoding.ASCII.GetString([0x73, 0x68, 0x69, 0x74, 0x74, 0x69, 0x6D]);
        public const string Current = "schale";

        public static string Value => Config.Instance.ServerConfiguration.IsAndroidClient ? Legacy : Current;

        /// <summary>Accepts server tokens minted under either the current or the original seed.</summary>
        public static bool IsServerToken(string token)
            => token.StartsWith(Current + ":", StringComparison.Ordinal) || token.StartsWith(Legacy + ":", StringComparison.Ordinal);
    }
}
