using Microsoft.AspNetCore.Mvc.Controllers;

namespace Watcher.Api.Security;

public sealed class NoStoreResponseMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        Endpoint? endpoint = context.GetEndpoint();
        ControllerActionDescriptor? action = endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>();
        var isNoStore = endpoint?.Metadata.GetMetadata<NoStoreResponseAttribute>() is not null ||
                         action?.FilterDescriptors.Any(filter => filter.Filter is NoStoreResponseAttribute) == true;
        if (isNoStore)
        {
            context.Response.Headers.CacheControl = "no-store";
        }

        return next(context);
    }
}
