// Payload.cs

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace PclPatch
{
    public static class Entry
    {
        // ===== 可调参数 =====
        const string PageTypeName = "PageDownloadInstall";
        const string LoadingName = "LoadMinecraft";
        const string LoaderTypeName = "ModDownload";
        const string LoaderFieldName = "DlClientListLoader";

        const int PollStartupMs = 1200;
        const int PollFallbackMs = 30000;
        const int ReCheckDelayMs = 400;
        const int StartWaitTimeoutMs = 90000;
        const int MaxRetry = 3;

        static readonly bool CallShowList = false;

        static readonly bool EnableVirtualization = true;   // 开 UI 虚拟化
        static readonly bool ForceVirtualizingPanel = true; // 普通 StackPanel -> VirtualizingStackPanel
        static readonly bool TryDisableAnimation = true;    // 尝试关闭 PCL 动画开关

        const int FindNodeBudget = 8000;    // Find 单次遍历节点预算，超限返回 null
        const int TickWarnMs = 50;          // tick 超过此毫秒数就打日志
        const bool DeepScanDiag = false;    // 全树计数诊断，默认关（太重）

        static Type[] _allTypes;            // 缓存 asm.GetTypes() 结果
        static volatile bool _typesLoading;
        static int _findBudget;

        static string _log;
        static Application _app;
        static Assembly _asm;
        static object _loader;
        static DispatcherTimer _timer;
        static volatile bool _timerRunning;

        static object _page;
        static bool _pageHooked;

        static volatile int _phase;
        static volatile int _startIssued;
        static volatile bool _settled;
        static volatile int _finishCount;
        static volatile int _retryCount;
        static long _lastFinishTick;
        static volatile bool _inFinish;

        static int _d1, _d4, _dResult, _dState, _dOpt, _dAni;
        static readonly object _lockObj = new object();

        // ============ 入口 ============

        // 托管入口点
        public static int Init(string arg)
        {
            try
            {
                _log = Path.Combine(
                    Path.GetDirectoryName(typeof(Entry).Assembly.Location) ?? ".", "pclpatch.log");
                TruncateLog();
                Log("=== init pid=" + Process.GetCurrentProcess().Id);
                var t = new Thread(Bootstrap) { IsBackground = true };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                return 0;
            }
            catch (Exception ex) { try { Log("fatal " + ex); } catch { } return 0; }
        }

        // 等待应用程序实例就绪后切换到 UI 线程
        static void Bootstrap()
        {
            try
            {
                for (int i = 0; i < 600 && _app == null; i++)
                {
                    _app = Application.Current;
                    if (_app == null) Thread.Sleep(100);
                }
                if (_app == null) { Log("非 UI 进程,静默退出"); return; }
                Log("拿到 Application");
                _app.Dispatcher.BeginInvoke(new Action(ArmUi), DispatcherPriority.Background);
            }
            catch (Exception ex) { Log("bootstrap " + ex); }
        }

        // 启动定时器并开始后台类型缓存预热
        static void ArmUi()
        {
            try
            {
                _timer = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(PollStartupMs),
                    DispatcherPriority.Background,
                    OnTick,
                    _app.Dispatcher);
                _timer.Start();
                _timerRunning = true;
                Log("轮询已启动 " + PollStartupMs + "ms（成功后停止）");

                // 启动后台线程预热类型缓存
                var warm = new Thread(() =>
                {
                    try
                    {
                        _typesLoading = true;
                        var sw = Stopwatch.StartNew();
                        var types = SafeGetTypes(_asm);
                        lock (_lockObj) { _allTypes = types; }
                        Log("[预热] 类型缓存完成 " + (types == null ? 0 : types.Length)
                            + " 个，耗时 " + sw.ElapsedMilliseconds + "ms（后台线程，未阻塞 UI）");
                    }
                    catch { }
                    finally { _typesLoading = false; }
                })
                { IsBackground = true };
                warm.Start();
            }
            catch (Exception ex) { Log("arm " + ex); }
        }

        // 获取程序集中的全部类型，部分加载失败时返回可用部分
        static Type[] SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                var list = new System.Collections.Generic.List<Type>();
                foreach (var t in ex.Types) if (t != null) list.Add(t);
                return list.ToArray();
            }
            catch { return null; }
        }

        // 获取已缓存的类型数组，未就绪时返回 null
        static Type[] GetTypesCached()
        {
            lock (_lockObj) { return _allTypes; }
        }

        // 获取预热是否正在进行
        static bool TypesWarming { get { return _typesLoading; } }

        // 停止定时器
        static void StopTimer()
        {
            try { if (_timer != null && _timerRunning) { _timer.Stop(); _timerRunning = false; } }
            catch { }
        }

        // 以指定间隔启动定时器
        static void StartTimer(int ms)
        {
            try
            {
                if (_timer == null) return;
                _timer.Stop();
                _timer.Interval = TimeSpan.FromMilliseconds(ms);
                _timer.Start();
                _timerRunning = true;
            }
            catch { }
        }

        // ============ 轮询 ============

        // 定时器回调，检查并修正页面状态
        static void OnTick(object sender, EventArgs e)
        {
            try
            {
                var win = _app.MainWindow;
                if (win == null) return;
                if (_asm == null) _asm = win.GetType().Assembly;

                EnsureLoader();
                if (_loader == null) return;

                var page = GetPage(win);
                if (page == null) return;

                if (InnerJson() == null)
                {
                    Once(ref _dResult, "数据未就绪，等待 Start");
                    if (_startIssued == 0)
                    {
                        _startIssued = 1;
                        Log("首次启动加载器");
                        var th = new Thread(DoStart) { IsBackground = true };
                        th.SetApartmentState(ApartmentState.STA);
                        th.Start();
                    }
                    return;
                }
                if (_phase < 2) { _phase = 2; Log("数据已就绪"); }

                if (!NeedsFinish(page))
                {
                    if (!_settled)
                    {
                        _settled = true;
                        StopTimer();
                        StartTimer(PollFallbackMs);
                        Log("✔ 收尾完成，停止轮询（兜底 " + PollFallbackMs + "ms）");
                    }
                    return;
                }

                long now = Environment.TickCount;
                if (now - _lastFinishTick < ReCheckDelayMs) return;
                _lastFinishTick = now;

                var pg = page;
                _app.Dispatcher.BeginInvoke(
                    new Action(() => Finish(pg)), DispatcherPriority.Background);
            }
            catch (Exception ex) { Once(ref _d4, "tick " + ex.Message); }
        }

        // 获取当前页面实例，缓存失效时重新查找
        static object GetPage(Window win)
        {
            var cached = _page as DependencyObject;
            if (cached != null && IsInTree(cached)) return _page;

            var found = Find(win, PageTypeName, 0);
            if (found == null) return null;

            _page = found;
            HookPage(found);
            Log("取得页面 " + found.GetType().FullName);
            return found;
        }

        // 判断元素是否仍位于主窗口的视觉树中
        static bool IsInTree(DependencyObject d)
        {
            try
            {
                var cur = d;
                for (int i = 0; i < 64; i++)
                {
                    if (cur == null) return false;
                    if (ReferenceEquals(cur, _app.MainWindow)) return true;
                    DependencyObject p;
                    try { p = VisualTreeHelper.GetParent(cur); } catch { p = null; }
                    if (p == null)
                    {
                        var fe = cur as FrameworkElement;
                        if (fe != null) { try { p = fe.Parent; } catch { } }
                    }
                    if (p == null) return false;
                    cur = p;
                }
                return false;
            }
            catch { return false; }
        }

        // 订阅页面的可见性与加载事件
        static void HookPage(DependencyObject page)
        {
            if (_pageHooked) return;
            _pageHooked = true;
            try
            {
                var fe = page as FrameworkElement;
                if (fe == null) return;
                fe.IsVisibleChanged += OnPageVisibilityChanged;
                fe.Loaded += OnPageLoaded;
                fe.Unloaded += OnPageUnloaded;
                Log("✔ 已订阅页面事件（IsVisibleChanged/Loaded/Unloaded）");
            }
            catch (Exception ex) { Log("hookpage " + ex.Message); }
        }

        // 页面可见性变化回调
        static void OnPageVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            try { Wake("页面 IsVisible -> " + e.NewValue); } catch { }
        }

        // 页面加载完成回调
        static void OnPageLoaded(object sender, RoutedEventArgs e) { try { Wake("页面 Loaded"); } catch { } }
        // 页面卸载回调
        static void OnPageUnloaded(object sender, RoutedEventArgs e) { try { Log("页面 Unloaded"); } catch { } }

        // 唤醒定时器以重新检查页面状态
        static void Wake(string why)
        {
            try
            {
                if (_app == null) return;
                Log("[唤醒] " + why);
                _settled = false;
                StartTimer(PollStartupMs);
            }
            catch { }
        }

        // 判断页面是否仍处于加载态
        static bool NeedsFinish(object page)
        {
            try
            {
                var panLoad = ReadMember(page, "PanLoad") as FrameworkElement;
                if (panLoad != null && panLoad.Visibility == Visibility.Visible) return true;

                var loading = ReadMember(page, LoadingName);
                if (loading != null)
                {
                    var sp = loading.GetType().GetProperty("State",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (sp != null)
                    {
                        object cur = null;
                        try { cur = sp.GetValue(loading, null); } catch { }
                        if (cur != null && !ReferenceEquals(cur, _loader)) return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        // ============ 收尾 ============

        // 对页面执行一次状态修正与渲染收尾
        static void Finish(object page)
        {
            if (_inFinish) return;
            _inFinish = true;
            var sw = Stopwatch.StartNew();
            try
            {
                _finishCount++;
                Log("--- 收尾 #" + _finishCount + " ---");

                var arr = TryGetVersionsArray();
                if (arr == null) { Log("未能取到 versions -> 放弃"); return; }

                if (SetMember(page, "MinecraftVersions", arr))
                    Log("MinecraftVersions 赋值成功 count=" + CountOf(arr));
                else
                    Log("MinecraftVersions 赋值失败");

                SetLoadingStateFinish();
                SwitchPanels(page);

                bool ok = InvokeVerbose(page, "LoadMinecraft_OnFinish");
                Log("OnFinish 结果=" + ok + " 累计=" + sw.ElapsedMilliseconds + "ms");

                if (CallShowList && !ok)
                {
                    Log("!! 按配置二次调用 ShowMinecraftVersionList（可能很慢）");
                    InvokeVerbose(page, "ShowMinecraftVersionList");
                }

                EnsureState(page);

                OptimizeRendering(page);
                DumpAnimationSwitches();

                ReportPanels(page);

                if (NeedsFinish(page) && _retryCount < MaxRetry)
                {
                    _retryCount++;
                    Log("复检仍显示加载态 -> 重试 #" + _retryCount);
                    var pg = page;
                    var d = new DispatcherTimer(
                        TimeSpan.FromMilliseconds(500),
                        DispatcherPriority.Background,
                        (s, ev) => { ((DispatcherTimer)s).Stop(); Finish(pg); },
                        _app.Dispatcher);
                    d.Start();
                }
                else
                {
                    _retryCount = 0;
                    if (!NeedsFinish(page) && !_settled)
                    {
                        _settled = true;
                        StopTimer();
                        StartTimer(PollFallbackMs);
                        Log("✔ 状态正常，停止轮询（兜底 " + PollFallbackMs + "ms）");
                    }
                }

                Log("--- 收尾 #" + _finishCount + " 结束，总耗时 " + sw.ElapsedMilliseconds + "ms ---");
            }
            catch (Exception ex) { Log("finish " + ex.Message); }
            finally { _inFinish = false; }
        }

        // ============ 渲染优化 ============

        // 为列表容器启用 UI 虚拟化并优化布局
        static void OptimizeRendering(object page)
        {
            if (!EnableVirtualization) return;
            if (Interlocked.Exchange(ref _dOpt, 1) != 0) return;
            try
            {
                var pan = ReadMember(page, "PanBack") as DependencyObject;
                if (pan == null) { Log("[优化] 未找到 PanBack，跳过"); return; }

                Log("[优化] PanBack 类型=" + pan.GetType().FullName
                    + " 可视子节点数=" + CountVisualDescendants(pan));

                // 按项滚动是虚拟化的前提
                try
                {
                    ScrollViewer.SetCanContentScroll(pan, true);
                    Log("[优化] ✔ ScrollViewer.CanContentScroll=True");
                }
                catch (Exception ex) { Log("[优化] CanContentScroll 失败 " + ex.Message); }

                var ic = FindDescendant(pan, typeof(ItemsControl)) as ItemsControl;
                if (ic == null)
                {
                    Log("[优化] PanBack 下未找到 ItemsControl（可能不是标准列表）");
                    return;
                }

                Log("[优化] 列表控件=" + ic.GetType().FullName + " 数据源项数=" + SafeItemsCount(ic));

                try
                {
                    VirtualizingPanel.SetIsVirtualizing(ic, true);
                    VirtualizingPanel.SetIsVirtualizingWhenGrouping(ic, true);
                    VirtualizingPanel.SetVirtualizationMode(ic, VirtualizationMode.Recycling);
                    ScrollViewer.SetCanContentScroll(ic, true);
                    Log("[优化] ✔ 已开启虚拟化 (VirtualizationMode=Recycling)");
                }
                catch (Exception ex) { Log("[优化] 虚拟化设置失败 " + ex.Message); }

                var panel = FindDescendant(ic, typeof(Panel)) as Panel;
                Log("[优化] 实际布局面板=" + (panel == null ? "?" : panel.GetType().FullName));

                if (ForceVirtualizingPanel && panel != null
                    && panel is StackPanel && !(panel is VirtualizingStackPanel))
                {
                    try
                    {
                        var factory = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
                        ic.ItemsPanel = new ItemsPanelTemplate(factory);
                        Log("[优化] ✔ ItemsPanel 已替换为 VirtualizingStackPanel");
                    }
                    catch (Exception ex) { Log("[优化] ItemsPanel 替换失败 " + ex.Message); }
                }

                Log("[优化] 处理后可视子节点数=" + CountVisualDescendants(pan));
            }
            catch (Exception ex) { Log("[优化] " + ex.Message); }
        }

        // 扫描并尝试关闭动画开关
        static void DumpAnimationSwitches()
        {
            if (!TryDisableAnimation) return;
            if (Interlocked.Exchange(ref _dAni, 1) != 0) return;
            var th = new Thread(() =>
            {
                try
                {
                    var types = GetTypesCached();
                    if (types == null) { Log("[Ani] 缓存未就绪，跳过"); return; }
                    ScanAnimationTypes(types);
                }
                catch (Exception ex) { Log("[Ani] " + ex.Message); }
            })
            { IsBackground = true };
            th.Start();
        }

        // 在类型集合中查找动画开关并置为关闭
        static void ScanAnimationTypes(Type[] types)
        {
            try
            {

                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (t.Name.IndexOf("Ani", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    foreach (var p in t.GetProperties(F))
                    {
                        if (p.PropertyType != typeof(bool) && p.PropertyType != typeof(double)) continue;
                        object v = null;
                        try { v = p.GetValue(null, null); } catch { }
                        Log("[Ani] " + t.Name + "." + p.Name + " (" + p.PropertyType.Name + ") = " + v);

                        if (p.PropertyType == typeof(bool) && p.CanWrite
                            && p.Name.IndexOf("Enable", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try { p.SetValue(null, false, null); Log("      -> 已置 false"); }
                            catch { }
                        }
                    }

                    foreach (var f in t.GetFields(F))
                    {
                        if (f.FieldType != typeof(bool) && f.FieldType != typeof(double)) continue;
                        object v = null;
                        try { v = f.GetValue(null); } catch { }
                        Log("[Ani] " + t.Name + "." + f.Name + " (" + f.FieldType.Name + ") = " + v);

                        if (f.FieldType == typeof(bool)
                            && f.Name.IndexOf("Enable", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try { f.SetValue(null, false); Log("      -> 已置 false"); }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex) { Log("[Ani] 扫描失败 " + ex.Message); }
        }

        // 获取数据源的项数
        static int SafeItemsCount(ItemsControl ic)
        {
            try
            {
                if (ic.ItemsSource == null && ic.Items == null) return -1;
                var src = ic.ItemsSource;
                if (src == null) return ic.Items.Count;
                var p = src.GetType().GetProperty("Count");
                return p == null ? -1 : Convert.ToInt32(p.GetValue(src, null));
            }
            catch { return -1; }
        }

        // 在视觉树中按类型查找后代元素
        static DependencyObject FindDescendant(DependencyObject root, Type type)
        {
            if (root == null) return null;
            if (!(root is Visual || root is Visual3D)) return null;
            if (type.IsAssignableFrom(root.GetType())) return root;

            int n;
            try { n = VisualTreeHelper.GetChildrenCount(root); } catch { return null; }
            for (int i = 0; i < n; i++)
            {
                DependencyObject c;
                try { c = VisualTreeHelper.GetChild(root, i); } catch { continue; }
                var r = FindDescendant(c, type);
                if (r != null) return r;
            }
            return null;
        }

        // 统计元素的视觉后代数量
        static int CountVisualDescendants(DependencyObject root)
        {
            int count = 0;
            try { Walk(root, ref count, 0); } catch { }
            return count;
        }

        // 递归遍历视觉后代
        static void Walk(DependencyObject d, ref int count, int depth)
        {
            if (d == null || depth > 60 || count > 20000) return;
            if (!(d is Visual || d is Visual3D)) return;
            count++;
            int n;
            try { n = VisualTreeHelper.GetChildrenCount(d); } catch { return; }
            for (int i = 0; i < n; i++)
            {
                DependencyObject c;
                try { c = VisualTreeHelper.GetChild(d, i); } catch { continue; }
                Walk(c, ref count, depth + 1);
            }
        }

        // ============ 基础 ============

        // 获取加载器实例
        static void EnsureLoader()
        {
            if (_loader != null) return;
            try
            {
                var lt = _asm.GetType("PCL." + LoaderTypeName) ?? FindType(_asm, LoaderTypeName);
                if (lt == null) { Once(ref _d1, "未找到类型 " + LoaderTypeName); return; }
                var f = lt.GetField(LoaderFieldName, BindingFlags.Public | BindingFlags.Static);
                _loader = f == null ? null : f.GetValue(null);
                if (_loader == null) { Once(ref _d1, "未找到字段 " + LoaderFieldName); return; }
                Once(ref _d1, "真 loader = " + _loader.GetType().Name + " @" + _loader.GetHashCode());
            }
            catch (Exception ex) { Log("ensureloader " + ex.Message); }
        }

        // 将加载控件的状态纠正为指定加载器
        static void EnsureState(object page)
        {
            try
            {
                var loading = ReadMember(page, LoadingName);
                if (loading == null) { Once(ref _dState, "未找到 " + LoadingName); return; }
                var sp = loading.GetType().GetProperty("State",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (sp == null) { Once(ref _dState, "MyLoading 无 State 属性"); return; }

                object cur = null;
                try { cur = sp.GetValue(loading, null); } catch { }
                if (cur != null && ReferenceEquals(cur, _loader)) return;

                Once(ref _dState, "State -> 纠正为真 loader");
                if (SetMember(loading, "State", _loader)) Log("✔ State 已纠正");
                else Log("✘ State 纠正失败");
            }
            catch (Exception ex) { Log("ensurestate " + ex.Message); }
        }

        // 将加载器的状态置为完成
        static void SetLoadingStateFinish()
        {
            try
            {
                var lsp = _loader.GetType().GetProperty("LoadingState",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (lsp == null || !lsp.PropertyType.IsEnum) return;

                var names = Enum.GetNames(lsp.PropertyType);
                foreach (var want in new[] { "Finish", "Finished", "Done", "Stop", "Success", "Completed" })
                {
                    foreach (var n in names)
                    {
                        if (!string.Equals(n, want, StringComparison.OrdinalIgnoreCase)) continue;
                        object before = null;
                        try { before = lsp.GetValue(_loader, null); } catch { }
                        if (before != null && string.Equals(before.ToString(), n, StringComparison.OrdinalIgnoreCase))
                            return;
                        if (lsp.CanWrite)
                        {
                            lsp.SetValue(_loader, Enum.Parse(lsp.PropertyType, n), null);
                            Log("LoadingState -> " + n);
                        }
                        return;
                    }
                }
            }
            catch (Exception ex) { Log("setloadingstate " + ex.Message); }
        }

        // 切换加载遮罩与内容面板的可见性
        static void SwitchPanels(object page)
        {
            try
            {
                SetVis(page, "PanLoad", Visibility.Collapsed);
                SetVis(page, "PanAllBack", Visibility.Visible);
                SetVis(page, "PanBack", Visibility.Visible);
            }
            catch (Exception ex) { Log("switchpanels " + ex.Message); }
        }

        // 设置指定元素的可见性
        static void SetVis(object page, string name, Visibility want)
        {
            var el = ReadMember(page, name) as FrameworkElement;
            if (el == null) return;
            if (el.Visibility == want) return;
            Log("面板 " + name + " " + el.Visibility + " -> " + want);
            el.Visibility = want;
        }

        // 输出各面板的当前可见性
        static void ReportPanels(object page)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("面板: ");
                foreach (var n in new[] { "PanLoad", "PanAllBack", "PanBack", "PanInner", "PanMinecraft" })
                {
                    var el = ReadMember(page, n) as FrameworkElement;
                    sb.Append(n + "=" + (el == null ? "?" : el.Visibility.ToString()) + "  ");
                }
                Log(sb.ToString());
            }
            catch { }
        }

        // 启动加载器并等待数据就绪
        static void DoStart()
        {
            try
            {
                InvokeVerbose(_loader, "Start");
                int waited = 0;
                while (waited < StartWaitTimeoutMs)
                {
                    Thread.Sleep(300); waited += 300;
                    if (InnerJson() != null)
                    {
                        Log("版本列表已就绪 (" + waited + "ms)");
                        _phase = 2;
                        return;
                    }
                }
                Log("等待超时");
            }
            catch (Exception ex) { Log("dostart " + ex.Message); }
        }

        // ============ 数据读取 ============

        // 从加载器输出中取出内层数据对象
        static object InnerJson()
        {
            var o = ReadMember(_loader, "Output");
            if (o == null) return null;
            var v = ReadMember(o, "Value");
            if (v == null) return null;
            var inner = ReadMember(v, "Value");
            return inner ?? v;
        }

        // 取出版本数组
        static object TryGetVersionsArray()
        {
            var jo = InnerJson();
            if (jo == null) return null;
            try
            {
                var t = jo.GetType();
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var idx = p.GetIndexParameters();
                    if (idx.Length == 1 && idx[0].ParameterType == typeof(string))
                    {
                        try
                        {
                            var r = p.GetValue(jo, new object[] { "versions" });
                            if (r != null) return Clone(r);
                        }
                        catch { }
                    }
                }
                foreach (var name in new[] { "versions", "Versions", "VersionList" })
                {
                    var r = ReadMember(jo, name);
                    if (r != null) return Clone(r);
                }
            }
            catch (Exception ex) { Log("trygetversions " + ex.Message); }
            return null;
        }

        // 创建对象副本
        static object Clone(object o)
        {
            try
            {
                var m = o.GetType().GetMethod("DeepClone", Type.EmptyTypes);
                if (m != null) return m.Invoke(o, null);
            }
            catch { }
            return o;
        }

        // 获取集合的项数
        static int CountOf(object o)
        {
            try
            {
                var p = o.GetType().GetProperty("Count");
                if (p != null) return Convert.ToInt32(p.GetValue(o, null));
            }
            catch { }
            return -1;
        }

        // ============ 反射工具 ============

        // 调用无参方法并输出耗时
        static bool InvokeVerbose(object o, string name)
        {
            var ty = o.GetType();
            MethodInfo best = null;
            foreach (var m in ty.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.Static))
            {
                if (!string.Equals(m.Name, name, StringComparison.Ordinal)) continue;
                bool allOpt = true;
                foreach (var p in m.GetParameters())
                    if (!(p.IsOptional || p.HasDefaultValue)) { allOpt = false; break; }
                if (!allOpt) continue;
                if (best == null || m.GetParameters().Length < best.GetParameters().Length) best = m;
            }
            if (best == null) return false;

            var ps = best.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
                args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                    : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);

            var sw = Stopwatch.StartNew();
            try
            {
                best.Invoke(o, args);
                Log("  调用 " + name + " 成功 (" + sw.ElapsedMilliseconds + "ms)");
                return true;
            }
            catch (TargetInvocationException tie)
            {
                Log("  调用 " + name + " 内部异常: "
                    + (tie.InnerException == null ? "" : tie.InnerException.Message)
                    + " (" + sw.ElapsedMilliseconds + "ms)");
                return false;
            }
            catch (Exception ex)
            {
                Log("  调用 " + name + " 失败: " + ex.Message + " (" + sw.ElapsedMilliseconds + "ms)");
                return false;
            }
        }

        // 按名称读取属性或字段的值
        static object ReadMember(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                var p = t.GetProperty(name, F);
                if (p != null) return p.GetValue(o, null);
                var f = t.GetField(name, F) ?? t.GetField("_" + name, F);
                if (f != null) return f.GetValue(o);
                for (var b = t.BaseType; b != null && b != typeof(object); b = b.BaseType)
                {
                    p = b.GetProperty(name, F);
                    if (p != null) return p.GetValue(o, null);
                    f = b.GetField(name, F);
                    if (f != null) return f.GetValue(o);
                }
            }
            catch { }
            return null;
        }

        // 按名称写入属性或字段的值
        static bool SetMember(object o, string name, object val)
        {
            try
            {
                var t = o.GetType();
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var p = t.GetProperty(name, F);
                if (p != null && p.CanWrite) { p.SetValue(o, val, null); return true; }
                var f = t.GetField(name, F) ?? t.GetField("_" + name, F);
                if (f != null) { f.SetValue(o, val); return true; }
                for (var b = t.BaseType; b != null && b != typeof(object); b = b.BaseType)
                {
                    p = b.GetProperty(name, F);
                    if (p != null && p.CanWrite) { p.SetValue(o, val, null); return true; }
                    f = b.GetField(name, F);
                    if (f != null) { f.SetValue(o, val); return true; }
                }
            }
            catch (Exception ex) { Log("setmember " + name + " " + ex.Message); }
            return false;
        }

        // 在视觉树中按类型名查找元素
        static DependencyObject Find(DependencyObject root, string namePart, int depth)
        {
            if (root == null || depth > 40) return null;
            if (depth == 0) _findBudget = FindNodeBudget;   // 每次顶层调用重置预算
            if (--_findBudget < 0) return null;            // 预算耗尽 -> 放弃本轮，防遍历失控
            if (!(root is Visual || root is Visual3D)) return null;
            if (root.GetType().Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0) return root;
            int n;
            try { n = VisualTreeHelper.GetChildrenCount(root); } catch { return null; }
            for (int i = 0; i < n; i++)
            {
                DependencyObject c;
                try { c = VisualTreeHelper.GetChild(root, i); } catch { continue; }
                var r = Find(c, namePart, depth + 1);
                if (r != null) return r;
            }
            var fe = root as FrameworkElement;
            if (fe != null)
            {
                try
                {
                    foreach (var c in LogicalTreeHelper.GetChildren(fe))
                    {
                        var d = c as DependencyObject;
                        if (d == null) continue;
                        var r = Find(d, namePart, depth + 1);
                        if (r != null) return r;
                    }
                }
                catch { }
            }
            return null;
        }

        // 在类型缓存中按名称查找类型
        static Type FindType(Assembly asm, string name)
        {
            try
            {
                var types = GetTypesCached();
                if (types == null)
                {
                    Once(ref _d1, "[预热等待] 类型缓存未就绪，本轮跳过（不在 UI 线程枚举）");
                    return null;
                }
                foreach (var t in types)
                    if (t != null && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            }
            catch { }
            return null;
        }

        // 输出仅一次的消息
        static void Once(ref int flag, string msg)
        {
            if (Interlocked.Exchange(ref flag, 1) == 0) Log(msg);
        }

        // 清空日志文件，使本次运行从空文件开始
        static void TruncateLog()
        {
            try { File.WriteAllText(_log ?? "pclpatch.log", string.Empty); }
            catch { }
        }

        // 追加一行日志
        static void Log(string msg)
        {
            try
            {
                lock (_lockObj)
                    File.AppendAllText(_log ?? "pclpatch.log",
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }
    }
}
