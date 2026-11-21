using AutoMapper;
using Microsoft.EntityFrameworkCore;
using BlueArchiveAPI.Services;
using Schale.Data;
using Schale.Data.GameModel;
using Schale.Data.ModelMapping;
using Schale.MX.GameLogic.DBModel;
using Schale.MX.NetworkProtocol;
using Schale.FlatData;
using Schale_Server.Core;

namespace Schale_Server.Core.NetworkProtocol.Handlers;

public class FriendHandler : ProtocolHandlerBase
{
    private readonly ISessionKeyService _sessionService;
    private readonly IMapper _mapper;

    public FriendHandler(
        IProtocolHandlerRegistry registry,
        ISessionKeyService sessionService,
        IMapper mapper) : base(registry)
    {
        _sessionService = sessionService;
        _mapper = mapper;
    }

    private FriendDB BuildFriendDB(SchaleDataContext db, AccountDBServer target)
    {
        var attachment = db.GetAccountAttachments(target.ServerId).FirstOrDefault();
        
        var charRow = db.Characters.FirstOrDefault(c =>
            c.AccountServerId == target.ServerId && c.ServerId == target.RepresentCharacterServerId)
            ?? db.Characters.FirstOrDefault(c => c.AccountServerId == target.ServerId && c.UniqueId == target.RepresentCharacterServerId)
            ?? db.Characters.FirstOrDefault(c => c.AccountServerId == target.ServerId);

        var representUniqueId = charRow?.UniqueId ?? 10000;

        return new FriendDB
        {
            AccountId = target.ServerId,
            Nickname = target.Nickname ?? "Sensei",
            Level = Math.Max(1, target.Level),
            RepresentCharacterUniqueId = representUniqueId,
            RepresentCharacterCostumeId = 0,
            LastConnectTime = target.LastConnectTime == default ? DateTime.UtcNow : target.LastConnectTime,
            ComfortValue = 10000,
            FriendCount = target.ContentInfo?.FriendIds?.Count ?? 0,
            AttachmentDB = attachment != null 
                ? _mapper.Map<AccountAttachmentDB>(attachment) 
                : new AccountAttachmentDB { AccountId = target.ServerId, EmblemUniqueId = 0 }
        };
    }

    private async Task<(FriendDB[] friends, FriendDB[] sent, FriendDB[] received, FriendDB[] blocked)> GetFriendLists(
        SchaleDataContext db, AccountDBServer account)
    {
        var friendIds = account.ContentInfo.FriendIds.Distinct().ToList();
        var sentIds = account.ContentInfo.SentRequestFriendIds.Distinct().ToList();
        var receivedIds = account.ContentInfo.ReceivedRequestFriendIds.Distinct().ToList();
        var blockedIds = account.ContentInfo.BlockedUserIds.Distinct().ToList();

        var allIds = friendIds.Concat(sentIds).Concat(receivedIds).Concat(blockedIds).Distinct().ToList();
        var accounts = allIds.Count > 0
            ? await db.Accounts.Where(a => allIds.Contains(a.ServerId)).ToListAsync()
            : [];

        var accountMap = accounts.ToDictionary(a => a.ServerId);

        var friendDBs = friendIds
            .Where(id => accountMap.ContainsKey(id))
            .Select(id => BuildFriendDB(db, accountMap[id]))
            .ToArray();

        var sentDBs = sentIds
            .Where(id => accountMap.ContainsKey(id))
            .Select(id => BuildFriendDB(db, accountMap[id]))
            .ToArray();

        var receivedDBs = receivedIds
            .Where(id => accountMap.ContainsKey(id))
            .Select(id => BuildFriendDB(db, accountMap[id]))
            .ToArray();

        var blockedDBs = blockedIds
            .Where(id => accountMap.ContainsKey(id))
            .Select(id => BuildFriendDB(db, accountMap[id]))
            .ToArray();

        return (friendDBs, sentDBs, receivedDBs, blockedDBs);
    }

    [ProtocolHandler(Protocol.Friend_Check)]
    public async Task<FriendCheckResponse> Check(
        SchaleDataContext db,
        FriendCheckRequest request,
        FriendCheckResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        return response;
    }

