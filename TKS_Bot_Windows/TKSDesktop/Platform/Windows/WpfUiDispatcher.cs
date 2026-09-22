using System.Windows;
using System.Windows.Threading;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Platform.Windows;

/// <summary>
/// UI 线程调度（PRD §3.5 平台抽象层的第 10 个接口：<c>IUiDispatcher</c> → <c>Application.Current.Dispatcher</c>）。
///
/// ⚠️ 无图形会话（<c>--selftest</c>，NFR-W-15 / FR-W-TEST-1）下 <c>Application.Current</c> 可能为 <c>null</c>：
///    此时本类退化为「直接在当前线程执行」，使上层代码在不创建 <see cref="Application"/> 时也能跑通，
///    并如实通过 <see cref="IsOnUiThread"/> 报告（= 无 UI 线程，视当前线程为 UI 线程）。
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher? _dispatcher;

    /// <param name="dispatcher">
    /// 显式指定调度器；为 <c>null</c> 时取 <see cref="Application.Current"/> 的 <c>Dispatcher</c>。
    /// 单测可传入自建 <see cref="Dispatcher"/>。
    /// </param>
    public WpfUiDispatcher(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher;
    }

    /// <inheritdoc />
    /// <remarks>无 UI 线程（自检模式）时返回 <c>true</c>，使 <see cref="Invoke"/> 走直通路径。</remarks>
    public bool IsOnUiThread => _dispatcher is null || _dispatcher.CheckAccess();

    /// <summary>是否已绑定到真实的 UI 调度器（诊断用；<c>false</c> 表示自检/无图形会话）。</summary>
    public bool HasDispatcher => _dispatcher is not null;

    /// <inheritdoc />
    /// <remarks>
    /// 已在 UI 线程时**直接执行**（避免 <c>Dispatcher.Invoke</c> 在当前线程重入时死锁）。
    /// </remarks>
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    /// <inheritdoc />
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            // 已是 UI 线程（或无 UI 线程）：不需要排队，但仍返回 Task 以统一调用形态。
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        // Dispatcher.InvokeAsync 的异常会被捕获进返回的 Task（不会直接抛出），符合契约。
        return dispatcher.InvokeAsync(action).Task;
    }

    /// <inheritdoc />
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            try
            {
                return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        return dispatcher.InvokeAsync(func).Task;
    }
}
