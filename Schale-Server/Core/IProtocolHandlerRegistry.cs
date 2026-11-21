using Schale.MX.NetworkProtocol;
using System.Reflection;

namespace Schale_Server.Core;

public interface IProtocolHandlerRegistry
{
    void RegisterHandlerType(Type handlerType);
    MethodInfo? GetHandler(Protocol protocol);
    Type? GetRequestType(Protocol protocol);
    Type? GetResponseType(Protocol protocol);
    Task<ResponsePacket?> InvokeHandler(Protocol protocol, RequestPacket request);
}
