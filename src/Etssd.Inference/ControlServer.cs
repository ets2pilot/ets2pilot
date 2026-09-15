using System.Net.Http.Json;
using Etssd.Interface.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Etssd.Inference;

/// <summary>POST 到 control server /trajectory 的轨迹。</summary>
/// <param name="Freq">轨迹的频率，Hz。</param>
/// <param name="Telemetry">锚点帧的遥测块，轨迹的第一步是从这一帧到下一帧。</param>
/// <param name="Speed">[H]。</param>
/// <param name="YawRate">[H]。</param>
public sealed record TrajectoryRequest(double Freq, byte[] Telemetry, double[] Speed, double[] YawRate);

/// <summary>控制算法 server。注册 60Hz telemetry webhook，跟踪最新的轨迹，把控制量 POST 到 interface 的 /control。</summary>
public sealed class ControlServer(HttpClient http)
{
    public const string Url = "http://127.0.0.1:5322";

    private Plan? _plan;

    public async Task RunAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(Url);
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => Json.Configure(o.SerializerOptions));
        await using var app = builder.Build();

        app.MapPost("/trajectory", (TrajectoryRequest trajectory) =>
        {
            var anchor = VehicleState.From(trajectory.Telemetry);
            _plan = new Plan(anchor.TimeUs, trajectory.Freq,
                new Controller(anchor, trajectory.Speed, trajectory.YawRate, trajectory.Freq));
            return Results.NoContent();
        });

        app.MapPost("/notify", async (Notification notification) =>
        {
            if (notification is not { Event: "data", Telemetry: { } telemetry } || _plan is not { } plan)
            {
                return Results.NoContent();
            }
            var state = VehicleState.From(telemetry);
            var offset = ((long)state.TimeUs - (long)plan.AnchorUs) / 1e6 * plan.Freq;
            if (offset > plan.Controller.Horizon)
            {
                return Results.NoContent();
            }
            // 控制流的遥测可能早于轨迹的锚点帧，按锚点取
            var command = plan.Controller.At(Math.Max(offset, 0), state);
            using var response = await http.PostAsJsonAsync(
                $"{InterfaceServer.Url}/control", new ControlRequest(command.Accel, command.Steer), Json.Options);
            return Results.NoContent();
        });

        await app.StartAsync(ct);
        using var registered = await http.PostAsJsonAsync($"{InterfaceServer.Url}/webhook",
            new WebhookRequest("control", SubscriptionType.Telemetry, 60, $"{Url}/notify"), Json.Options, ct);
        registered.EnsureSuccessStatusCode();
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
        await app.StopAsync();
    }

    private sealed record Plan(ulong AnchorUs, double Freq, Controller Controller);
}
