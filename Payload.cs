// PclPatch.dll v10
//
// v8 已成功出列表，但切换标签页后会重现"正在加载 - 100%"。
// v9 用常驻看门狗解决，但每 800ms 全树遍历，属于屎山。
// v10 在 v9 基础上做性能优化：
//
//   A. 轻量判定 O(1)：只看 PanLoad.Visibility 和 LoadMinecraft.State 是否被换回假的，
//      不再每次全树遍历（v9 的大头开销）
//   B. 事件优先：订阅成功 -> 轮询降为 5 秒兜底
//   C. 空闲退场：连续 N 次无需收尾 -> 彻底停掉轮询，只剩事件订阅，开销归零
//   D. 事件唤醒：退场后若 loader 状态再变，临时唤醒轮询
//   E. 顶部常量集中可调
//
// 编译：.NET Framework 4.8 类库
// 引用：PresentationCore / PresentationFramework / WindowsBase / System.Xaml

using System;
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

        const int PollFastMs = 1200;      // 事件订阅失败时的轮询间隔
        const int PollIdleMs = 5000;      // 事件订阅成功后的兜底轮询间隔
        const int IdleExitCount = 8;      // 连续 N 次无需收尾 -> 停止轮询
        const int FinishThrottleMs = 1000; // 收尾节流，防闪烁
        const int StartWaitTimeoutMs = 90000;

        static string _log;
        static Application _app;
        static Assembly _asm;
        static object _loader;
        static DispatcherTimer _timer;

        static volatile int _phase;         // 0未开始 1已Start 2数据就绪
        static volatile int _startIssued;
        static volatile int _subscribed;
        static volatile int _watchStopped;
        static volatile int _idleHits;
        static volatile int _finishCount;
        static long _lastFinishTick;

        static int _d1, _dPage, _d4, _dResult, _dState, _dExit;
        static readonly object _lockObj = new object();

        // ============ 入口 ============

        public static int Init(string arg)
        {
            try
            {
                _log = Path.Combine(
                    Path.GetDirectoryName(typeof(Entry).Assembly.Location) ?? ".", "pclpatch.log");
                Log("=== init pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
                var t = new Thread(Bootstrap) { IsBackground = true };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                return 0;
            }
            catch (Exception ex) { try { Log("fatal " + ex); } catch { } return 0; }
        }

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

        static void ArmUi()
        {
            try
            {
                _timer = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(PollFastMs),
                    DispatcherPriority.Background,
                    OnTick,
                    _app.Dispatcher);
                _timer.Start();
                Log("看门狗已启动，轮询间隔 " + PollFastMs + "ms");
            }
            catch (Exception ex) { Log("arm " + ex); }
        }

        // ============ 轮询（轻量） ============

        static void OnTick(object sender, EventArgs e)
        {
            try
            {
                var win = _app.MainWindow;
                if (win == null) return;
                if (_asm == null) _asm = win.GetType().Assembly;

                EnsureLoader();
                if (_loader == null) return;

                // 每次重新 Find：切换标签页后可能是新实例
                // 这一步无法避免（要拿当前页面），但只在有页面时继续
                var page = Find(win, PageTypeName, 0);
                if (page == null) { Once(ref _dPage, "页面不在视觉树中"); return; }

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

                // 订阅事件（只做一次）
                TrySubscribe();

                // ★ 轻量判定 O(1)，不遍历树
                if (!NeedsFinish(page))
                {
                    _idleHits++;
                    MaybeExit();
                    return;
                }

                _idleHits = 0;
                long now = Environment.TickCount;
                if (now - _lastFinishTick < FinishThrottleMs) return;
                _lastFinishTick = now;

                var pg = page;
                _app.Dispatcher.BeginInvoke(
                    new Action(() => Finish(pg)), DispatcherPriority.Background);
            }
            catch (Exception ex) { Once(ref _d4, "tick " + ex.Message); }
        }

        // ★ O(1) 判定：加载遮罩可见 或 State 被换回假 loader
        static bool NeedsFinish(object page)
        {
            try
            {
                var panLoad = ReadMember(page, "PanLoad") as FrameworkElement;
                if (panLoad != null && panLoad.Visibility == Visibility.Visible)
                    return true;

                var loading = ReadMember(page, LoadingName);
                if (loading != null)
                {
                    var sp = loading.GetType().GetProperty("State",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (sp != null)
                    {
                        object cur = null;
                        try { cur = sp.GetValue(loading, null); } catch { }
                        if (cur != null && !ReferenceEquals(cur, _loader))
                            return true;   // 被换回假的
                    }
                }
                return false;
            }
            catch { return false; }
        }

        // ★ 空闲退场
        static void MaybeExit()
        {
            if (_watchStopped != 0) return;
            if (_subscribed == 0) return;          // 没订阅成功就别退
            if (_idleHits < IdleExitCount) return;

            _watchStopped = 1;
            if (_timer != null) _timer.Stop();
            Once(ref _dExit, "★ 看门狗退场（连续 " + _idleHits + " 次无需收尾，改由事件驱动，开销归零）");
        }

        // ★ 事件唤醒
        static void Wake()
        {
            if (_watchStopped == 0) return;
            _watchStopped = 0;
            _idleHits = 0;
            if (_timer != null) _timer.Start();
            Log("事件唤醒看门狗");
        }

        // ============ 收尾（幂等） ============

        static void Finish(object page)
        {
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

                FinishLoader();
                SwitchPanels(page);

                if (!InvokeVerbose(page, "LoadMinecraft_OnFinish"))
                    InvokeVerbose(page, "ShowMinecraftVersionList");
                else
                    InvokeVerbose(page, "ShowMinecraftVersionList");

                EnsureState(page);   // 渲染后可能又被改回去
                ReportPanels(page);
                Log("--- 收尾 #" + _finishCount + " 结束 ---");
            }
            catch (Exception ex) { Log("finish " + ex.Message); }
        }

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

                Once(ref _dState, "State 原值=" + (cur == null ? "null" : "@" + cur.GetHashCode())
                    + " -> 纠正为真 loader");
                if (SetMember(loading, "State", _loader)) Log("✔ State 已纠正");
                else Log("✘ State 纠正失败");
            }
            catch (Exception ex) { Log("ensurestate " + ex.Message); }
        }

        // ============ 事件订阅 ============

        static void TrySubscribe()
        {
            if (_subscribed != 0) return;
            _subscribed = 1;
            try
            {
                var t = _loader.GetType();
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static;
                int n = 0;
                foreach (var ev in t.GetEvents(F))
                {
                    if (!(ev.Name == "LoadingStateChanged" || ev.Name == "ProgressChanged")) continue;
                    try
                    {
                        var add = ev.GetAddMethod(true);
                        if (add == null) continue;
                        var handler = Delegate.CreateDelegate(
                            ev.EventHandlerType,
                            typeof(Entry).GetMethod(nameof(OnLoaderEvent),
                                BindingFlags.Static | BindingFlags.NonPublic),
                            false);
                        if (handler == null) { Log("无法构造 " + ev.Name + " 处理器"); continue; }
                        add.Invoke(_loader, new object[] { handler });
                        Log("✔ 已订阅 " + ev.Name);
                        n++;
                    }
                    catch (Exception ex) { Log("订阅 " + ev.Name + " 失败: " + ex.Message); }
                }

                if (n > 0)
                {
                    _subscribed = 2;   // 2 = 订阅成功
                    Log("事件订阅成功，轮询降为 " + PollIdleMs + "ms 兜底");
                    if (_timer != null)
                    {
                        _timer.Stop();
                        _timer.Interval = TimeSpan.FromMilliseconds(PollIdleMs);
                        _timer.Start();
                    }
                }
                else
                {
                    _subscribed = 1;   // 1 = 订阅失败，保持快速轮询
                    Log("未订阅到事件，保持 " + PollFastMs + "ms 轮询");
                }
            }
            catch (Exception ex) { Log("trysubscribe " + ex.Message); }
        }

        static void OnLoaderEvent(object sender, object e)
        {
            try
            {
                Log("[事件] loader 状态变化");
                Wake();

                long now = Environment.TickCount;
                if (now - _lastFinishTick < FinishThrottleMs) return;
                _lastFinishTick = now;

                if (_app == null) return;
                _app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var win = _app.MainWindow;
                    if (win == null) return;
                    var page = Find(win, PageTypeName, 0);
                    if (page == null) return;
                    if (InnerJson() == null) return;
                    Finish(page);
                }), DispatcherPriority.Background);
            }
            catch { }
        }

        static void FinishLoader()
        {
            try
            {
                var lsp = _loader.GetType().GetProperty("LoadingState",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (lsp != null && lsp.PropertyType.IsEnum)
                {
                    var names = Enum.GetNames(lsp.PropertyType);
                    foreach (var want in new[] { "Finish", "Finished", "Done", "Stop", "Success", "Completed" })
                    {
                        foreach (var n in names)
                        {
                            if (!string.Equals(n, want, StringComparison.OrdinalIgnoreCase)) continue;
                            object before = null;
                            try { before = lsp.GetValue(_loader, null); } catch { }
                            if (before != null && string.Equals(before.ToString(), n, StringComparison.OrdinalIgnoreCase))
                            { goto fired; }
                            if (lsp.CanWrite)
                            {
                                lsp.SetValue(_loader, Enum.Parse(lsp.PropertyType, n), null);
                                Log("LoadingState -> " + n);
                            }
                            goto fired;
                        }
                    }
                }
            fired:
                FireEvent(_loader, "LoadingStateChanged");
                FireEvent(_loader, "ProgressChanged");
            }
            catch (Exception ex) { Log("finishloader " + ex.Message); }
        }

        static void FireEvent(object o, string eventName)
        {
            try
            {
                var t = o.GetType();
                Delegate d = null;
                var cands = new[] { eventName + "Event", "_" + eventName, eventName, eventName + "Handler" };
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static;
                foreach (var c in cands)
                {
                    var f = t.GetField(c, F);
                    if (f != null && typeof(Delegate).IsAssignableFrom(f.FieldType))
                    { d = f.GetValue(o) as Delegate; if (d != null) break; }
                }
                if (d == null)
                {
                    foreach (var f in t.GetFields(F))
                    {
                        if (typeof(Delegate).IsAssignableFrom(f.FieldType)
                            && f.Name.IndexOf(eventName, StringComparison.OrdinalIgnoreCase) >= 0)
                        { d = f.GetValue(o) as Delegate; if (d != null) break; }
                    }
                }
                if (d == null) return;

                var ps = d.GetType().GetMethod("Invoke").GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    if (i == 0) { args[i] = o; continue; }
                    if (pt == typeof(EventArgs)) { args[i] = EventArgs.Empty; continue; }
                    if (typeof(EventArgs).IsAssignableFrom(pt))
                    { try { args[i] = Activator.CreateInstance(pt); } catch { args[i] = null; } continue; }
                    if (pt.IsEnum)
                    {
                        object v = null;
                        foreach (var n in Enum.GetNames(pt))
                            if (string.Equals(n, "Finish", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(n, "Finished", StringComparison.OrdinalIgnoreCase))
                                v = Enum.Parse(pt, n);
                        args[i] = v ?? Enum.GetValues(pt).GetValue(0);
                        continue;
                    }
                    args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                }
                try { d.DynamicInvoke(args); }
                catch (TargetInvocationException tie)
                { Log("  事件 " + eventName + " 处理器异常: " + (tie.InnerException == null ? "" : tie.InnerException.Message)); }
                catch (Exception ex) { Log("  事件 " + eventName + " 失败: " + ex.Message); }
            }
            catch (Exception ex) { Log("fireevent " + ex.Message); }
        }

        // ============ 面板 ============

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

        static void SetVis(object page, string name, Visibility want)
        {
            var el = ReadMember(page, name) as FrameworkElement;
            if (el == null) return;
            if (el.Visibility == want) return;
            Log("面板 " + name + " " + el.Visibility + " -> " + want);
            el.Visibility = want;
        }

        static void ReportPanels(object page)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("面板: ");
                foreach (var n in new[] { "PanLoad", "PanAllBack", "PanBack", "PanInner", "PanMinecraft", "CardMinecraft" })
                {
                    var el = ReadMember(page, n) as FrameworkElement;
                    sb.Append(n + "=" + (el == null ? "?" : el.Visibility.ToString()) + "  ");
                }
                Log(sb.ToString());
            }
            catch { }
        }

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

        // ============ 数据：剥两层 Value ============

        static object InnerJson()
        {
            var o = ReadMember(_loader, "Output");
            if (o == null) return null;
            var v = ReadMember(o, "Value");       // DlClientListResult
            if (v == null) return null;
            var inner = ReadMember(v, "Value");   // 真正的 JObject
            return inner ?? v;
        }

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

            try { best.Invoke(o, args); Log("  调用 " + name + " 成功"); return true; }
            catch (TargetInvocationException tie)
            {
                Log("  调用 " + name + " 内部异常: " + (tie.InnerException == null ? "" : tie.InnerException.Message));
                return false;
            }
            catch (Exception ex) { Log("  调用 " + name + " 失败: " + ex.Message); return false; }
        }

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

        static DependencyObject Find(DependencyObject root, string namePart, int depth)
        {
            if (root == null || depth > 40) return null;
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

        static Type FindType(Assembly asm, string name)
        {
            try
            {
                foreach (var t in asm.GetTypes())
                    if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (var t in ex.Types)
                    if (t != null && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            }
            catch { }
            return null;
        }

        static void Once(ref int flag, string msg)
        {
            if (Interlocked.Exchange(ref flag, 1) == 0) Log(msg);
        }

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