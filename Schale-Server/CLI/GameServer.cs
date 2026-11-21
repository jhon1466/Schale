using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Schale.Data;
using Schale.Commands;
using Schale.Services;
using Schale.Services.Client;
using Schale.Services.IrcClient;
using Schale.Services.WebClient;
using Schale.Extensions;
using Schale.Managers;
using Schale_Server.Core;
using Schale_Server.Services;
using Schale_Server.Managers;
using Schale_Server.GameClient;
using BlueArchiveAPI.Configuration;
using BlueArchiveAPI.Services;
using Schale.Utils;
using Serilog;
using Serilog.Events;
using AutoMapper;

namespace Schale.CLI
{
    public class GameServer
    {
        public static async Task Run(bool update, bool console, long? id)
        {
            // Prevent console freezes: disables QuickEdit mode on Windows and replaces Console.Out with a non-blocking async writer so that pipe buffer saturation can never block request threads.
            ConsoleHelper.Harden();

            var config = ConfigLogger.LogConfiguration();

            Console.WriteLine("===========================================");
            Console.WriteLine("    Schale Server - Blue Archive");
            Console.WriteLine("===========================================");

            Log.Information("Starting Game Server...");

            try
            {
                Config.Load();
                Schale_Server.Services.ServerNoticeService.Load();
                Schale_Server.Services.EventScheduleService.Load();
                Schale_Server.Services.WorldRaidService.Load();

                Schale_Server.Core.Diagnostics.GatewayWireLog.Configure(
                    Config.Instance.ServerConfiguration.PacketLogging.WireDump);
                if (Schale_Server.Core.Diagnostics.GatewayWireLog.Enabled)
                    Log.Information("Gateway wire dump is Enabled (logs/wire-{Date:yyyy-MM-dd}.txt)", DateTime.Now);

                using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog());
                var resolverLogger = loggerFactory.CreateLogger<BlueArchiveVersionResolver>();
                using var httpClient = new HttpClient();
                var resolver = new BlueArchiveVersionResolver(httpClient, resolverLogger);

                var (versionId, cdnBaseUrl) = await resolver.GetOrUpdateVersionIdAsync(
                    Config.Instance.ServerConfiguration.OverrideVersionId,
                    Config.Instance.ServerConfiguration.OverrideCdnBaseUrl
                );

                // Android's server_config URL includes the live client build. Keep its `desc` in sync too;
                // leaving the Steam-era default here advertises 1.90 to a 1.93 Android client and can stall
                // the in-game connection bootstrap before it reaches the lobby.
                if (Config.Instance.ServerConfiguration.IsAndroidClient
                    && Version.TryParse(BlueArchiveVersionResolver.GetCachedSourceBuildVersion(), out var androidBuild)
                    && Config.Instance.ServerConfiguration.GameVersion != androidBuild)
                {
                    Config.Instance.ServerConfiguration.GameVersion = androidBuild;
                    Config.Instance.ServerInfoConfig = Config.GetServerInfoConfig();
                    Config.Save();
                    Log.Information("Aligned Android server metadata to client build {GameVersion}", androidBuild);
                }

                BlueArchiveVersionState.Initialise(new BlueArchiveVersionState
                {
                    VersionId = versionId,
                    CdnBaseUrl = cdnBaseUrl
                });

                Console.WriteLine($"[Version Resolver] Initialized with VersionId: {versionId}");

                Console.WriteLine("\n[Command System] Loading commands...");
                CommandFactory.LoadCommands();
                Console.WriteLine("Console commands loaded");

                Console.WriteLine("\n[Resource Manager] Checking Excel tables...");
                await ResourceService.LoadResources(Config.Instance.ServerConfiguration.UseCustomExcel);

                // Fail fast with a clear message if the ExcelDB SQLCipher key cannot decrypt the ExcelDB.db for this client version (the key rotates between some game updates).
                ExcelTableService.ValidateExcelDbKey();

                var builder = WebApplication.CreateBuilder(Environment.GetCommandLineArgs());

                builder.Configuration.AddConfiguration(config);
                builder.Host.UseSerilog();

                builder.Services.AddDbProvider();
                builder.Services.AddControllers();
                builder.Services.AddResponseCompression(options => options.EnableForHttps = false);
                builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(options =>
                    options.Level = System.IO.Compression.CompressionLevel.Fastest);
                builder.Services.AddSingleton<Schale_Server.Services.LocalAccountStore>();
                builder.Services.AddMemoryCache();
                builder.Services.AddAutoMapper(cfg => {}, typeof(Schale.MappingProfiles.GameModelsMappingProfile).Assembly);

                builder.Services.AddProtocolHandlers();
                builder.Services.AddMemorySessionKeyService();
                builder.Services.AddExcelTableService();
                builder.Services.AddWebService();
                builder.Services.AddIrcService();
                builder.Services.AddHexaMapService();
                builder.Services.AddSharedDataCache();

                // These rewrite files of a local Steam install. An Android client is patched offline instead (see ANDROID.md).
                if (Config.Instance.ServerConfiguration.IsAndroidClient)
                {
                    Log.Information("Client platform is Android: Steam client patch services are disabled");
                }
                else
                {
                    builder.Services.AddHostedService<ClientMetadataPatchService>();
                    builder.Services.AddHostedService<ClientSteamOfflinePatchService>();
                    builder.Services.AddHostedService<ClientGrap64ManagementService>();
                    builder.Services.AddHostedService<ClientInfaceConfigPatchService>();
                    builder.Services.AddHostedService<ClientNativeIasPatchService>();
                    builder.Services.AddHostedService<ClientExcelBannerPatchService>();
                    builder.Services.AddHostedService<ClientExcelCharacterSyncService>();
                    builder.Services.AddHostedService<ClientCatalogPatchService>();
                    builder.Services.AddHostedService<ClientRegionLabelPatchService>();
                    builder.Services.AddHostedService<ClientStoreUrlPatchService>();
                }
                builder.Services.AddHostedService<WorldRaidSyncService>();
                builder.Services.AddGameClient();
                builder.Services.AddManagers();
                builder.Services.AddHandlers();

                builder.Services.AddSingleton<CafeService>();
                builder.Services.AddSingleton<HandlerManager>();
                builder.Services.AddSingleton<CustomCharacterService>();
                builder.Services.AddSingleton<ModCatalogService>();

                builder.Services.AddHostedService<Schale_Server.GameClient.GameClientService>();

                builder.Services.AddCors(options =>
                {
                    options.AddPolicy("SchaleGM", policy =>
                    {
                        policy
                            .WithOrigins("http://localhost:3000", "https://tauri.localhost")
                            .AllowAnyMethod()
                            .AllowAnyHeader();
                    });
                });

                builder.Services.AddEndpointsApiExplorer();
                builder.Services.AddSwaggerGen();

                builder.Configuration["Kestrel:Certificates:Default:Path"] = null;
                builder.Configuration["Kestrel:Certificates:Default:KeyPath"] = null;

                X509Certificate2? httpsCert = null;
                X509Certificate2Collection? httpsCertificateChain = null;
                X509Certificate2? accountPortalCert = null;
                X509Certificate2Collection? accountPortalCertificateChain = null;
                var certPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "certs", "selfsigned_cert.pem");
                var keyPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "certs", "selfsigned_key.pem");
                var accountPortalCertPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "certs", "account_portal_fullchain.pem");
                var accountPortalKeyPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "certs", "account_portal_key.pem");

                if (File.Exists(certPath) && File.Exists(keyPath))
                {
                    try
                    {
                        var certPem = File.ReadAllText(certPath);
                        var keyPem = File.ReadAllText(keyPath);
                        var cert = X509Certificate2.CreateFromPem(certPem, keyPem);
                        httpsCert = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
                        var pemCertificates = new X509Certificate2Collection();
                        pemCertificates.ImportFromPem(certPem);
                        httpsCertificateChain = new X509Certificate2Collection();
                        foreach (var chainCert in pemCertificates)
                        {
                            // ServerCertificate is sent separately; provide every issuing certificate here,
                            // including cross-signed intermediates so Android can build to a trusted root.
                            if (!string.Equals(chainCert.Thumbprint, httpsCert.Thumbprint, StringComparison.OrdinalIgnoreCase))
                                httpsCertificateChain.Add(chainCert);
                        }
                        Console.WriteLine($"Loaded certificate for HTTPS: {Path.GetFileName(certPath)}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to load certificate: {ex.Message}");
                    }
                }

                if (File.Exists(accountPortalCertPath) && File.Exists(accountPortalKeyPath))
                {
                    try
                    {
                        var portalCertPem = File.ReadAllText(accountPortalCertPath);
                        var portalKeyPem = File.ReadAllText(accountPortalKeyPath);
                        var portalLeaf = X509Certificate2.CreateFromPem(portalCertPem, portalKeyPem);
                        accountPortalCert = X509CertificateLoader.LoadPkcs12(portalLeaf.Export(X509ContentType.Pkcs12), null);
                        accountPortalCertificateChain = new X509Certificate2Collection();
                        var portalCertificates = new X509Certificate2Collection();
                        portalCertificates.ImportFromPem(portalCertPem);
                        foreach (var chainCert in portalCertificates)
                        {
                            if (!string.Equals(chainCert.Thumbprint, accountPortalCert.Thumbprint, StringComparison.OrdinalIgnoreCase))
                                accountPortalCertificateChain.Add(chainCert);
                        }
                        Console.WriteLine($"Loaded DNS certificate for in-game account portal: {Path.GetFileName(accountPortalCertPath)}");
                    }
                    catch (Exception ex)
                    {
                        accountPortalCert = null;
                        Console.WriteLine($"Failed to load account portal certificate: {ex.Message}");
                    }
                }

                // The game enters through HTTP on the API port and is redirected to the DNS portal host.
                // Prefer its publicly trusted DNS certificate on HTTPS; keep the IP certificate as fallback.
                if (accountPortalCert != null)
                {
                    httpsCert = accountPortalCert;
                    httpsCertificateChain = accountPortalCertificateChain;
                }

                var apiPort = ParsePort(Config.Instance.ServerConfiguration.HostPort, 5000, nameof(Config.Instance.ServerConfiguration.HostPort));
                var gatewayPort = ParsePort(Config.Instance.ServerConfiguration.GatewayPort, 5100, nameof(Config.Instance.ServerConfiguration.GatewayPort));
                var httpsPort = ParsePort(Config.Instance.ServerConfiguration.HttpsPort, 443, nameof(Config.Instance.ServerConfiguration.HttpsPort));

                builder.WebHost.ConfigureKestrel(options =>
                {
                    if (httpsCert != null)
                    {
                        var httpsAddress = httpsPort == 443 ? System.Net.IPAddress.Any : System.Net.IPAddress.Loopback;
                        options.Listen(httpsAddress, httpsPort, listenOptions =>
                        {
                            listenOptions.UseHttps(httpsOptions =>
                            {
                                httpsOptions.ServerCertificate = httpsCert;
                                httpsOptions.ServerCertificateChain = httpsCertificateChain;
                            });
                        });
                        Console.WriteLine($"HTTPS on port {httpsPort} for SDK endpoints");
                    }
                    else
                    {
                        Console.WriteLine("HTTPS on port 443 disabled (no certificate)");
                    }
                    
                    options.Listen(System.Net.IPAddress.Any, apiPort);
                    if (gatewayPort != apiPort)
                        options.Listen(System.Net.IPAddress.Any, gatewayPort);
                });
                Console.WriteLine($"HTTP on ports {apiPort} (API) & {gatewayPort} (Gateway)");

                var app = builder.Build();

                // LoginSync is the largest Android gateway response and the client retries it when the cellular
                // link takes too long. Negotiate HTTP gzip to reduce bytes on the wire without changing its JSON.
                app.UseResponseCompression();
                app.InitializeProtocolHandlers();

                using (var scope = app.Services.CreateScope())
                {
                    var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SchaleDataContext>>();
                    var excelService = scope.ServiceProvider.GetRequiredService<ExcelTableService>();
                    var mapper = scope.ServiceProvider.GetRequiredService<IMapper>();

                    using var context = await contextFactory.CreateDbContextAsync();
                    if (!context.Database.CanConnect())
                        context.Database.EnsureCreated();

                    if (context.Database.GetPendingMigrations().Any())
                        context.Database.Migrate();

                    // EnsureCreated only builds the schema for a brand-new database and does nothing to one that already exists, and the project has no migrations.
                    // SchemaReconciler derives both tables and columns from the EF model, so adding a property is enough - hand-written CREATE TABLE IF NOT EXISTS blocks would cover new tables but silently miss new columns.
                    var schemaChanges = await SchemaReconciler.ReconcileAsync(context);
                    foreach (var change in schemaChanges)
                        Console.WriteLine($"Schema: {change}");

                    var parcelHandler = scope.ServiceProvider.GetRequiredService<ParcelHandler>();
                    AccountInitializationService.Initialize(excelService, parcelHandler);

                    // A client update replaces ExcelDB.db and takes the forced dates with it, so re-apply whatever the operator left switched on. The cached world raid manifest rides the same pass.
                    if (Schale_Server.Services.EventScheduleService.Configured || Schale_Server.Services.WorldRaidService.Manifest != null)
                    {
                        try { Schale_Server.Services.EventScheduleService.Apply(excelService); }
                        catch (Exception ex) { Log.Warning(ex, "Could not re-apply the event schedule override"); }
                    }

                    if (console)
                    {
                        var consoleConnection = new ConsoleClientConnection(
                            contextFactory,
                            mapper,
                            excelService,
                            new StreamWriter(Console.OpenStandardOutput()),
                            id ?? 2
                        );
                        _ = Task.Run(() => ConsoleCommand.ConsoleCommandListener(consoleConnection));
                    }
                }

                if (app.Environment.IsDevelopment())
                {
                    app.UseSwagger();
                    app.UseSwaggerUI();
                }

                app.UseAuthorization();

                var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                if (Directory.Exists(wwwrootPath))
                {
                    var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(wwwrootPath);
                    var contentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
                    contentTypeProvider.Mappings[".woff2"] = "font/woff2";
                    contentTypeProvider.Mappings[".js"] = "application/javascript";
                    contentTypeProvider.Mappings[".mjs"] = "application/javascript";
                    contentTypeProvider.Mappings[".css"] = "text/css";
                    contentTypeProvider.Mappings[".png"] = "image/png";
                    contentTypeProvider.Mappings[".json"] = "application/json";

                    app.UseDefaultFiles(new DefaultFilesOptions
                    {
                        FileProvider = fileProvider,
                        RequestPath = ""
                    });

                    app.UseStaticFiles(new StaticFileOptions
                    {
                        FileProvider = fileProvider,
                        RequestPath = "",
                        ContentTypeProvider = contentTypeProvider
                    });
                }

                app.UseSerilogRequestLogging(options =>
                {
                    // The Control Center polls /health on a timer whenever it is open, and at Information that is most of the request log. Below the pipeline minimum it costs nothing and a failing probe still comes through as an error.
                    options.GetLevel = (context, elapsed, ex) =>
                        ex != null || context.Response.StatusCode > 499 ? LogEventLevel.Error :
                        context.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Debug :
                        LogEventLevel.Information;
                });
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.Value?.Contains("/ias", StringComparison.OrdinalIgnoreCase) == true)
                        Log.Information("[IAS Raw] {Method} {Scheme}://{Host}{Path}{QueryString}", context.Request.Method, context.Request.Scheme, context.Request.Host, context.Request.Path, context.Request.QueryString);

                    await next();
                });

                // Android port: AnimeGamesProxy rewrites the URL, so an unmatched request is logged with whatever still
                // identifies its original Nexon host (Host, X-Forwarded-*, Referer) plus its body, to implement it next.
                if (Config.Instance.ServerConfiguration.IsAndroidClient)
                {
                    app.Use(async (context, next) =>
                    {
                        context.Request.EnableBuffering();
                        await next();
                        if (context.Response.StatusCode != StatusCodes.Status404NotFound && context.Response.StatusCode != StatusCodes.Status405MethodNotAllowed)
                            return;
                        context.Request.Body.Position = 0;
                        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                        var body = await reader.ReadToEndAsync();
                        var headers = string.Join(" | ", context.Request.Headers.Select(h => $"{h.Key}: {h.Value}"));
                        Log.Warning("[Android 404] {Method} {Host}{Path}{Query} headers=[{Headers}] body={Body}",
                            context.Request.Method, context.Request.Host, context.Request.Path, context.Request.QueryString,
                            headers, body.Length > 2000 ? body[..2000] : body);
                    });
                }

                // AnimeGamesProxy's "server connection test" sends HEAD/GET to the bare address and reports anything but 200 as a failure.
                app.MapMethods("/", new[] { "GET", "HEAD" }, () => Results.Ok(new { status = "ok", service = "Schale Server" }));
                app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "Schale Server" }));
                app.MapControllers();
                app.UseCors("SchaleGM");

                app.Run();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "An unhandled exception occurred during runtime");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static int ParsePort(string value, int fallback, string configName)
        {
            if (int.TryParse(value, out var port) && port > 0 && port <= ushort.MaxValue)
                return port;

            Log.Warning("Invalid {ConfigName} value {PortValue}; using {FallbackPort}", configName, value, fallback);
            return fallback;
        }
    }
}
