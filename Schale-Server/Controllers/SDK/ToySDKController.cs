using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Text;
using System.Text.Json;
using BlueArchiveAPI.Models;
using BlueArchiveAPI;
using Schale.Crypto;

namespace Schale_Server.Controllers.SDK
{
    [ApiController]
    [Route("/toy/sdk/")]
    // The Android SDK posts the same toy calls without the /toy prefix (public.api.nexon.com/sdk/<name>.nx).
    [Route("/sdk/")]
    public class ToySDKController : ControllerBase
    {
        [HttpPost("getCountry.nx")]
        public IResult GetCountry()
        {
            GuestDeviceIdentity.RememberClientDevice(Request);
            // Android (/sdk/getCountry.nx): a 404 is what let the first cold start reach server_config; keep it until the
            // official Android body is known.
            if (Request.Path.StartsWithSegments("/sdk"))
                return Results.NotFound();
            var res = new
            {
                errorCode = -2,
                result = new { },
                errorText = "Missing request parameters or incorrect format.",
                errorDetail = ""
            };
            Response.Headers["errorcode"] = "-2";
            Response.Headers["access-control-allow-origin"] = "*";
            var encryptedBytes = Utils.PreGatewayAesEncrypt(JsonSerializer.Serialize(res));
            return Results.Bytes(encryptedBytes, contentType: "text/html; charset=UTF-8");
        }

        // Android toy SDK: public.api.nexon.com/sdk/ip.nx, polled until it answers (hundreds of 404s in a row otherwise).
        // The official body was not captured; this is the generic toy envelope with the caller's address.
        [HttpGet("ip.nx")]
        [HttpPost("ip.nx")]
        public IResult GetIp()
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "127.0.0.1";
            return Results.Json(new { errorCode = 0, errorText = "Success", errorDetail = "", result = new { ip } });
        }

        // Android analytics event keys; a 404 surfaces as "initializeEventKeys getEventInfo failed : errorCode is 1004".
        // Encrypted like getCountry/getPromotion: a plain body makes the SDK log "cipherWithAES128 ... BAD_DECRYPT".
        [HttpPost("getEventKeys")]
        [HttpPost("getEventKeys.nx")]
        public IResult GetEventKeys()
        {
            var res = new { errorCode = 0, errorText = "Success", errorDetail = "", result = new { eventKeys = Array.Empty<object>() } };
            var encryptedBytes = Utils.PreGatewayAesEncrypt(JsonSerializer.Serialize(res));
            return Results.Bytes(encryptedBytes, contentType: "text/html; charset=UTF-8");
        }

        [HttpPost("getPromotion.nx")]
        public IResult GetPromotion()
        {
            GuestDeviceIdentity.RememberClientDevice(Request);
            var res = new
            {
                errorCode = 0,
                errorDetail = "",
                errorText = "Success",
                result = new
                {
                    bannerList = new List<object>(),
                    portraitBannerList = new List<object>()
                }
            };
            var encryptedBytes = Utils.PreGatewayAesEncrypt(JsonSerializer.Serialize(res));
            return Results.Bytes(encryptedBytes, contentType: "text/html");
        }

