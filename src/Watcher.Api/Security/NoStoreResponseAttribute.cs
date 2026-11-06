using Microsoft.AspNetCore.Mvc.Filters;

namespace Watcher.Api.Security;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class NoStoreResponseAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context) =>
        context.HttpContext.Response.Headers.CacheControl = "no-store";

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
