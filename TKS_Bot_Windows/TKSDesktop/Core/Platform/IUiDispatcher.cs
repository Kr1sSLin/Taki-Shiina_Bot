namespace TKSDesktop.Core.Platform;

/// <summary>
/// UI 线程调度（PRD §3.5 / FR-W-ARCH-3）。
///
/// ⚠️ 契约冻结：`Core` 层与 ViewModel **只能**经此接口触达 UI 线程，
///    **不得**直接引用 `System.Windows.Threading.Dispatcher`（NFR-W-14 / V-W-S1）。
/// ⚠️ 所有 `ObservableCollection` 变更**必须**经此调度到 UI 线程（FR-W-ARCH-3）。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>当前是否已在 UI 线程。</summary>
    bool IsOnUiThread { get; }

    /// <summary>同步投递到 UI 线程（已在 UI 线程时直接执行）。</summary>
    void Invoke(Action action);

    /// <summary>异步投递到 UI 线程（已在 UI 线程时直接执行）。</summary>
    Task InvokeAsync(Action action);

    /// <summary>异步投递并取回结果。</summary>
    Task<T> InvokeAsync<T>(Func<T> func);
}
