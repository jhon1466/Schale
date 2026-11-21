using Schale.Data.GameModel;
using BlueArchiveAPI.Services;
using Schale.Data;
using Schale.MX.GameLogic.DBModel;
using Schale.MX.GameLogic.Parcel;
using Schale.MX.NetworkProtocol;
using Schale.FlatData;
using Serilog;
using Schale_Server.Core;
using Schale_Server.Services;

namespace Schale_Server.Core.NetworkProtocol.Handlers;

public class BillingHandler : ProtocolHandlerBase
{
    private readonly ISessionKeyService _sessionService;
    private readonly ExcelTableService _excelTableService;
    private readonly ParcelHandler _parcelHandler;

    public BillingHandler(
        IProtocolHandlerRegistry registry,
        ISessionKeyService sessionService,
        ExcelTableService excelTableService,
        ParcelHandler parcelHandler) : base(registry)
    {
        _sessionService = sessionService;
        _excelTableService = excelTableService;
        _parcelHandler = parcelHandler;
    }

    [ProtocolHandler(Protocol.Billing_PurchaseListByNexon)]
    public async Task<BillingPurchaseListByNexonResponse> PurchaseListByNexon(
        SchaleDataContext db,
        BillingPurchaseListByNexonRequest request,
        BillingPurchaseListByNexonResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        response.CountList = CurrentCashPurchases(db, account);
        response.OrderList = [];
        response.MonthlyProductList = [];
        response.BlockedProductDBs = [];
        response.GachaTicketItemIdList = [];
        response.ProductMonthlyIdInMailList = [];
        // Official's standalone response carries this (as [] when empty) and has no IsTeenage key - that's a request-side field.
        response.DailyRecordIdInMailList = [];
        
        var battlePasses = db.BattlePasses.Where(x => x.AccountServerId == account.ServerId && x.PurchaseGroupId != 0).ToList();
        var battlePassProductList = new List<BattlePassProductPurchaseDB>();

        if (battlePasses.Any())
        {
            var productExcels = _excelTableService.GetTable<ProductExcelT>();
            var shopCashExcels = _excelTableService.GetTable<ShopCashExcelT>();

            foreach (var bp in battlePasses)
            {
                Log.Debug("[BillingHandler] BattlePassId: {BattlePassId}. Total Products: {ProductCount}", bp.BattlePassId, productExcels.Count);

                var battlePassProducts = productExcels.Where(x => x.ParcelType.Contains(ParcelType.ProductBattlePass)).ToList();
                Log.Debug("[BillingHandler] Found {MatchCount} products containing ProductBattlePass type.", battlePassProducts.Count);

                ProductExcelT product = null;
                
                product = battlePassProducts.FirstOrDefault(x => {
                    var idx = x.ParcelType.IndexOf(ParcelType.ProductBattlePass);
                    return idx >= 0 && idx < x.ParcelId.Count && x.ParcelId[idx] == bp.BattlePassId;
                });

                if (product != null)
                {
                    var shopCash = shopCashExcels.FirstOrDefault(x => x.CashProductId == product.Id);
                    
                    if (shopCash != null)
                    {
                        battlePassProductList.Add(new BattlePassProductPurchaseDB
                        {
                            ProductId = shopCash.Id, // Billing response expects ShopCashId as ProductId
                            BattlePassId = bp.BattlePassId,
                            PurchaseBattlePassGroupId = bp.PurchaseGroupId
                        });
                        Log.Debug("[BillingHandler] Mapped BattlePassId {BattlePassId} to ShopCashId {ShopCashId} via ProductId {ProductId}",
                            bp.BattlePassId, shopCash.Id, product.Id);
                    }
                    else
                    {
                        Log.Warning("[BillingHandler] ShopCash not found for ProductId: {ProductId} (BattlePassId: {BattlePassId})", product.Id, bp.BattlePassId);
                    }
                }
                else
                {
                    Log.Warning("[BillingHandler] No product found for BattlePassId: {BattlePassId} among {CandidateCount} candidates.", bp.BattlePassId, battlePassProducts.Count);
                }
            }
        }
        else
        {
            Log.Debug("[BillingHandler] No BattlePasses found for Account: {AccountServerId} with PurchaseGroupId != 0", account.ServerId);
        }

        response.BattlePassProductList = battlePassProductList;
        response.BattlePassIdInMailList = [];

        return response;
    }

