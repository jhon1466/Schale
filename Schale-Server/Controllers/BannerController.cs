using Microsoft.AspNetCore.Mvc;

namespace Schale_Server.Controllers;

// Lobby banner art for Management_BannerList. Nothing in the pipeline serves files - the app is MapControllers plus /health - so the image needs a route.
// Catch-all because it is not settled whether the client fetches BannerDB.Url on its own or concatenates FileName onto it; both land here.
[ApiController]
public class BannerController : ControllerBase
{
    [HttpGet("/banner/{**path}")]
    public IResult Image(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dir = Path.Combine(AppContext.BaseDirectory, "Data", "Banner");

        string? candidate = null;
        if (System.IO.File.Exists(Path.Combine(dir, name + ".png")))
        {
            candidate = Path.Combine(dir, name + ".png");
        }
        else
        {
            var cut = name.LastIndexOf('_');
            if (cut > 0 && System.IO.File.Exists(Path.Combine(dir, name[..cut] + ".png")))
            {
                candidate = Path.Combine(dir, name[..cut] + ".png");
            }
        }

        var bannerFallback = Path.Combine(dir, "Event_Banner_854.png");
        var koyukiFallback = Path.Combine(AppContext.BaseDirectory, "Data", "Koyuki", "koyuki.png");

        var file = candidate ?? (System.IO.File.Exists(bannerFallback) ? bannerFallback : koyukiFallback);
        if (!System.IO.File.Exists(file))
            return Results.NotFound();

        return Results.Bytes(System.IO.File.ReadAllBytes(file), "image/png");
    }
}
