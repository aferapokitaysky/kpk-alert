namespace Watcher.Api.Security;

public static class ContentSecurityPolicyExtensions
{
    public const string Policy =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'; " +
        "script-src 'self'; " +
        "script-src-attr 'none'; " +
        "style-src 'self'; " +
        "style-src-attr 'none'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "worker-src 'self'; " +
        "manifest-src 'self'; " +
        "frame-src 'none'";

    public static IApplicationBuilder UseContentSecurityPolicy(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                var response = (HttpResponse)state;
                response.Headers["Content-Security-Policy"] = Policy;
                return Task.CompletedTask;
            }, context.Response);

            await next();
        });

        return app;
    }
}
