using System.Runtime.CompilerServices;
using AutoMapper;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.Data.ModelMapping;
using Schale.MX.NetworkProtocol;

namespace Schale_Server.Services;

/// <summary>
/// Remembers which mission rows changed while one request was being handled (each request gets its own DbContext),
/// so the dispatcher can attach them to that request's response. The client only plays its "task complete" toast and
/// refreshes the counters from MissionProgressDBs on the response of the action that moved them; progress that is only
/// saved shows up on the next mission screen sync, silently.
/// </summary>
public static class MissionChangeLog
{
    private static readonly ConditionalWeakTable<SchaleDataContext, Dictionary<long, MissionProgressDBServer>> Changes = new();

    public static void Record(SchaleDataContext context, MissionProgressDBServer row)
        => Changes.GetOrCreateValue(context)[row.MissionUniqueId] = row;

    /// <summary>Adds every mission changed during the request that the handler did not already report.</summary>
    public static void AttachTo(SchaleDataContext context, ResponsePacket response, IMapper mapper, ISet<long> missionScreenIds)
    {
        if (!Changes.TryGetValue(context, out var changed) || changed.Count == 0)
            return;
        Changes.Remove(context);

        var reported = response.MissionProgressDBs?.Select(x => x.MissionUniqueId).ToHashSet() ?? [];
        var extra = changed.Values
            .Where(r => missionScreenIds.Contains(r.MissionUniqueId) && !reported.Contains(r.MissionUniqueId))
            .Select(r => r.ToMap(mapper))
            .ToList();
        if (extra.Count == 0)
            return;

        response.MissionProgressDBs ??= [];
        response.MissionProgressDBs.AddRange(extra);
    }
}
