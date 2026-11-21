using Microsoft.AspNetCore.Mvc;
using BlueArchiveAPI.Configuration;
using Schale_Server.Services;

namespace Schale_Server.Controllers
{
    [ApiController]
    [Route("addressables")]
    public class AddressablesController : ControllerBase
    {
        private readonly ModCatalogService _catalog;
        private readonly ILogger<AddressablesController> _logger;

        public AddressablesController(ModCatalogService catalog, ILogger<AddressablesController> logger)
        {
            _catalog = catalog;
            _logger = logger;
        }

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly SemaphoreSlim _catalogLock = new(1, 1);

        // The catalog sits under a path the client derives from the root it was handed and that layout moves between versions, so everything below the root is caught and sorted out by filename instead of being routed.
        [HttpGet("{**path}")]
        public async Task<IActionResult> Get(string path)
        {
            if (string.IsNullOrEmpty(path))
                return NotFound();

            // This endpoint is intentionally public: the Android game downloads its signed CDN resources here.
            // The host authorization filter is not applied to this controller; keep admin endpoints separate.

            var file = Path.GetFileName(path);
            if (file.StartsWith("catalog_", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Catalog request: {Path}", path);
                var current = _catalog.Current();
                if (file.EndsWith(".hash", StringComparison.OrdinalIgnoreCase))
                    return Content(current.Hash, "text/plain");
                if (file.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
                    return File(current.Bytes, "application/octet-stream");
            }

            if (path.EndsWith("Preload/TableBundles/ExcelDB.db", StringComparison.OrdinalIgnoreCase)
                && System.IO.File.Exists(ClientExcelBannerPatchService.ServerPatchedExcelDbPath))
            {
                _logger.LogInformation("Serving banner-patched ExcelDB.db for {Path}", path);
                return PhysicalFile(ClientExcelBannerPatchService.ServerPatchedExcelDbPath, "application/octet-stream", enableRangeProcessing: true);
            }

            if ((file.Equals("TableCatalog.bytes", StringComparison.OrdinalIgnoreCase)
                    || file.Equals("TableCatalog.hash", StringComparison.OrdinalIgnoreCase))
                && System.IO.File.Exists(ClientExcelBannerPatchService.ServerPatchedExcelDbPath))
            {
                var bytesPath = await EnsurePatchedTableCatalogAsync(path);
                if (bytesPath != null)
                {
                    var blob = await System.IO.File.ReadAllBytesAsync(bytesPath);
                    if (file.Equals("TableCatalog.hash", StringComparison.OrdinalIgnoreCase))
                        return Content(ModCatalogService.Crc32Bytes(blob).ToString(), "text/plain");
                    return File(blob, "application/octet-stream");
                }
            }

            if (path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            {
                var mods = Path.GetFullPath(CustomCharacterService.ModsDir);
                var full = Path.GetFullPath(Path.Combine(mods, path[5..].Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(mods, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(full))
                    return NotFound();
                return PhysicalFile(full, "application/octet-stream");
            }

            return Redirect($"{Config.Instance.ServerConfiguration.CdnBaseUrl}/{path}");
        }

        private async Task<string?> EnsurePatchedTableCatalogAsync(string requestPath)
        {
            var cached = ClientExcelBannerPatchService.ServerPatchedCatalogPath;
            if (System.IO.File.Exists(cached))
                return cached;

            await _catalogLock.WaitAsync();
            try
            {
                if (System.IO.File.Exists(cached))
                    return cached;

                var bytesPath = requestPath.EndsWith(".hash", StringComparison.OrdinalIgnoreCase)
                    ? requestPath[..^5] + ".bytes"
                    : requestPath;
                var url = $"{Config.Instance.ServerConfiguration.CdnBaseUrl}/{bytesPath}";
                var blob = await _http.GetByteArrayAsync(url);
                ModCatalogService.SyncCatalogEntry(blob, "ExcelDB.db", ClientExcelBannerPatchService.ServerPatchedExcelDbPath);
                Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
                await System.IO.File.WriteAllBytesAsync(cached, blob);
                return cached;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch/sync TableCatalog.bytes from CDN; falling back to redirect");
                return null;
            }
            finally
            {
                _catalogLock.Release();
            }
        }
    }
}