        [HttpPost("enterToy.nx")]
        public IResult EnterToy()
        {
            GuestDeviceIdentity.RememberClientDevice(Request);
            var localHost = Request.Host.HasValue ? Request.Host.Value : "127.0.0.1:5000";
            var gtableUrl = $"http://{localHost}/gid/2079.json";

            var res = new
            {
                errorCode = 0,
                errorText = "Success",
                errorDetail = "",
                result = new
                {
                    gtable_url = gtableUrl,
                    platform_sdk = "WINDOWS",
                    service = new
                    {
                        title = "Blue Archive",
                        buildVer = "2",
                        policyApiVer = "2",
                        termsApiVer = "2",
                        useTPA = 0,
                        useGbNpsn = 1,
                        useGbKrpc = 1,
                        useGbArena = 1,
                        useGbJppc = 0,
                        useGamania = 0,
                        useToyBanDialog = 0,
                        grbRating = "",
                        networkCheckSampleRate = "3",
                        nkMemberAccessCode = "0",
                        useIdfaCollection = 0,
                        useIdfaDialog = 0,
                        useIdfaDialogNTest = 0,
                        useNexonOTP = 0,
                        useRegionLock = 0,
                        usePcDirectRun = 0,
                        useArenaCSByRegion = 0,
                        usePlayNow = 0,
                        methinksUsage = new
                        {
                            useAlwaysOnRecording = 0,
                            useScreenshot = 0,
                            useStreaming = 0,
                            useSurvey = 0
                        },
                        livestreamUsage = new { useIM = 0 },
                        useExactAlarmActivation = 0,
                        useCollectUserActivity = 0,
                        userActivityDataPushNotification = new
                        {
                            changePoints = new List<object>(),
                            notificationType = ""
                        },
                        appAppAuthLoginIconUrl = "",
                        useGuidCreationBlk = 0,
                        guidCreationBlkWlCo = new List<object>(),
                        useArena2FA = 0,
                        usePrimary = 1,
                        loginUIType = "1",
                        clientId = "MjcwOA",
                        useMemberships = new List<int> { 101, 103, 110, 107, 9999 },
                        useMembershipsInfo = new
                        {
                            nexonNetSecretKey = "",
                            nexonNetProductId = "",
                            nexonNetRedirectUri = ""
                        }
                    },
                    endBanner = new Dictionary<string, object>(),
                    // NXPToyTermsManager.UpdateTerms throws on a null list, so the key has to be here even empty or the enter-toy callback never completes.
                    termsAgree = Array.Empty<object>(),
                    country = "PH",
                    idfa = new
                    {
                        dialog = new List<object>(),
                        imgUrl = "",
                        language = ""
                    },
                    useLocalPolicy = new List<string> { "0", "0" },
                    enableLogging = false,
                    enablePlexLogging = false,
                    enableForcePingLogging = false,
                    userArenaRegion = 5,
                    offerwall = new { id = 0, title = "" },
                    useYoutubeRewardEvent = false,
                    gpgCycle = 0,
                    eve = new
                    {
                        domain = "https://eve.nexon.com",
                        gApi = "https://g-eve-apis.nexon.com"
                    },
                    insign = new
                    {
                        useSimpleSignup = 0,
                        useKrpcSimpleSignup = 0,
                        useArenaSimpleSignup = 0
                    }
                }
            };
            return Results.Json(res, contentType: "text/html");
        }

        // Android "Play with Guest Account": public.api.nexon.com/sdk/signIn.nx with a PreGatewayAes body. A 404 here is
        // "Request failed.(10001)". Answers with the same session as signInWithTicket (same guid, which the later NPSN
        // crypto and IasController expect), encrypted like the other pre-login toy calls.
        [HttpPost("signIn.nx")]
        [DisableFormValueModelBinding]
        public async Task<IResult> SignIn([FromServices] ILogger<ToySDKController> logger,
            [FromServices] Schale_Server.Services.LocalAccountStore accounts)
        {
            using var buffer = new MemoryStream();
            await Request.Body.CopyToAsync(buffer);
            string request;
            try { request = NXCrypto.PreGatewayAesDecrypt(buffer.ToArray()); }
            catch (Exception e) { request = $"<{buffer.Length} bytes, not PreGatewayAes: {e.Message}>"; }
            // This payload may contain a user's email and password. Never put its contents in server logs.
            var payloadFields = TryGetTopLevelJsonFields(request);
            logger.LogInformation("[Toy signIn] decryptedPayloadLength={Length} topLevelFields={Fields}",
                Encoding.UTF8.GetByteCount(request), payloadFields);

            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(request);
            }
            catch (JsonException)
            {
                return EncryptedSignInError("Invalid login request.");
            }

            var reqMemType = 9999;
            using (document)
            {
                var root = document.RootElement;
                if (TryGetJsonValue(root, "memType", out var memberType)
                    && memberType.ValueKind == JsonValueKind.Number && memberType.TryGetInt32(out var value))
                {
                    reqMemType = value;
                }

                var isGuest = reqMemType == 9999;
                if (!isGuest)
                {
                    var username = FindCredential(root, "email", "emailAddress", "mail", "username", "userName", "userId", "userID", "loginId", "login_id", "accountId", "nexonId");
                    var password = FindCredential(root, "password", "passwd", "pwd", "userPassword", "userPw", "user_password", "loginPassword", "loginPw", "nexonPassword", "pw");
                    logger.LogInformation("[Toy signIn] found username='{User}', passwordLen={PwLen}", username, password?.Length ?? -1);
                    var account = username == null || password == null ? null : accounts.Authenticate(username.Trim(), password, msg => logger.LogInformation("[Toy signIn auth] {Msg}", msg));
                    if (account == null)
                        return EncryptedSignInError("Invalid username or password.");

                    string? payloadSeed = null;
                    if (TryGetJsonValue(root, "uuid", out var uuidEl) && uuidEl.ValueKind == JsonValueKind.String)
                        payloadSeed = uuidEl.GetString();
                    else if (TryGetJsonValue(root, "uuid2", out var uuid2El) && uuid2El.ValueKind == JsonValueKind.String)
                        payloadSeed = uuid2El.GetString();

                    if (!string.IsNullOrWhiteSpace(payloadSeed))
                        GuestDeviceIdentity.RememberClientSeed(Request, payloadSeed);

                    var deviceKey = GuestDeviceIdentity.RecentDeviceKey(Request)
                        ?? (!string.IsNullOrWhiteSpace(payloadSeed) ? GuestDeviceIdentity.DeviceKey(payloadSeed) : null);

                    if (deviceKey != null)
                    {
                        accounts.LinkDevice(account.Username, deviceKey, account.PublisherId);
                        GuestDeviceIdentity.BindDeviceAccount(deviceKey, account.PublisherId);
                    }
                }
            }