    [ProtocolHandler(Protocol.Friend_List)]
    public async Task<FriendListResponse> List(
        SchaleDataContext db,
        FriendListRequest request,
        FriendListResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var (friends, sent, received, blocked) = await GetFriendLists(db, account);
        response.FriendDBs = friends;
        response.SentRequestFriendDBs = sent;
        response.ReceivedRequestFriendDBs = received;
        response.BlockedUserDBs = blocked;

        return response;
    }

    [ProtocolHandler(Protocol.Friend_ListByIds)]
    public async Task<FriendListByIdsResponse> ListByIds(
        SchaleDataContext db,
        FriendListByIdsRequest request,
        FriendListByIdsResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        response.ListResult = [];

        if (request.TargetAccountIds != null && request.TargetAccountIds.Length > 0)
        {
            var targetAccounts = await db.Accounts
                .Where(a => request.TargetAccountIds.Contains(a.ServerId))
                .ToListAsync();

            response.ListResult = targetAccounts
                .Select(t => BuildFriendDB(db, t))
                .ToArray();
        }

        return response;
    }

    [ProtocolHandler(Protocol.Friend_SendFriendRequest)]
    public async Task<FriendSendFriendRequestResponse> SendFriendRequest(
        SchaleDataContext db,
        FriendSendFriendRequestRequest request,
        FriendSendFriendRequestResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        if (request.TargetAccountId == account.ServerId)
            throw new WebAPIException(WebAPIErrorCode.FriendRequestTargetIsYourself);

        var target = await db.Accounts.FirstOrDefaultAsync(a => a.ServerId == request.TargetAccountId);
        if (target == null)
            throw new WebAPIException(WebAPIErrorCode.FriendRequestNotFound);

        if (account.ContentInfo.BlockedUserIds.Contains(target.ServerId) ||
            target.ContentInfo.BlockedUserIds.Contains(account.ServerId))
            throw new WebAPIException(WebAPIErrorCode.FriendBlockUserCannotSendRequest);

        if (account.ContentInfo.FriendIds.Contains(target.ServerId))
            throw new WebAPIException(WebAPIErrorCode.FriendAlreadyFriend);

        if (target.ContentInfo.IdCard.AutoAcceptFriendRequest)
        {
            if (!account.ContentInfo.FriendIds.Contains(target.ServerId))
                account.ContentInfo.FriendIds.Add(target.ServerId);
            if (!target.ContentInfo.FriendIds.Contains(account.ServerId))
                target.ContentInfo.FriendIds.Add(account.ServerId);

            account.ContentInfo.SentRequestFriendIds.Remove(target.ServerId);
            account.ContentInfo.ReceivedRequestFriendIds.Remove(target.ServerId);
            target.ContentInfo.SentRequestFriendIds.Remove(account.ServerId);
            target.ContentInfo.ReceivedRequestFriendIds.Remove(account.ServerId);
        }
        else
        {
            if (!account.ContentInfo.SentRequestFriendIds.Contains(target.ServerId))
                account.ContentInfo.SentRequestFriendIds.Add(target.ServerId);
            if (!target.ContentInfo.ReceivedRequestFriendIds.Contains(account.ServerId))
                target.ContentInfo.ReceivedRequestFriendIds.Add(account.ServerId);
        }

        db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
        db.Entry(target).Property(x => x.ContentInfo).IsModified = true;
        await db.SaveChangesAsync();

        var (friends, sent, received, blocked) = await GetFriendLists(db, account);
        response.FriendDBs = friends;
        response.SentRequestFriendDBs = sent;
        response.ReceivedRequestFriendDBs = received;
        response.BlockedUserDBs = blocked;

        return response;
    }

