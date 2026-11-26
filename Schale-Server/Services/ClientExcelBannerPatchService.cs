using BlueArchiveAPI.Configuration;
using BlueArchiveAPI.Services;
using Microsoft.Data.Sqlite;
using Schale.Crypto;
using Schale.FlatData;

namespace Schale_Server.Services
{
    // The "View All Recruitments" list is built client-side from the client's encrypted ExcelDB.db; the server never serves it.
    // Active recruitment rows with an empty GachaBannerPath draw as a blank but still selectable banner, and the empty slot pushes the real ones into a gap.
    //
    // So this patches the source: at startup it opens ExcelDB.db (SQLCipher, same key ExcelTableService uses) and fills each empty GachaBannerPath with the art of the most closely related banner - longest shared Id prefix, meaning the same event family.
    // Same idea as ClientMetadataPatchService and global-metadata.dat.
    public class ClientExcelBannerPatchService : IHostedService
    {
        private const string SchemaTable = "ShopRecruitDBSchema";

        private readonly ILogger<ClientExcelBannerPatchService> logger;

        public ClientExcelBannerPatchService(ILogger<ClientExcelBannerPatchService> logger)
        {
            this.logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!Config.Instance.ServerConfiguration.AutoPatchClientBanners)
                    return Task.CompletedTask;

                var dbPath = GetExcelDbPath();
                if (!string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath))
                {
                    PatchBanners(dbPath, logger);
                }
                else if (!Config.Instance.ServerConfiguration.IsAndroidClient)
                {
                    logger.LogWarning("Client banner auto-patch enabled but client ExcelDB.db was not found ({Path})", dbPath);
                }