    [ProtocolHandler(Protocol.Billing_PurchaseListByYostar)]
    public async Task<BillingPurchaseListByYostarResponse> PurchaseListByYostar(
        SchaleDataContext db,
        BillingPurchaseListByYostarRequest request,
        BillingPurchaseListByYostarResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        response.CountList = [];
        response.OrderList = [];
        response.MonthlyProductList = [];
        response.BlockedProductDBs = [];

        var battlePasses = db.BattlePasses.Where(x => x.AccountServerId == account.ServerId && x.PurchaseGroupId != 0).ToList();
        var battlePassProductList = new List<BattlePassProductPurchaseDB>();
        if (battlePasses.Any())
        {
            var productExcels = _excelTableService.GetTable<ProductExcelT>();
            var shopCashExcels = _excelTableService.GetTable<ShopCashExcelT>();
            foreach (var bp in battlePasses)
            {
                var product = productExcels.FirstOrDefault(x => {
                    var idx = x.ParcelType.IndexOf(ParcelType.ProductBattlePass);
                    return idx >= 0 && idx < x.ParcelId.Count && x.ParcelId[idx] == bp.BattlePassId;
                });
                var shopCash = product == null ? null : shopCashExcels.FirstOrDefault(x => x.CashProductId == product.Id);
                if (shopCash == null) continue;

                battlePassProductList.Add(new BattlePassProductPurchaseDB
                {
                    ProductId = shopCash.Id, // Billing response expects ShopCashId as ProductId
                    BattlePassId = bp.BattlePassId,
                    PurchaseBattlePassGroupId = bp.PurchaseGroupId
                });
            }
        }
        response.BattlePassProductList = battlePassProductList;

        return response;
    }

    // there is no billing backend to hand the order to, so the ShopCashId doubles as the order id and TransactionEnd grants straight off it
    [ProtocolHandler(Protocol.Billing_TransactionStartByYostar)]
    public async Task<BillingTransactionStartByYostarResponse> TransactionStartByYostar(
        SchaleDataContext db,
        BillingTransactionStartByYostarRequest request,
        BillingTransactionStartByYostarResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        response.PurchaseCount = 1;
        response.PurchaseResetDate = DateTime.MinValue;
        response.PurchaseOrderId = request.ShopCashId;
        response.PurchaseServerTag = PurchaseServerTag.Production;

        return response;
    }

    [ProtocolHandler(Protocol.Billing_TransactionEndByYostar)]
    public async Task<BillingTransactionEndByYostarResponse> TransactionEndByYostar(
        SchaleDataContext db,
        BillingTransactionEndByYostarRequest request,
        BillingTransactionEndByYostarResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        response.CountList = [];
        response.MonthlyProductList = [];
        response.BattlePassProductList = [];

        if (request.EndType != BillingTransactionEndType.Success)
            return response;

        var shopCash = _excelTableService.GetTable<ShopCashExcelT>().FirstOrDefault(x => x.Id == request.PurchaseOrderId);
        var product = shopCash == null ? null : _excelTableService.GetTable<ProductExcelT>().FirstOrDefault(x => x.Id == shopCash.CashProductId);
        if (product == null)
            return response;

        var parcelTypes = product.ParcelType ?? [];
        var parcelIds = product.ParcelId ?? [];
        var parcelAmounts = product.ParcelAmount ?? [];

        var parcels = new List<ParcelInfo>();
        for (int i = 0; i < parcelTypes.Count; i++)
        {
            parcels.Add(new ParcelInfo
            {
                Key = new ParcelKeyPair { Type = parcelTypes[i], Id = parcelIds[i] },
                Amount = parcelAmounts[i]
            });
        }

        var parcelResultDB = new ParcelResultDB();
        await _parcelHandler.BuildParcel(db, account, parcels, parcelResultDB);

        response.ParcelResult = parcelResultDB;
        response.PurchaseCount = 1;
        response.CountList = [new PurchaseCountDB
        {
            ShopCashId = shopCash.Id,
            PurchaseCount = 1,
            ResetDate = DateTime.MinValue,
            PurchaseDate = account.GameSettings.ServerDateTime(),
            ManualResetDate = null
        }];

        return response;
    }