    [ProtocolHandler(Protocol.Friend_AcceptFriendRequest)]
    public async Task<FriendAcceptFriendRequestResponse> AcceptFriendRequest(
        SchaleDataContext db,
        FriendAcceptFriendRequestRequest request,
        FriendAcceptFriendRequestResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        var target = await db.Accounts.FirstOrDefaultAsync(a => a.ServerId == request.TargetAccountId);
        if (target != null)
        {
            account.ContentInfo.ReceivedRequestFriendIds.Remove(target.ServerId);
            target.ContentInfo.SentRequestFriendIds.Remove(account.ServerId);

            if (!account.ContentInfo.FriendIds.Contains(target.ServerId))
                account.ContentInfo.FriendIds.Add(target.ServerId);
            if (!target.ContentInfo.FriendIds.Contains(account.ServerId))
                target.ContentInfo.FriendIds.Add(account.ServerId);

            db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
            db.Entry(target).Property(x => x.ContentInfo).IsModified = true;
            await db.SaveChangesAsync();
        }

        var (friends, sent, received, blocked) = await GetFriendLists(db, account);
        response.FriendDBs = friends;
        response.SentRequestFriendDBs = sent;
        response.ReceivedRequestFriendDBs = received;
        response.BlockedUserDBs = blocked;

        return response;
    }

    [ProtocolHandler(Protocol.Friend_DeclineFriendRequest)]
    public async Task<FriendDeclineFriendRequestResponse> DeclineFriendRequest(
        SchaleDataContext db,
        FriendDeclineFriendRequestRequest request,
        FriendDeclineFriendRequestResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        var target = await db.Accounts.FirstOrDefaultAsync(a => a.ServerId == request.TargetAccountId);
        if (target != null)
        {
            account.ContentInfo.ReceivedRequestFriendIds.Remove(target.ServerId);
            target.ContentInfo.SentRequestFriendIds.Remove(account.ServerId);

            db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
            db.Entry(target).Property(x => x.ContentInfo).IsModified = true;
            await db.SaveChangesAsync();
        }

        var (friends, sent, received, blocked) = await GetFriendLists(db, account);
        response.FriendDBs = friends;
        response.SentRequestFriendDBs = sent;
        response.ReceivedRequestFriendDBs = received;
        response.BlockedUserDBs = blocked;

        return response;
    }

    [ProtocolHandler(Protocol.Friend_CancelFriendRequest)]
    public async Task<FriendCancelFriendRequestResponse> CancelFriendRequest(
        SchaleDataContext db,
        FriendCancelFriendRequestRequest request,
        FriendCancelFriendRequestResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        var target = await db.Accounts.FirstOrDefaultAsync(a => a.ServerId == request.TargetAccountId);
        if (target != null)
        {
            account.ContentInfo.SentRequestFriendIds.Remove(target.ServerId);
            target.ContentInfo.ReceivedRequestFriendIds.Remove(account.ServerId);

            db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
            db.Entry(target).Property(x => x.ContentInfo).IsModified = true;
            await db.SaveChangesAsync();
        }

        var (friends, sent, received, blocked) = await GetFriendLists(db, account);
        response.FriendDBs = friends;
        response.SentRequestFriendDBs = sent;
        response.ReceivedRequestFriendDBs = received;
        response.BlockedUserDBs = blocked;

        return response;
    }

    [ProtocolHandler(Protocol.Friend_Remove)]
    public async Task<FriendRemoveResponse> Remove(
        SchaleDataContext db,
        FriendRemoveRequest request,
        FriendRemoveResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        var target = await db.Accounts.FirstOrDefaultAsync(a => a.ServerId == request.TargetAccountId);
        if (target != null)
        {
            account.ContentInfo.FriendIds.Remove(target.ServerId);
            target.ContentInfo.FriendIds.Remove(account.ServerId);

            db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
            db.Entry(target).Property(x => x.ContentInfo).IsModified = true;
            await db.SaveChangesAsync();
        }

        var (friends, sent, received, blocked) = await GetFriendLists(db, account);
        response.FriendDBs = friends;
        response.SentRequestFriendDBs = sent;
        response.ReceivedRequestFriendDBs = received;
        response.BlockedUserDBs = blocked;

        return response;
    }

