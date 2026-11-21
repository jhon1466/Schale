using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Schale_Server.Controllers.SDK
{
    // Nexon's stamp (IAP/cash-shop) service. The client calls it on every lobby entry and any failure surfaces as a modal over the loaded lobby.
    // gamescale.core.dll's response handler (sub_1800F0490) accepts only HTTP 200: 400/500 map to 0x2FCDB394, anything else including 404 to 0x2FCDB396, both remapped to 801010009 on screen. The body must parse as JSON (else 0x2FCDB395) but field lookups are lenient, so {} answers anything unmodelled - hence the catch-all instead of per-path routes.
    // Prefixes vary by environment (/stamp/live, /stamp/live01, /stamp/pre, /stamp/{alpha,qa,qa02}/public), so the match is on the suffix.
    [ApiController]
    public class StampController : ControllerBase
    {
        // Opaque handles the client echoes back on later stamp calls (issue/purchase), which an offline server never completes, so stable placeholders suffice.
        private const string StampId = "schale-stamp-00000000-0000-0000-0000-000000000001";
        private const string StampToken = "schale-stamp-token";

        private readonly ILogger<StampController> _logger;

        public StampController(ILogger<StampController> logger)
        {
            _logger = logger;
        }

        [AcceptVerbs("GET", "POST", "PUT", "DELETE", "OPTIONS", Route = "/stamp/{**path}")]
        public IResult Handle(string? path)
        {
            var suffix = "/" + (path ?? string.Empty);
            object body;

            if (Ends(suffix, "/v1/enter"))
            {
                body = new
                {
                    stamp_id = StampId,
                    stamp_token = StampToken,
                    direct_payment_url = ""
                };
            }
            else if (Ends(suffix, "/products"))
            {
                // Nothing is for sale offline; an empty list is a valid "no products" and the client renders the shop from its own excel anyway.
                body = new { product_infos = Array.Empty<object>() };
            }
            else if (suffix.Contains("/v1/restore/", StringComparison.OrdinalIgnoreCase))
            {
                // PlatformService deserializes this endpoint into RetRestorePurchase,
                // whose wire members are uppercase. An empty transaction-list object
                // is not that contract and can be interpreted as a failed restore.
                body = new Dictionary<string, object>
                {
                    ["RESULT_CODE"] = 0,
                    ["RESULT_DESCRIPTION"] = "",
                    ["PRODUCTS"] = Array.Empty<object>(),
                    ["IS_TEENAGE"] = false
                };
            }
            else if (Ends(suffix, "/v2/stamp/status")
                || suffix.Contains("/v2/restore/", StringComparison.OrdinalIgnoreCase))
            {
                // Pending-transaction poll and purchase-restore; report none so the client never waits on a delivery. Both spellings of the list appear in the SDK.
                body = new
                {
                    transactions = Array.Empty<object>(),
                    billingTransactions = Array.Empty<object>()
                };
            }
            else if (Ends(suffix, "/v1/histories"))
            {
                // Android inpay purchase history, polled on lobby entry. With {} the client went on to an empty
                // Billing_PurchaseCashShopVerifyByNexon and showed "Your product is on the way" every time; report no purchases.
                body = new
                {
                    histories = Array.Empty<object>(),
                    history_list = Array.Empty<object>(),
                    transactions = Array.Empty<object>(),
                    billingTransactions = Array.Empty<object>(),
                    total_count = 0
                };
            }
            else
            {
                body = new { };
                _logger.LogWarning("[stamp] unmodelled endpoint {Method} /stamp/{Path} - answering 200 {{}}",
                    Request.Method, path);
            }

            _logger.LogInformation("[stamp] {Method} /stamp/{Path}{Query}",
                Request.Method, path, Request.QueryString.Value);

            return Results.Json(body);
        }

        private static bool Ends(string suffix, string tail)
            => suffix.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
    }
}