                EnsureServerPatchedExcelDb();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to patch client recruitment banners");
            }

            return Task.CompletedTask;
        }

        internal static string ServerPatchedExcelDbPath =>
            Path.Combine(ResourceService.ResourceDir, "Patched", "ExcelDB.db");

        internal static string ServerPatchedCatalogPath =>
            Path.Combine(ResourceService.ResourceDir, "Patched", "TableCatalog.bytes");

        private void EnsureServerPatchedExcelDb()
        {
            var source = Path.Combine(ResourceService.DumpedDir, "ExcelDB.db");
            if (!File.Exists(source))
                return;

            var target = ServerPatchedExcelDbPath;
            var dir = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(dir);

            var versionMarker = Path.Combine(dir, "version.txt");
            var versionId = Config.Instance.ServerConfiguration.VersionId ?? string.Empty;
            var needsRefresh = !File.Exists(target)
                || !File.Exists(versionMarker)
                || File.ReadAllText(versionMarker) != versionId;

            if (needsRefresh)
            {
                File.Copy(source, target, overwrite: true);
                File.WriteAllText(versionMarker, versionId);
                if (File.Exists(ServerPatchedCatalogPath))
                    File.Delete(ServerPatchedCatalogPath);
            }

            PatchBanners(target, logger);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        internal static void PatchBanners(string dbPath, ILogger? logger = null)
        {
            SqliteProvider.EnsureInitialized();
            // ExcelDB flatbuffer strings are stored plaintext (the SQLCipher layer is the only encryption); read and write them as-is so non-modified fields round-trip byte-equal.
            TableEncryptionService.UseEncryption = false;

            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            conn.Open();
            ApplyKey(conn);

            // ShopRecruitExcel has drifted - the client inserted two fields mid-table since the models were generated - so a straight UnPack walks a later field's offset into the middle of another one and throws.
            var slots = ClientExcelSchema.RowSlots(typeof(ShopRecruitExcelT), "ShopRecruitExcel");
            var artSlot = slots[Array.FindIndex(typeof(ShopRecruitExcelT).GetProperties(), p => p.Name == nameof(ShopRecruitExcelT.GachaBannerPath))];
            var endSlot = slots[Array.FindIndex(typeof(ShopRecruitExcelT).GetProperties(), p => p.Name == nameof(ShopRecruitExcelT.SalePeriodTo))];

            var rows = new List<BannerRow>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT rowid, Bytes FROM [{SchemaTable}]";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var rowid = reader.GetInt64(0);
                    var bytes = (byte[])reader[1];
                    var rec = (ShopRecruitExcelT)RealignedRowReader.Read(bytes, typeof(ShopRecruitExcelT), slots);
                    rows.Add(new BannerRow(rowid, rec.Id, rec.GachaBannerPath ?? string.Empty, rec.SalePeriodTo, bytes));
                }
            }

            var withArt = rows.Where(r => !string.IsNullOrEmpty(r.Art)).ToList();
            if (withArt.Count == 0)
            {
                logger?.LogWarning("Client recruitment banners: no banner carries art to copy; skipping");
                return;
            }

            var now = DateTime.Now;
            // Rows that ship without art. Read from the pristine dump too, because older builds of this service already filled
            // these rows in the client copy with a sibling's art - which made several different recruitments share one image.
            var originallyEmpty = rows.Where(r => string.IsNullOrEmpty(r.Art)).Select(r => r.Id).ToHashSet();
            originallyEmpty.UnionWith(PristineEmptyArtIds(dbPath, slots, logger));

            // Only touch banners that can still be displayed (no end date / future / unparseable), so we don't rewrite hundreds of expired historical rows.
            var targets = rows
                .Where(r => originallyEmpty.Contains(r.Id))
                .Where(r => !IsExpired(r.SalePeriodTo, now))
                .ToList();

            if (targets.Count == 0)
            {
                logger?.LogInformation("Client recruitment banners: no displayable empty-art rows to patch");
                return;
            }

            var patched = 0;
            using (var tx = conn.BeginTransaction())
            {
                foreach (var target in targets)
                {
                    // Hide it: the client lists recruitments by sale period, so an end date in the past drops the row from
                    // "View All Recruitments" instead of drawing it with someone else's banner art.
                    var newBytes = RealignedRowReader.WithString(target.Bytes, endSlot, HiddenSaleEnd);
                    var art = "(hidden)";
                    if (newBytes == null)
                    {
                        // No end-date field to point at (undated row): keep the old behaviour so it is at least not blank.
                        if (!string.IsNullOrEmpty(target.Art)) continue;
                        art = PickRelatedArt(target.Id, withArt);
                        newBytes = RealignedRowReader.WithString(target.Bytes, artSlot, art);
                        if (newBytes == null) continue;
                    }

                    using var upd = conn.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = $"UPDATE [{SchemaTable}] SET Bytes = @b WHERE rowid = @r";
                    upd.Parameters.Add("@b", SqliteType.Blob).Value = newBytes;
                    upd.Parameters.Add("@r", SqliteType.Integer).Value = target.RowId;
                    upd.ExecuteNonQuery();
                    patched++;

                    logger?.LogInformation("Banner {Id}: no own art -> {Art}", target.Id, art);
                }
                tx.Commit();
            }

            logger?.LogInformation("Patched {Count} empty recruitment banner art path(s) in client ExcelDB", patched);
        }

        private const string HiddenSaleEnd = "2000-01-01 00:00:00";

        private static IEnumerable<long> PristineEmptyArtIds(string dbPath, int[] slots, ILogger? logger)
        {
            var pristine = Path.Combine(ResourceService.DumpedDir, "ExcelDB.db");
            if (!File.Exists(pristine) || string.Equals(Path.GetFullPath(pristine), Path.GetFullPath(dbPath), StringComparison.OrdinalIgnoreCase))
                return [];
            try
            {
                using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = pristine, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                conn.Open();
                ApplyKey(conn);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT Bytes FROM [{SchemaTable}]";
                using var reader = cmd.ExecuteReader();
                var ids = new List<long>();
                while (reader.Read())
                {
                    var rec = (ShopRecruitExcelT)RealignedRowReader.Read((byte[])reader[0], typeof(ShopRecruitExcelT), slots);
                    if (string.IsNullOrEmpty(rec.GachaBannerPath)) ids.Add(rec.Id);
                }
                return ids;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not read the pristine ExcelDB to find art-less banners");
                return [];
            }
        }

        // Reuse the art of the banner whose Id shares the longest leading-digit run -> same event/family, so the blank slot shows that event's real banner image.
        private static string PickRelatedArt(long emptyId, List<BannerRow> withArt)
        {
            var key = emptyId.ToString();
            var best = withArt[0].Art;
            var bestScore = -1;
            foreach (var candidate in withArt)
            {
                var score = CommonPrefixLength(key, candidate.Id.ToString());
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate.Art;
                }
            }
            return best;
        }

        private static int CommonPrefixLength(string a, string b)
        {
            var n = Math.Min(a.Length, b.Length);
            var i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i;
        }

        private static bool IsExpired(string salePeriodTo, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(salePeriodTo))
                return false;
            return DateTime.TryParse(salePeriodTo, out var to) && to < now;
        }

        private static void ApplyKey(SqliteConnection conn)
        {
            var key = Environment.GetEnvironmentVariable("SCHALE_EXCELDB_SQLCIPHER_KEY");
            if (string.IsNullOrWhiteSpace(key))
                key = Config.Instance.ServerConfiguration.ExcelDbSqlCipherKey;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA key = \"x'{key.Trim()}'\";";
            cmd.ExecuteNonQuery();
        }

        private static string GetExcelDbPath()
        {
            var configured = Environment.GetEnvironmentVariable("SCHALE_CLIENT_EXCELDB_PATH");
            if (string.IsNullOrWhiteSpace(configured))
                configured = Config.Instance.ServerConfiguration.ClientExcelDbPath;
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            const string relative = @"BlueArchive_Data\StreamingAssets\PUB\Resource\Preload\TableBundles\ExcelDB.db";

            // Derive from the configured client-metadata install root (same install tree).
            var metaPath = Config.Instance.ServerConfiguration.ClientMetadataPath;
            if (!string.IsNullOrWhiteSpace(metaPath))
            {
                var idx = metaPath.IndexOf(@"\BlueArchive_Data", StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    var candidate = Path.Combine(metaPath[..idx], relative);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            // Any Steam library can hold the install.
            return SteamGameLocator.FindGameFile(relative) ?? string.Empty;
        }

        private readonly record struct BannerRow(long RowId, long Id, string Art, string SalePeriodTo, byte[] Bytes);
    }
}