    [ProtocolHandler(Protocol.Friend_Block)]
    public async Task<FriendBlockResponse> Block(
        SchaleDataContext db,
        FriendBlockRequest request,
        FriendBlockResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        if (request.TargetAccountId == account.ServerId)
            throw new WebAPIException(WebAPIErrorCode.FriendBlockTargetIsYourself);

        var target = await db.Accounts.FirstOrDefaultAsync(a => a.ServerId == request.TargetAccountId);
        if (target != null)
        {
            account.ContentInfo.FriendIds.Remove(target.ServerId);
            target.ContentInfo.FriendIds.Remove(account.ServerId);
            account.ContentInfo.SentRequestFriendIds.Remove(target.ServerId);
            account.ContentInfo.ReceivedRequestFriendIds.Remove(target.ServerId);
            target.ContentInfo.SentRequestFriendIds.Remove(account.ServerId);
            target.ContentInfo.ReceivedRequestFriendIds.Remove(account.ServerId);

            if (!account.ContentInfo.BlockedUserIds.Contains(target.ServerId))
                account.ContentInfo.BlockedUserIds.Add(target.ServerId);

            db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
            db.Entry(target).Property(x => x.ContentInfo).IsModified = true;
            await db.SaveChangesAsync();
        }

        var (friends, sent, received, blocked) = await GetFriendLists(db, account);
        response.FriendDBs = friends;
        response.SentRequestFriendDBs = sent;
        response.ReceivedRequestFriendDBs = received;
        response.BlockedUserDBs = blocked;

        return response;
    }

    [ProtocolHandler(Protocol.Friend_Unblock)]
    public async Task<FriendUnblockResponse> Unblock(
        SchaleDataContext db,
        FriendUnblockRequest request,
        FriendUnblockResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        account.ContentInfo.BlockedUserIds.Remove(request.TargetAccountId);

        db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
        await db.SaveChangesAsync();

        var (friends, sent, received, blocked) = await GetFriendLists(db, account);
        response.FriendDBs = friends;
        response.SentRequestFriendDBs = sent;
        response.ReceivedRequestFriendDBs = received;
        response.BlockedUserDBs = blocked;

        return response;
    }

    [ProtocolHandler(Protocol.Friend_GetIdCard)]
    public async Task<FriendGetIdCardResponse> GetIdCard(
        SchaleDataContext db,
        FriendGetIdCardRequest request,
        FriendGetIdCardResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var card = account.ContentInfo.IdCard;
        card.Level = Math.Max(1, account.Level);
        card.FriendCode = AccountHandler.BuildFriendCode(account.ServerId);
        card.LastConnectTime = account.LastConnectTime == default ? DateTime.UtcNow : account.LastConnectTime;

        var charRow = db.Characters.FirstOrDefault(c =>
            c.AccountServerId == account.ServerId && c.ServerId == account.RepresentCharacterServerId)
            ?? db.Characters.FirstOrDefault(c => c.AccountServerId == account.ServerId && c.UniqueId == account.RepresentCharacterServerId)
            ?? db.Characters.FirstOrDefault(c => c.AccountServerId == account.ServerId);

        card.RepresentCharacterUniqueId = charRow?.UniqueId ?? 10000;
        card.RepresentCharacterCostumeId = 0;

        response.FriendIdCardDB = card;
        return response;
    }

    [ProtocolHandler(Protocol.Friend_SetIdCard)]
    public async Task<FriendSetIdCardResponse> SetIdCard(
        SchaleDataContext db,
        FriendSetIdCardRequest request,
        FriendSetIdCardResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var card = account.ContentInfo.IdCard;
        card.Comment = request.Comment;
        card.RepresentCharacterUniqueId = request.RepresentCharacterUniqueId;
        card.EmblemId = request.EmblemId;
        card.SearchPermission = request.SearchPermission;
        card.AutoAcceptFriendRequest = request.AutoAcceptFriendRequest;
        card.ShowAccountLevel = request.ShowAccountLevel;
        card.ShowFriendCode = request.ShowFriendCode;
        card.ShowRaidRanking = request.ShowRaidRanking;
        card.ShowArenaRanking = request.ShowArenaRanking;
        card.ShowEliminateRaidRanking = request.ShowEliminateRaidRanking;
        card.CardBackgroundId = request.BackgroundId;

        db.Entry(account).Property(x => x.ContentInfo).IsModified = true;
        await db.SaveChangesAsync();

        return response;
    }