    [ProtocolHandler(Protocol.Billing_PurchaseFreeProduct)]
    public async Task<BillingPurchaseFreeProductResponse> PurchaseFreeProduct(
        SchaleDataContext db,
        BillingPurchaseFreeProductRequest request,
        BillingPurchaseFreeProductResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

        var shopCashExcels = _excelTableService.GetTable<ShopCashExcelT>();
        var productExcels = _excelTableService.GetTable<ProductExcelT>();

        var shopCash = shopCashExcels.FirstOrDefault(x => x.Id == request.ShopCashId);
        if (shopCash == null)
        {
            response.PurchaseProduct = new();
            response.ParcelResult = new();
            return response;
        }

        var product = productExcels.FirstOrDefault(x => x.Id == shopCash.CashProductId);
        if (product == null)
        {
            response.PurchaseProduct = new();
            response.ParcelResult = new();
            return response;
        }

        // Free cash-shop products (the "Free Daily Pack") are limited per day/week/month; the count was never stored,
        // so every server restart offered the pack again. Kept in ShopPurchaseHistories under the negated ShopCashId.
        var now = account.GameSettings.ServerDateTime();
        var periodStart = ShopManager.PeriodStart(ToResetType(product.PurchasePeriodType), now);
        var history = db.ShopPurchaseHistories.FirstOrDefault(x => x.AccountServerId == account.ServerId && x.ShopUniqueId == -request.ShopCashId);
        if (history != null && history.PeriodStart != periodStart)
        {
            history.PeriodStart = periodStart;
            history.PurchaseCount = 0;
        }
        if (product.PurchasePeriodLimit > 0 && history != null && history.PurchaseCount >= product.PurchasePeriodLimit)
        {
            response.PurchaseProduct = ToCountDB(history, product, now);
            response.ParcelResult = new();
            return response;
        }
        if (history == null)
        {
            history = new ShopPurchaseHistoryDBServer { AccountServerId = account.ServerId, ShopUniqueId = -request.ShopCashId, PeriodStart = periodStart };
            db.ShopPurchaseHistories.Add(history);
        }
        history.PurchaseCount++;

        var parcelTypes = product.ParcelType ?? [];
        var parcelIds = product.ParcelId ?? [];
        var parcelAmounts = product.ParcelAmount ?? [];

        var parcels = new List<ParcelInfo>();
        for (int i = 0; i < parcelTypes.Count; i++)
        {
            parcels.Add(new ParcelInfo
            {
                Key = new ParcelKeyPair { Type = parcelTypes[i], Id = parcelIds[i] },
                Amount = parcelAmounts[i]
            });
        }

        var parcelResultDB = new ParcelResultDB();
        var parcelResult = await _parcelHandler.BuildParcel(db, account, parcels, parcelResultDB);

        response.ParcelResult = parcelResultDB;
        await db.SaveChangesAsync();
        response.PurchaseProduct = ToCountDB(history, product, now);

        return response;
    }

    private static PurchaseCountResetType ToResetType(PurchasePeriodType period) => period switch
    {
        PurchasePeriodType.Day => PurchaseCountResetType.Day,
        PurchasePeriodType.Week => PurchaseCountResetType.Week,
        PurchasePeriodType.Month => PurchaseCountResetType.Month,
        _ => PurchaseCountResetType.None,
    };

    // ResetDate is when the count clears (04:00 of the next game day / Monday / 1st); MinValue for never-resetting products.
    private static DateTime NextReset(PurchasePeriodType period, DateTime periodStart) => period switch
    {
        PurchasePeriodType.Day => periodStart.AddDays(1).AddHours(4),
        PurchasePeriodType.Week => periodStart.AddDays(7).AddHours(4),
        PurchasePeriodType.Month => periodStart.AddMonths(1).AddHours(4),
        _ => DateTime.MinValue,
    };

    private static PurchaseCountDB ToCountDB(ShopPurchaseHistoryDBServer history, ProductExcelT product, DateTime now) => new()
    {
        ShopCashId = -history.ShopUniqueId,
        PurchaseCount = (int)history.PurchaseCount,
        ResetDate = NextReset(product.PurchasePeriodType, history.PeriodStart),
        PurchaseDate = now,
        ManualResetDate = null
    };

    private List<PurchaseCountDB> CurrentCashPurchases(SchaleDataContext db, AccountDBServer account)
    {
        var now = account.GameSettings.ServerDateTime();
        var shopCash = _excelTableService.GetTable<ShopCashExcelT>().GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        var products = _excelTableService.GetTable<ProductExcelT>().GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        var result = new List<PurchaseCountDB>();
        foreach (var h in db.ShopPurchaseHistories.Where(x => x.AccountServerId == account.ServerId && x.ShopUniqueId < 0).ToList())
        {
            if (!shopCash.TryGetValue(-h.ShopUniqueId, out var cash) || !products.TryGetValue(cash.CashProductId, out var product))
                continue;
            if (h.PeriodStart != ShopManager.PeriodStart(ToResetType(product.PurchasePeriodType), now) || h.PurchaseCount <= 0)
                continue;
            result.Add(ToCountDB(h, product, now));
        }
        return result;
    }

