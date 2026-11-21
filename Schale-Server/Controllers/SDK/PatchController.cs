using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Schale_Server.Controllers.SDK
{
    [ApiController]
    [Route("patch/v1.1")]
    public class PatchController : ControllerBase
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private readonly ILogger<PatchController> _logger;
        private const string UpstreamVersionCheckUrl = "https://api-pub.nexon.com/patch/v1.1/version-check";
        private const string DefaultResourcePath = "https://ba.dn.nexoncdn.co.kr/com.nexon.bluearchive/aa8899b35d6b4866/resource-data.json";

        public PatchController(ILogger<PatchController> logger)
        {
            _logger = logger;
        }

        private static string GetResourcePath()
        {
            try
            {
                var cachePath = "./config/ba_version_cache.json";
                if (System.IO.File.Exists(cachePath))
                {
                    var cache = JsonSerializer.Deserialize<JsonElement>(System.IO.File.ReadAllText(cachePath));
                    if (cache.TryGetProperty("VersionId", out var vid))
                    {
                        var v = vid.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                            return $"https://ba.dn.nexoncdn.co.kr/com.nexon.bluearchive/{v}/resource-data.json";
                    }
                }
            }
            catch { }
            return DefaultResourcePath;
        }

        [HttpPost("version-check")]
        public async Task<IActionResult> VersionCheck()
        {
            string body;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            string buildVersion = "1.96.474931";
            string buildNumber = "474931";
            int patchVersion = 1789;
            string language = "es";
            string marketGameId = "com.nexon.bluearchive";
            string upstreamBodyToSend = body;

            try
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var node = JsonNode.Parse(body) as JsonObject ?? new JsonObject();
                    if (node.TryGetPropertyValue("curr_build_version", out var bv) && bv != null)
                        buildVersion = bv.ToString();
                    if (node.TryGetPropertyValue("curr_build_number", out var bn) && bn != null)
                        buildNumber = bn.ToString();
                    if (node.TryGetPropertyValue("curr_patch_version", out var pv) && pv != null)
                        int.TryParse(pv.ToString(), out patchVersion);
                    if (node.TryGetPropertyValue("language", out var lang) && lang != null)
                        language = lang.ToString();
                    if (node.TryGetPropertyValue("market_game_id", out var mgid) && mgid != null)
                        marketGameId = mgid.ToString();

                    // Ensure market_code is present for Nexon patch API
                    if (!node.ContainsKey("market_code") || node["market_code"] == null)
                    {
                        node["market_code"] = "playstore";
                    }
                    upstreamBodyToSend = node.ToJsonString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse incoming version-check body");
            }

            // Try upstream first
            try
            {
                using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, UpstreamVersionCheckUrl);
                upstreamReq.Content = new StringContent(upstreamBodyToSend, Encoding.UTF8, "application/json");
                upstreamReq.Headers.Add("User-Agent", "Dalvik/2.1.0");

                using var response = await _httpClient.SendAsync(upstreamReq);
                if (response.IsSuccessStatusCode)
                {
                    var upstreamBody = await response.Content.ReadAsStringAsync();
                    _logger.LogInformation("Upstream patch version-check succeeded for build {Version}", buildVersion);
                    return Content(upstreamBody, "application/json");
                }
                else
                {
                    _logger.LogWarning("Upstream patch returned status {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Upstream Nexon patch check failed or timed out, returning fallback");
            }

            // Fallback: report client version as latest so no unpacking/patching is required
            var fallback = new
            {
                api_version = "v1.1",
                market_game_id = marketGameId,
                latest_build_version = buildVersion,
                latest_build_number = buildNumber,
                min_build_version = buildVersion,
                min_build_number = buildNumber,
                language = language,
                patch = new
                {
                    patch_version = 1789,
                    resource_path = GetResourcePath(),
                    bdiff_path = Array.Empty<object>()
                }
            };

            return Ok(fallback);
        }

        [HttpGet("version-check.json")]
        [HttpPost("version-check.json")]
        public IActionResult VersionCheckJson()
        {
            var fallback = new
            {
                api_version = "v1.1",
                market_game_id = "com.nexon.bluearchive",
                latest_build_version = "1.96.474931",
                latest_build_number = "474931",
                min_build_version = "1.96.474931",
                min_build_number = "474931",
                language = "es",
                patch = new
                {
                    patch_version = 1789,
                    resource_path = GetResourcePath(),
                    bdiff_path = Array.Empty<object>()
                }
            };
            return Ok(fallback);
        }

        [HttpGet("static-resource")]
        [HttpPost("static-resource")]
        public IActionResult StaticResource()
        {
            var res = new
            {
                api_version = "v1.1",
                patch_version = 1789,
                resource_path = GetResourcePath(),
                resources = Array.Empty<object>()
            };
            return Ok(res);
        }

        [HttpGet("group/group-data.json")]
        [HttpPost("group/group-data.json")]
        public IActionResult GroupData()
        {
            return Ok(new
            {
                api_version = "v1.1",
                groups = Array.Empty<object>()
            });
        }

        [HttpGet("sdk-config.json")]
        [HttpPost("sdk-config.json")]
        public IActionResult SdkConfig()
        {
            return Ok(new
            {
                api_version = "v1.1"
            });
        }

        [HttpPost("report")]
        public IActionResult Report()
        {
            return Ok(new
            {
                api_version = "v1.1",
                status = 200
            });
        }
    }
}