    [ProtocolHandler(Protocol.Friend_Search)]
    public async Task<FriendSearchResponse> Search(
        SchaleDataContext db,
        FriendSearchRequest request,
        FriendSearchResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var rawCode = request.FriendCode?.Trim();
        if (!string.IsNullOrWhiteSpace(rawCode))
        {
            var upperCode = rawCode.ToUpperInvariant();
            var isNumeric = long.TryParse(rawCode, out var targetId);

            var matches = db.Accounts.AsEnumerable().Where(a =>
                a.ServerId != account.ServerId &&
                a.ServerId > 1 &&
                (
                    (isNumeric && (a.ServerId == targetId || a.PublisherAccountId == targetId)) ||
                    AccountHandler.BuildFriendCode(a.ServerId) == upperCode ||
                    (a.Nickname != null && string.Equals(a.Nickname, rawCode, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(a.ContentInfo?.IdCard?.FriendCode, upperCode, StringComparison.OrdinalIgnoreCase)
                )
            ).ToList();

            if (matches.Count == 0)
            {
                response.SearchResult = [];
                return response;
            }

            response.SearchResult = matches.Select(m => BuildFriendDB(db, m)).ToArray();
            return response;
        }

        // Recommend/browse: return real player accounts (ServerId > 1) not self, not blocked, not already requested/friends
        var candidates = await db.Accounts
            .Where(a => a.ServerId != account.ServerId && a.ServerId > 1)
            .ToListAsync();

        var filtered = candidates
            .Where(a => !account.ContentInfo.BlockedUserIds.Contains(a.ServerId) &&
                        !account.ContentInfo.FriendIds.Contains(a.ServerId) &&
                        !account.ContentInfo.SentRequestFriendIds.Contains(a.ServerId))
            .Take(15)
            .Select(c => BuildFriendDB(db, c))
            .ToArray();

        response.SearchResult = filtered;
        return response;
    }

    [ProtocolHandler(Protocol.Friend_GetFriendDetailedInfo)]
    public async Task<FriendGetFriendDetailedInfoResponse> GetFriendDetailedInfo(
        SchaleDataContext db,
        FriendGetFriendDetailedInfoRequest request,
        FriendGetFriendDetailedInfoResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var target = await db.Accounts.FirstOrDefaultAsync(a => a.ServerId == request.FriendAccountId);
        if (target == null)
            throw new WebAPIException(WebAPIErrorCode.FriendRequestNotFound);

        var attachment = db.GetAccountAttachments(target.ServerId).FirstOrDefault();
        var card = target.ContentInfo.IdCard;

        var charRow = db.Characters.FirstOrDefault(c =>
            c.AccountServerId == target.ServerId && c.ServerId == target.RepresentCharacterServerId)
            ?? db.Characters.FirstOrDefault(c => c.AccountServerId == target.ServerId && c.UniqueId == target.RepresentCharacterServerId)
            ?? db.Characters.FirstOrDefault(c => c.AccountServerId == target.ServerId);

        var representUniqueId = charRow?.UniqueId ?? 10000;
        var friendCode = AccountHandler.BuildFriendCode(target.ServerId);
        var charCount = await db.Characters.CountAsync(c => c.AccountServerId == target.ServerId);

        response.Nickname = target.Nickname ?? "Sensei";
        response.Level = Math.Max(1, target.Level);
        response.ClanName = "Schale Network";
        response.Comment = card.Comment ?? target.Comment ?? string.Empty;
        response.FriendCount = target.ContentInfo?.FriendIds?.Count ?? 0;
        response.FriendCode = friendCode;
        response.RepresentCharacterUniqueId = representUniqueId;
        response.RepresentCharacterCostumeId = 0;
        response.CharacterCount = charCount;
        response.AttachmentDB = attachment != null 
            ? _mapper.Map<AccountAttachmentDB>(attachment) 
            : new AccountAttachmentDB { AccountId = target.ServerId, EmblemUniqueId = 0 };
        response.AssistCharacterDBs = [];

        response.DetailedAccountInfoDB = new DetailedAccountInfoDB
        {
            AccountId = target.ServerId,
            Nickname = response.Nickname,
            Level = response.Level,
            ClanName = response.ClanName,
            Comment = response.Comment,
            FriendCode = friendCode,
            RepresentCharacterUniqueId = representUniqueId,
            CharacterCount = charCount,
            AssistCharacterDBs = []
        };

        return response;
    }
}
