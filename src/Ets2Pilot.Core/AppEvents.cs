using Microsoft.Extensions.Logging;

namespace Ets2Pilot.Core;

public static class AppEvents
{
    /// <summary>标记给用户看的日志。GUI 日志页只显示带这个编号的条目，其余只进文件。</summary>
    /// <remarks>编号取一个不与 ASP.NET 撞车的值，转发进来的 Kestrel 日志也带 EventId。</remarks>
    public static readonly EventId UserVisible = new(9001, nameof(UserVisible));
}
