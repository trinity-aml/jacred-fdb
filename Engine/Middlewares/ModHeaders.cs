using Microsoft.AspNetCore.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace JacRed.Engine.Middlewares
{
    public class ModHeaders
    {
        private readonly RequestDelegate _next;
        public ModHeaders(RequestDelegate next)
        {
            _next = next;
        }

        public Task Invoke(HttpContext httpContext)
        {
            httpContext.Response.Headers.Add("Access-Control-Allow-Credentials", "true");
            httpContext.Response.Headers.Add("Access-Control-Allow-Private-Network", "true");
            httpContext.Response.Headers.Add("Access-Control-Allow-Headers", "Accept, Origin, Content-Type");
            httpContext.Response.Headers.Add("Access-Control-Allow-Methods", "POST, GET, OPTIONS");

            if (httpContext.Request.Headers.TryGetValue("origin", out var origin))
                httpContext.Response.Headers.Add("Access-Control-Allow-Origin", origin.ToString());
            else if (httpContext.Request.Headers.TryGetValue("referer", out var referer))
                httpContext.Response.Headers.Add("Access-Control-Allow-Origin", referer.ToString());
            else
                httpContext.Response.Headers.Add("Access-Control-Allow-Origin", "*");

            if (httpContext.Connection.RemoteIpAddress.ToString() != "127.0.0.1")
            {
                if (httpContext.Request.Path.Value.StartsWith("/cron/") || httpContext.Request.Path.Value.StartsWith("/jsondb") || httpContext.Request.Path.Value.StartsWith("/dev/"))
                    return Task.CompletedTask;

                if (!string.IsNullOrEmpty(AppInit.conf.apikey))
                {
                    if (httpContext.Request.Path.Value == "/" || Regex.IsMatch(httpContext.Request.Path.Value, "^/(api/v1\\.0/conf|stats/|sync/)"))
                        return _next(httpContext);

                    if (AppInit.conf.apikey != Regex.Match(httpContext.Request.QueryString.Value, "(\\?|&)apikey=([^&]+)").Groups[2].Value)
                        return Task.CompletedTask;
                }
            }

            return _next(httpContext);
        }
    }
}