            var localSessionUserId = GuestDeviceIdentity.LocalSessionUserId(Request);
            var accountGuid = GuestDeviceIdentity.AccountGuid(Request);

            var res = new
            {
                errorCode = 0,
                errorDetail = "",
                errorText = "Success",
                result = new
                {
                    npSN = localSessionUserId,
                    guid = accountGuid,
                    umKey = "109:1120300221",
                    npaCode = "0E032VW034F",
                    memType = reqMemType,
                    npToken = $"schale-nptoken:{Guid.NewGuid():N}",
                    sessionToken = $"schale-session:{Guid.NewGuid():N}",
                    isNewUser = 0,
                    loginResultType = 1,
                    withdrawExpiresIn = 0,
                    isSwap = false,
                    termsAgree = Array.Empty<object>(),
                    terms = Array.Empty<object>()
                }
            };
            var encryptedBytes = Utils.PreGatewayAesEncrypt(JsonSerializer.Serialize(res));
            return Results.Bytes(encryptedBytes, contentType: "text/html; charset=UTF-8");
        }

        private IResult EncryptedSignInError(string message)
        {
            var response = new { errorCode = -1, errorDetail = "", errorText = message, result = new { } };
            var encryptedBytes = Utils.PreGatewayAesEncrypt(JsonSerializer.Serialize(response));
            return Results.Bytes(encryptedBytes, contentType: "text/html; charset=UTF-8");
        }

        private static string? FindCredential(JsonElement element, params string[] names)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                        return property.Value.GetString();
                }

                foreach (var property in element.EnumerateObject())
                {
                    var nested = FindCredential(property.Value, names);
                    if (nested != null) return nested;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindCredential(item, names);
                    if (nested != null) return nested;
                }
            }

            return null;
        }

        private static bool TryGetJsonValue(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static string TryGetTopLevelJsonFields(string payload)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? string.Join(",", document.RootElement.EnumerateObject().Select(property => property.Name))
                    : document.RootElement.ValueKind.ToString();
            }
            catch (JsonException)
            {
                return "non-json";
            }
        }

        [HttpPost("signInWithTicket.nx")]
        public IResult SignInWithTicket()
        {
            // Field set derived from NPA.InfaceSDK.NXPToySignInWithTicketResponse.FillJsonBody (0x18943DDD0) - the SDK's own serializer, i.e. the authoritative shape of this response. The deserializer matches JSON keys to these field names.
            // The post-sign-in managed path (NXPAccountLinkBase._LoginWithTicket_b__0 @0x1893D6850) reads guid, umKey, sessionToken and termsAgree, builds the NXPUpdatedUser (SetSessionToken) and calls AgreeTermsWithTicket -> GetGameToken.
            // Omitting sessionToken/npToken leaves the user half-populated and the post-sign-in chain stalls before issuing the game-token request.
            var localSessionUserId = GuestDeviceIdentity.LocalSessionUserId(Request);
            var accountGuid = GuestDeviceIdentity.AccountGuid(Request);
            var res = new
            {
                errorCode = 0,
                errorDetail = "",
                errorText = "Success",
                result = new
                {
                    npSN = localSessionUserId,
                    guid = accountGuid,
                    umKey = "109:1120300221",
                    npaCode = "0E032VW034F",
                    npToken = $"schale-nptoken:{Guid.NewGuid():N}",
                    sessionToken = $"schale-session:{Guid.NewGuid():N}",
                    isNewUser = 0,
                    loginResultType = 1,
                    withdrawExpiresIn = 0,
                    isSwap = false,
                    // termsAgree empty -> NXPToyTermsManager.IsAgree (0x1893F98D0) short-circuits to "agreed" (empty/null list), so AgreeTermsWithTicket proceeds straight to GetGameToken instead of popping the offline-incompatible NXPTermsDialog.
                    termsAgree = Array.Empty<object>(),
                    terms = Array.Empty<object>()
                }
            };
            return Results.Json(res);
        }

        [HttpPost("terms.nx")]
        public IResult Terms()
        {
            var res = new
            {
                errorCode = 0,
                errorDetail = "",
                errorText = "Success",
                result = new
                {
                    // Empty terms so the merged terms list the SDK evaluates (NXPToyTermsManager.IsAgree) is empty -> treated as agreed -> no NXPTermsDialog. See signInWithTicket.
                    terms = Array.Empty<object>()
                }
            };
            return Results.Json(res);
        }

        // The AES "npsn" the toy SDK uses for NPSN-crypt Bolt traffic is NOT the npSN field - NXPAuthRequestCredential..ctor(NXPToySession) (GameAssembly 0x18937EC20) sets
        //   _Npsn = long.Parse(session.guid)
        // i.e. it is the account GUID parsed as a long. session.guid comes from the guid we return in signInWithTicket, so this MUST equal that guid (= IasController DefaultSteamGuid "20790000041274554"). Using the npSN here derives the wrong AES key and the SDK decrypts getPolicyList into garbage -> JSON parse 10001.
        // getPolicyList/getUserInfo/getTermsList/logoutSVC are managed toy Bolt requests (NXPToyBoltRequestManager over BestHTTP, so visible via mitm). NXPToyNetworkUtil.MakeSuccessResult (GameAssembly 0x1893839A0) decrypts the body before parsing it:
        //   text = NXPCrypto.Decrypt(req.DecryptType, BytesToHexString(response.RawBytes), Npsn); JSON.Parse(text) - on failure errorCode 10001, gs_error_network_response_json_parsing
        // NXPToyGetPolicyListRequest sets EncryptType/DecryptType = Npsn (0x200000002), so the cipher is AES-128-ECB under NXCrypto.GenerateNpsnAes128Key - not the shared PreGatewayAes key that the pre-login getCountry/getPromotion calls use.
        // Plain JSON or shared-key ciphertext decrypts to garbage, JSON.Parse throws, and "Request failed (10001)" at TAP TO START blocks the game-server handoff.
        // Emit raw NPSN-encrypted bytes; the SDK hex-encodes them itself.
        [HttpPost("getUserInfo.nx")]
        public IResult GetUserInfo()
        {
            var localSessionUserId = GuestDeviceIdentity.LocalSessionUserId(Request);
            var accountGuid = GuestDeviceIdentity.AccountGuid(Request);
            long.TryParse(accountGuid, System.Globalization.CultureInfo.InvariantCulture, out var npsn);
            var res = new
            {
                errorCode = 0,
                errorText = "Success",
                errorDetail = "",
                result = new
                {
                    npsn = npsn,
                    npsnString = accountGuid,
                    guid = accountGuid,
                    npToken = $"schale-nptoken:{Guid.NewGuid():N}",
                    npaCode = "0E032VW034F",
                    email = "schale@sensei.local",
                    pushAgree = 1,
                    code = 0,
                    npsnUserInfo = new
                    {
                        email = "schale@sensei.local",
                        isBlock = false
                    },
                    njUserInfo = new
                    {
                        email = "schale@sensei.local"
                    },
                    nkUserInfo = new
                    {
                        email = "schale@sensei.local"
                    },
                    platformUserInfo = new
                    {
                        email = "schale@sensei.local"
                    },
                    policyList = Array.Empty<object>(),
                    policy = Array.Empty<object>(),
                    terms = Array.Empty<object>(),
                    list = Array.Empty<object>()
                }
            };
            var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(res));
            var encrypted = new NXCrypto().PostGatewayAesEncrypt(plain, NXCrypto.NXToyCryptoType.NPSN, GuestDeviceIdentity.AccountNpsn(Request))
                            ?? plain;
            return Results.Bytes(encrypted, contentType: "text/html; charset=UTF-8");
        }

        [HttpPost("getPolicyList.nx")]
        [HttpPost("getTermsList.nx")]
        [HttpPost("logoutSVC.nx")]
        public IResult Any()
        {
            var res = new
            {
                errorCode = 0,
                errorText = "Success",
                errorDetail = "",
                result = new
                {
                    code = 0,
                    policyList = Array.Empty<object>(),
                    policy = Array.Empty<object>(),
                    terms = Array.Empty<object>(),
                    list = Array.Empty<object>()
                }
            };
            var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(res));
            var encrypted = new NXCrypto().PostGatewayAesEncrypt(plain, NXCrypto.NXToyCryptoType.NPSN, GuestDeviceIdentity.AccountNpsn(Request))
                            ?? plain;
            return Results.Bytes(encrypted, contentType: "text/html; charset=UTF-8");
        }
    }

    /// <summary>
    /// The Android Nexon SDK sends a raw encrypted byte payload while declaring a form
    /// content type. MVC's default form value providers try to parse those ciphertext
    /// bytes as JQuery keys before the action runs, causing a 500 before we can decrypt it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            var factories = context.ValueProviderFactories;
            factories.RemoveType<FormValueProviderFactory>();
            factories.RemoveType<JQueryFormValueProviderFactory>();
            factories.RemoveType<FormFileValueProviderFactory>();
        }

        public void OnResourceExecuted(ResourceExecutedContext context) { }
    }
}
