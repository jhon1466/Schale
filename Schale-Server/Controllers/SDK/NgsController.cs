using Microsoft.AspNetCore.Mvc;

namespace Schale_Server.Controllers.SDK
{
    // Nexon Game Shield (NGS) endpoints, as reached by the Android client through AnimeGamesProxy, which rewrites
    // x-init.ngs.nexon.com/v1 to <server>/v1. On Steam redirect_server.py answers these inside mitmproxy instead, so the
    // server never saw them and IasController's {*path} catch-all answered them with an IAS login body, which NGS rejected
    // in a retry loop before the client hung on a black screen.
    [ApiController]
    public class NgsController : ControllerBase
    {
        private static readonly Lazy<byte[]> XInitResponse = new(LoadXInitResponse);
        private readonly ILogger<NgsController> logger;

        public NgsController(ILogger<NgsController> logger)
        {
            this.logger = logger;
        }

        // x-init.ngs.nexon.com/v1: same captured body and "CE: G" header redirect_server.py serves.
        [HttpPost("/v1")]
        public IResult XInit()
        {
            LogRequest("x-init /v1");
            Response.Headers["CE"] = "G";
            return Results.Bytes(XInitResponse.Value, "application/json");
        }

        // x-phaethon / x-csauth: redirect_server.py answers these with an empty 200.
        [HttpPost("/v2")]
        public IResult Phaethon()
        {
            LogRequest("/v2");
            return Results.Bytes(Array.Empty<byte>(), "text/plain");
        }

        private void LogRequest(string name)
        {
            // The Android client posts JSON here repeatedly; log the start of the body to confirm it really is NGS
            // and not an IAS call sharing the bare /v1 path.
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = reader.ReadToEndAsync().GetAwaiter().GetResult();
            Request.Body.Position = 0;
            logger.LogInformation("[NGS] {Name} content-type={ContentType} length={Length} body={Body}",
                name, Request.ContentType, Request.ContentLength, body.Length > 400 ? body[..400] : body);
        }

        private static byte[] LoadXInitResponse()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Config", "ngs_x_init_response.bin");
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : Array.Empty<byte>();
        }
    }
}
