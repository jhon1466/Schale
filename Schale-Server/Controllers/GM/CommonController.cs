using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Schale.Data;
using Schale_Server.Services;
using Schale_Server.GameClient;

namespace Schale.Controllers.GM
{
    [ApiController]
    [Route("/dev/api")]
    public class CommonController : ControllerBase
    {
        private readonly IDbContextFactory<SchaleDataContext> contextFactory;

        public CommonController(IDbContextFactory<SchaleDataContext> _contextFactory)
        {
            contextFactory = _contextFactory;
        }

        [HttpGet]
        [Route("hina")]
        public async Task<IResult> HinaCheck()
        {
            try
            {
                await using var context = await contextFactory.CreateDbContextAsync();
                bool isReady = await context.Accounts.AnyAsync(account => account.ServerId != SchaleAI.AccountId);
                return Results.Ok(isReady);
            }
            catch (Exception)
            {
                return Results.Ok(false);
            }
        }
    }
}
