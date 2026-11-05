using Microsoft.AspNetCore.Mvc.Filters;

namespace Watcher.Api.Security;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequestBodySizeLimitAttribute(long maximumBytes) : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.HttpContext.Request.ContentLength > maximumBytes)
        {
            context.Result = new Microsoft.AspNetCore.Mvc.StatusCodeResult(
                StatusCodes.Status413PayloadTooLarge);
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
