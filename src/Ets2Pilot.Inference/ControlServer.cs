using System.Net.Http.Json;
using Ets2Pilot.Bridge.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ets2Pilot.Inference;

/// <summary>POST 到 control server /trajectory 的轨迹。</summary>
/// <param name="Freq">轨迹的频率，Hz。</param>
/// <param name="Telemetry">锚点帧的遥测块，轨迹的第一步是从这一帧到下一帧。</param>
/// <param name="Speed">[H]。</param>
/// <param name="YawRate">[H]。</param>
public sealed record TrajectoryRequest(double Freq, byte[] Telemetry, double[] Speed, double[] YawRate);

/// <summary>控制算法 server。每收到一条轨迹就以轨迹时长为租期注册 60Hz telemetry webhook，跟踪最新的轨迹，autopilot 开启时把控制量 POST 到 bridge 的 /control，steer 先经一阶低通。</summary>
public sealed class ControlServer(HttpClient http, AutopilotSwitch autopilot)
{
    public const string Url = "http://127.0.0.1:5322";

    private const double SteerTauS = 0.15;

    // 状态跨轨迹保留，轨迹切换产生的 steer 阶跃同样被平滑
    private readonly LowPass _steer = new(SteerTauS);
    private Plan? _plan;

    public async Task RunAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(Url);
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => Json.Configure(o.SerializerOptions));
        var app = builder.Build();

        app.MapPost("/trajectory", async (TrajectoryRequest trajectory) =>
        {
            var anchor = VehicleState.From(trajectory.Telemetry);
            _plan = new Plan(anchor.TimeUs, trajectory.Freq,
                new Controller(anchor, trajectory.Speed, trajectory.YawRate, trajectory.Freq));
            using var registered = await http.PostAsJsonAsync($"{BridgeServer.Url}/webhook",
                new WebhookRequest("control", SubscriptionType.Telemetry, 60, $"{Url}/notify", trajectory.Speed.Length / trajectory.Freq), Json.Options);
            return Results.NoContent();
        });

        app.MapPost("/notify", async (Notification notification) =>
        {
            // 自动驾驶关闭时不调用 Controller.At，积分项不累积人工驾驶期间的速度误差
            if (notification is not { Event: "data", Telemetry: { } telemetry } || _plan is not { } plan || !autopilot.IsEngaged)
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
            var steer = _steer.Next(state.TimeUs, command.Steer);
            using var response = await http.PostAsJsonAsync(
                $"{BridgeServer.Url}/control", new ControlRequest(command.Accel, steer), Json.Options);
            return Results.NoContent();
        });

        await app.RunAsync(ct);
    }

    private sealed record Plan(ulong AnchorUs, double Freq, Controller Controller);
}