    [ProtocolHandler(Protocol.Billing_CheckConditionCashShopGoods)]
    public async Task<BillingCheckConditionCashGoodsResponse> CheckConditionCashShopGoods(
        SchaleDataContext db,
        BillingCheckConditionCashGoodsRequest request,
        BillingCheckConditionCashGoodsResponse response)
    {
        // Always allow
        response.result = true;
        return response;
    }

    [ProtocolHandler(Protocol.Billing_PurchaseCashShopVerifyByNexon)]
    public async Task<BillingPurchaseCashShopVerifyByNexonResponse> PurchaseCashShopVerifyByNexon(
        SchaleDataContext db,
        BillingPurchaseCashShopVerifyByNexonRequest request,
        BillingPurchaseCashShopVerifyByNexonResponse response)
    {
        var account = await _sessionService.GetAuthenticatedUser(db, request.SessionKey);

            // Android's offline stamp restore can issue this protocol with ShopCashId=0 and no StampToken.
            // This is not a purchase. In particular, ParcelResult={} is interpreted by the client as an
            // incomplete delivery and produces "Your product is on the way" on every lobby entry.
        if (request.ShopCashId <= 0)
        {
                // Keep every purchase-result field absent. Empty objects/lists still look like a started
                // delivery to the Android billing flow, despite there being no ShopCashId to fulfill.
                response.ParcelResult = null;
            response.MailDB = null;
                response.CountList = null;
            response.PurchaseCount = 0;
                response.MonthlyProductList = null;
                response.ProductMonthlyIdInMailList = null;
                response.GachaTicketItemIdList = null;
                response.shopId = null;
            response.itemPrice = 0;
                response.currency = null;
                response.stampId = null;
                response.BattlePassIdInMailList = null;
                response.DailyRecordIdInMailList = null;
            return response;
        }
        
        var shopCashExcels = _excelTableService.GetTable<ShopCashExcelT>();
        var productExcels = _excelTableService.GetTable<ProductExcelT>();

        var shopCash = shopCashExcels.FirstOrDefault(x => x.Id == request.ShopCashId);
        if (shopCash == null)
            return response;

        var product = productExcels.FirstOrDefault(x => x.Id == shopCash.CashProductId);
        if (product == null)
            return response;

        var parcelTypes = product.ParcelType ?? [];
        var parcelIds = product.ParcelId ?? [];
        var parcelAmounts = product.ParcelAmount ?? [];

        var parcels = new List<ParcelInfo>();
        for (int i = 0; i < parcelTypes.Count; i++)
        {
            parcels.Add(new ParcelInfo
            {
                Key = new ParcelKeyPair { Type = parcelTypes[i], Id = parcelIds[i] },
                Amount = parcelAmounts[i]
            });
        }

        var parcelResultDB = new ParcelResultDB();
        var parcelResult = await _parcelHandler.BuildParcel(db, account, parcels, parcelResultDB);

        response.ParcelResult = parcelResultDB;
        response.PurchaseCount = 1;
        
        // Basic response fields to satisfy client
        response.shopId = request.ShopCashId.ToString();
        response.currency = request.CurrencyCode ?? "USD";
        response.itemPrice = request.CurrencyValue;

        // Update history skipped (no table)
        await db.SaveChangesAsync();

        return response;
    }

    // Nexon validate/finish run over the stamp web endpoints (the client's BillingValidateNexonResponse is that JSON), so this gateway pair is dead in the retail client.
    [ProtocolHandler(Protocol.Billing_ValidateByNexon)]
    public async Task<BillingValidateByNexonResponse> ValidateByNexon(
        SchaleDataContext db,
        BillingValidateByNexonRequest request,
        BillingValidateByNexonResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        return response;
    }

    [ProtocolHandler(Protocol.Billing_FinishByNexon)]
    public async Task<BillingFinishByNexonResponse> FinishByNexon(
        SchaleDataContext db,
        BillingFinishByNexonRequest request,
        BillingFinishByNexonResponse response)
    {
        await _sessionService.GetAuthenticatedUser(db, request.SessionKey);
        return response;
    }
}
