using Etssd.Core;
using Microsoft.Extensions.Logging;

namespace Etssd.Inference;

/// <summary>自动驾驶激活状态机。对外的状态只有 <see cref="IsEngaged"/>，转移时播放提示音。</summary>
/// <remarks>
/// 转移函数线程安全，返回状态是否变化，未变化时不发声。
/// IsEngaged 与轨迹是否存在无关，轨迹中断结束后控制自动恢复。
/// </remarks>
public sealed class AutopilotSwitch(ILogger log)
{
    // Hz, ms
    private static readonly (int, int)[] EngageTones = [(880, 120), (1320, 180)];
    private static readonly (int, int)[] DisengageTones = [(1320, 120), (880, 180)];
    private static readonly (int, int)[] LockTones = [(440, 150)];

    private static readonly System.Threading.Lock SoundLock = new();

    private readonly System.Threading.Lock _gate = new();
    private volatile State _state = State.Manual;

    private enum State
    {
        Manual,
        Engaged,
        Locked,
    }

    /// <summary>IsEngaged 变化后在调用转移函数的线程上触发。</summary>
    public event Action? Changed;

    public bool IsEngaged => _state == State.Engaged;

    /// <summary>锁定手动驾驶，自动驾驶开启时一并关闭。锁定期间 <see cref="Engage"/> 无效。</summary>
    public bool Lock()
    {
        lock (_gate)
        {
            return Move(State.Engaged, State.Locked, DisengageTones, "自动驾驶已关闭，手动驾驶已锁定")
                || Move(State.Manual, State.Locked, LockTones, "手动驾驶已锁定");
        }
    }

    public bool Unlock() => Move(State.Locked, State.Manual, [], "手动驾驶已解锁");

    public bool Engage() => Move(State.Manual, State.Engaged, EngageTones, "自动驾驶已开启");

    public bool Disengage() => Move(State.Engaged, State.Manual, DisengageTones, "自动驾驶已关闭");

    private bool Move(State from, State to, (int Hz, int Ms)[] tones, string message)
    {
        lock (_gate)
        {
            if (_state != from)
            {
                return false;
            }
            _state = to;
            log.LogInformation(AppEvents.UserVisible, "{Message}", message);
            if (tones.Length > 0)
            {
                Play(tones);
            }
            if (from == State.Engaged || to == State.Engaged)
            {
                Changed?.Invoke();
            }
            return true;
        }
    }

    /// <summary>Console.Beep 阻塞到播完，放到线程池上播放，锁保证两次转移的提示音不重叠。</summary>
    private static void Play((int Hz, int Ms)[] tones) => Task.Run(() =>
    {
        lock (SoundLock)
        {
            foreach (var (hz, ms) in tones)
            {
                Console.Beep(hz, ms);
            }
        }
    });
}
