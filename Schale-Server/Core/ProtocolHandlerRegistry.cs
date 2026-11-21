using Microsoft.Extensions.DependencyInjection;
using Schale_Server.Services;
using Microsoft.EntityFrameworkCore;
using Schale.Data;
using Schale.MX.NetworkProtocol;
using System.Reflection;

namespace Schale_Server.Core;

public class ProtocolHandlerRegistry : IProtocolHandlerRegistry
{
    private readonly IDbContextFactory<SchaleDataContext> _contextFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Dictionary<Protocol, MethodInfo> _handlerMethods = new();
    private readonly Dictionary<Protocol, Type> _requestTypes = new();
    private readonly Dictionary<Protocol, Type> _responseTypes = new();
    private readonly ILogger<ProtocolHandlerRegistry> _logger;

    public ProtocolHandlerRegistry(
        IDbContextFactory<SchaleDataContext> contextFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<ProtocolHandlerRegistry> logger)
    {
        _contextFactory = contextFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
        
        BuildProtocolTypeMappings();
    }

    private void BuildProtocolTypeMappings()
    {
        var protocolAssembly = typeof(RequestPacket).Assembly;

        foreach (var type in protocolAssembly.GetTypes())
        {
            if (type.IsClass && !type.IsAbstract)
            {
                if (type.IsAssignableTo(typeof(RequestPacket)) && type != typeof(RequestPacket))
                {
                    var instance = Activator.CreateInstance(type);
                    var protocolProp = type.GetProperty("Protocol");
                    if (protocolProp != null)
                    {
                        var protocol = (Protocol)protocolProp.GetValue(instance)!;
                        _requestTypes.TryAdd(protocol, type);
                    }
                }
                else if (type.IsAssignableTo(typeof(ResponsePacket)) && type != typeof(ResponsePacket))
                {
                    var instance = Activator.CreateInstance(type);
                    var protocolProp = type.GetProperty("Protocol");
                    if (protocolProp != null)
                    {
                        var protocol = (Protocol)protocolProp.GetValue(instance)!;
                        _responseTypes.TryAdd(protocol, type);
                    }
                }
            }
        }

        _logger.LogInformation("Mapped {RequestCount} request types and {ResponseCount} response types",
            _requestTypes.Count, _responseTypes.Count);
    }

    public void RegisterHandlerType(Type handlerType)
    {
        var classAttribute = handlerType.GetCustomAttribute<ProtocolHandlerAttribute>();
        var methods = handlerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (classAttribute != null && classAttribute.Protocol != Protocol.None)
        {
            var handleMethod = methods.FirstOrDefault(m => 
                m.Name == "HandleInternal" && 
                m.GetParameters().Length == 3);

            if (handleMethod != null)
            {
                if (!_handlerMethods.ContainsKey(classAttribute.Protocol))
                {
                    _handlerMethods[classAttribute.Protocol] = handleMethod;
                    _logger.LogDebug("Registered class handler {HandlerType} for protocol {Protocol}",
                        handlerType.Name, classAttribute.Protocol);
                }
            }
        }

        foreach (var method in methods)
        {
            var methodAttributes = method.GetCustomAttributes<ProtocolHandlerAttribute>();
            foreach (var attr in methodAttributes)
            {
                if (!_handlerMethods.ContainsKey(attr.Protocol))
                {
                    _handlerMethods[attr.Protocol] = method;
                    _logger.LogDebug("Registered method handler {HandlerType}.{MethodName} for protocol {Protocol}",
                        handlerType.Name, method.Name, attr.Protocol);
                }
                else
                {
                    _logger.LogWarning("Duplicate handler for protocol {Protocol} - ignoring {HandlerType}.{MethodName}",
                        attr.Protocol, handlerType.Name, method.Name);
                }
            }
        }
    }

    public MethodInfo? GetHandler(Protocol protocol)
    {
        _handlerMethods.TryGetValue(protocol, out var method);
        return method;
    }

    public Type? GetRequestType(Protocol protocol)
    {
        _requestTypes.TryGetValue(protocol, out var type);
        return type;
    }

    public Type? GetResponseType(Protocol protocol)
    {
        _responseTypes.TryGetValue(protocol, out var type);
        return type;
    }

    private static HashSet<long>? _missionScreenIds;

    // Ids the client's mission screen can resolve (MissionExcel + GuideMissionExcel); battle-pass and event rows share the table but must not ride along.
    private static HashSet<long> MissionScreenIds(BlueArchiveAPI.Services.ExcelTableService excel)
        => _missionScreenIds ??= excel.GetTable<Schale.FlatData.MissionExcelT>().Select(x => x.Id)
            .Concat(excel.GetTable<Schale.FlatData.GuideMissionExcelT>().Select(x => x.Id))
            .ToHashSet();

    public async Task<ResponsePacket?> InvokeHandler(Protocol protocol, RequestPacket request)
    {
        var method = GetHandler(protocol);
        if (method == null)
        {
            _logger.LogWarning("No handler found for protocol {Protocol}", protocol);
            return null;
        }

        var responseType = GetResponseType(protocol);
        if (responseType == null)
        {
            _logger.LogError("No response type found for protocol {Protocol}", protocol);
            return null;
        }

        await using var context = await _contextFactory.CreateDbContextAsync();
        var response = (ResponsePacket)Activator.CreateInstance(responseType)!;

        var handlerType = method.DeclaringType;
        if (handlerType == null)
        {
            _logger.LogError("Handler method has no declaring type for protocol {Protocol}", protocol);
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var instance = scope.ServiceProvider.GetService(handlerType);
        if (instance == null)
        {
            _logger.LogError("Could not resolve handler instance for {HandlerType}", handlerType.Name);
            return null;
        }

        try
        {
            var result = method.Invoke(instance, new object[] { context, request, response });

            var final = response;
            if (result is Task task)
            {
                await task;
                var resultProp = task.GetType().GetProperty("Result");
                if (resultProp?.GetValue(task) is ResponsePacket taskResult)
                    final = taskResult;
            }
            else if (result is ResponsePacket directResult)
            {
                final = directResult;
            }

            // Missions moved by this action ride on its response, so the client plays the "task complete" toast.
            var mapper = scope.ServiceProvider.GetService<AutoMapper.IMapper>();
            var excel = scope.ServiceProvider.GetService<BlueArchiveAPI.Services.ExcelTableService>();
            if (mapper != null && excel != null && protocol is not (Protocol.Mission_List or Protocol.Mission_Sync))
                MissionChangeLog.AttachTo(context, final, mapper, MissionScreenIds(excel));

            return final;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invoking handler for protocol {Protocol}", protocol);
            throw;
        }
    }
}
