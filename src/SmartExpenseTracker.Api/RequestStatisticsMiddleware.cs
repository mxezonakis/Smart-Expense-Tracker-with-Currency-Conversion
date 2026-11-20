using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Controllers;
using SmartExpenseTracker.Api.Controllers;

namespace SmartExpenseTracker.Api
{
    internal sealed class RequestStatisticsMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly RequestStatistics _statistics;

        public RequestStatisticsMiddleware(RequestDelegate next, RequestStatistics statistics)
        {
            _next = next;
            _statistics = statistics;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
            if (HttpMethods.IsGet(context.Request.Method) && action?.ControllerTypeInfo.AsType() == typeof(StatisticsController))
            {
                await _next(context);
                return;
            }

            long start = Stopwatch.GetTimestamp();
            await _next(context);
            if (!context.RequestAborted.IsCancellationRequested)
            {
                _statistics.Record(context.Response.StatusCode, Stopwatch.GetElapsedTime(start));
            }
        }
    }
}
