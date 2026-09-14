using Etssd.Interface.Frames;
using Etssd.Interface.VJoy;
using Etssd.Interface.Webhook;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Etssd.Interface.Http;

/// <summary>POST/DELETE /webhook 与 POST /control。固定端口同时保证本机只有一个 interface 实例。</summary>
public static class InterfaceServer
{
    public const string Url = "http://127.0.0.1:5320";

    private static readonly WebhookResponse Layout = new(
        FrameRing.ProtocolVersion, FrameRing.MappingName, FrameRing.SlotCount, FrameRing.Width, FrameRing.Height);

    public static WebApplication Build(SubscriberRegistry registry, VJoyController? vjoy, ILoggerFactory loggers)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(Url);
        builder.Logging.ClearProviders();
        // 请求级日志为 Information，/control 按游戏帧率调用
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddProvider(new ForwardingLoggerProvider(loggers));
        builder.Services.ConfigureHttpJsonOptions(o => Json.Configure(o.SerializerOptions));
        var app = builder.Build();

        app.MapPost("/webhook", (WebhookRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || !double.IsFinite(request.Freq) || request.Freq <= 0)
            {
                return Results.BadRequest("name 不能为空，freq 须为正数");
            }
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                return Results.BadRequest("url 须为 http 绝对地址");
            }
            return registry.Add(request) ? Results.Ok(Layout) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        });

        app.MapDelete("/webhook", ([FromQuery] string name) =>
            registry.Remove(name) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/control", (ControlRequest request) =>
        {
            if (vjoy is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            if (!double.IsFinite(request.Throttle) || !double.IsFinite(request.Steer))
            {
                return Results.BadRequest("throttle 与 steer 须为有限数");
            }
            vjoy.Send(request.Throttle, request.Steer);
            return Results.NoContent();
        });

        return app;
    }

    private sealed class ForwardingLoggerProvider(ILoggerFactory loggers) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => loggers.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }
}
