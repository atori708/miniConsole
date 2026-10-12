using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace MiniConsoleBench
{
    /// <summary>
    /// miniConsole の計測用。パッケージには含めない。
    /// メニュー (Tools/miniConsole Bench) から実行するか、unity CLI から
    /// <c>unity command eval --code 'return MiniConsoleBench.Bench.RunAll();'</c> のように呼ぶ。
    /// 5 秒を超える計測 (Emit など) を eval から呼ぶときは Defer で包み、LastResult で結果を取る。
    /// LogStore などは internal なのでリフレクションで触る。
    /// </summary>
    [InitializeOnLoad]
    public static class Bench
    {
        const string Menu = "Tools/miniConsole Bench/";
        const int DefaultCount = 49999;
        const string ReloadStartKey = "MiniConsoleBench.ReloadStart";
        const string ReloadResultKey = "MiniConsoleBench.ReloadResult";
        const string ReloadLogKey = "MiniConsoleBench.ReloadLog";
        const string DeferredResultKey = "MiniConsoleBench.DeferredResult";
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        static readonly Type StoreType = Type.GetType("MiniConsole.LogStore, MiniConsole.Editor");
        static readonly Type ItemType = Type.GetType("MiniConsole.LogItem, MiniConsole.Editor");
        static readonly Type WindowType = Type.GetType("MiniConsole.MiniConsoleWindow, MiniConsole.Editor");
        static readonly Type SettingsType = Type.GetType("MiniConsole.ConsoleSettings, MiniConsole.Editor");

        // beforeAssemblyReload から、リロード後の [InitializeOnLoad] までの時間を測る
        static Bench()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (SessionState.GetBool(ReloadLogKey, false)) SessionState.SetString(ReloadStartKey, DateTime.UtcNow.Ticks.ToString());
            };

            var start = SessionState.GetString(ReloadStartKey, "");
            if (start.Length == 0) return;

            SessionState.EraseString(ReloadStartKey);
            SessionState.EraseBool(ReloadLogKey);
            var ms = (DateTime.UtcNow.Ticks - long.Parse(start)) / 10000.0;
            var result = $"Domain reload: {ms:F0} ms (items={Items().Count})";
            SessionState.SetString(ReloadResultKey, result);
            Debug.Log($"[miniConsole Bench] {result}");
        }

        [MenuItem(Menu + "Run All (Fill 50k + Rebuild)")]
        static void RunAllMenu() => Debug.Log($"[miniConsole Bench]\n{RunAll()}");

        [MenuItem(Menu + "Fill 50k Logs")]
        static void FillMenu() => Debug.Log($"[miniConsole Bench] {Fill(DefaultCount)}");

        [MenuItem(Menu + "Emit 50k Logs via Debug.Log")]
        static void EmitMenu() => Debug.Log($"[miniConsole Bench]\n{Emit()}");

        [MenuItem(Menu + "Emit 50k Logs via Debug.Log (with Baseline)")]
        static void EmitBaselineMenu() => Debug.Log($"[miniConsole Bench]\n{Emit(baseline: true)}");

        [MenuItem(Menu + "Measure Rebuild")]
        static void RebuildMenu() => Debug.Log($"[miniConsole Bench]\n{MeasureRebuildAll()}");

        [MenuItem(Menu + "Measure Domain Reload")]
        static void ReloadMenu() => RequestReload();

        [MenuItem(Menu + "Stats")]
        static void StatsMenu() => Debug.Log($"[miniConsole Bench] {Stats()}");

        [MenuItem(Menu + "Clear Logs")]
        static void ClearMenu() => Clear();

        /// <summary>全ログを消してから Fill → Stats → 各条件の再構築を測る。</summary>
        public static string RunAll(int count = DefaultCount)
        {
            Clear();
            var sb = new StringBuilder();
            sb.AppendLine(Fill(count));
            sb.AppendLine(Stats());
            sb.Append(MeasureRebuildAll());
            return sb.ToString();
        }

        /// <summary>
        /// 実際のログに近いダミーを LogStore.Add で直接積む (Unity の Console には出ない)。内訳は <see cref="NextLog"/>。
        /// </summary>
        public static string Fill(int count = DefaultCount)
        {
            var store = Store();
            var add = StoreType.GetMethod("Add", Any);
            var keyGetter = ItemType.GetProperty("CollapseKey").GetGetMethod();
            var severityField = ItemType.GetField("severity");
            var random = new System.Random(1234);
            var hashWatch = new Stopwatch();

            for (var i = 0; i < count; i++)
            {
                var (type, template, message) = NextLog(random, i);

                var item = Activator.CreateInstance(ItemType);
                severityField.SetValue(item, Enum.ToObject(severityField.FieldType, ToSeverity(type)));
                ItemType.GetField("message").SetValue(item, message);
                ItemType.GetField("stackTrace").SetValue(item, StackTrace(template));
                ItemType.GetField("timestamp").SetValue(item, $"[12:{i / 60 % 60:00}:{i % 60:00}]");

                // LogStore.Add 内で行う受け取り時のハッシュ計算だけを切り出して測る (Add 内では計算済みなので再計算されない)
                var getKey = (Func<Hash128>)Delegate.CreateDelegate(typeof(Func<Hash128>), item, keyGetter);
                hashWatch.Start();
                getKey();
                hashWatch.Stop();

                add.Invoke(store, new[] { item });
            }

            StoreType.GetMethod("NotifyChanged", Any).Invoke(null, null);
            var hashMs = hashWatch.Elapsed.TotalMilliseconds;
            return $"Filled {count} logs: hashing {hashMs:F1} ms ({hashMs * 1000 / count:F2} us/log)";
        }

        /// <summary>
        /// 実際に Debug.Log / LogWarning / LogError でログを出す。Unity 標準の Console にも出て、
        /// miniConsole では LogCollector の受信 → Flush → LogStore.Add の実経路を通る。
        /// baseline = true なら、miniConsole の受信ハンドラを外した状態でも同じ件数を出し、1 件あたりの上乗せを求める
        /// (Console に 2 倍の件数が出る)。
        /// </summary>
        public static string Emit(int count = DefaultCount, bool baseline = false)
        {
            var collector = Type.GetType("MiniConsole.LogCollector, MiniConsole.Editor");
            var flush = collector.GetMethod("Flush", Any);
            var pending = (ICollection)collector.GetField("Pending", Any).GetValue(null);
            flush.Invoke(null, null);

            var sb = new StringBuilder();
            if (baseline)
            {
                var handler = (Application.LogCallback)Delegate.CreateDelegate(
                    typeof(Application.LogCallback), collector.GetMethod("OnLogReceived", Any));
                Application.logMessageReceivedThreaded -= handler;
                try
                {
                    var baseMs = EmitLogs(count);
                    sb.AppendLine($"Emit {count} logs without miniConsole: {baseMs:F0} ms ({baseMs * 1000 / count:F1} us/log)");
                }
                finally
                {
                    Application.logMessageReceivedThreaded += handler;
                }
            }

            var emitMs = EmitLogs(count);
            var received = pending.Count;
            sb.AppendLine($"Emit {count} logs with miniConsole: {emitMs:F0} ms ({emitMs * 1000 / count:F1} us/log), queued={received}");

            // 普段は EditorApplication.update で呼ばれる取り込みを、ここで直接呼んで測る
            var sw = Stopwatch.StartNew();
            flush.Invoke(null, null);
            var flushMs = sw.Elapsed.TotalMilliseconds;
            sb.Append($"Flush (LogStore.Add incl. hashing): {flushMs:F1} ms ({flushMs * 1000 / Math.Max(received, 1):F2} us/log), items={Items().Count}");
            return sb.ToString();
        }

        static double EmitLogs(int count)
        {
            var random = new System.Random(1234);
            var sw = new Stopwatch();
            for (var i = 0; i < count; i++)
            {
                var (type, template, message) = NextLog(random, i);
                sw.Start();
                EmitAt(template % 5, type, message);
                sw.Stop();
            }

            return sw.Elapsed.TotalMilliseconds;
        }

        // 呼び出しの深さを変えて、スタックトレースがテンプレートごとに異なるようにする
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void EmitAt(int depth, LogType type, string message)
        {
            if (depth > 0)
            {
                EmitAt(depth - 1, type, message);
                return;
            }

            switch (type)
            {
                case LogType.Warning: Debug.LogWarning(message); break;
                case LogType.Error: Debug.LogError(message); break;
                default: Debug.Log(message); break;
            }
        }

        /// <summary>
        /// 8 割 Log / 1.5 割 Warning / 0.5 割 Error。7 割は同じ文面の繰り返し (Collapse で畳まれる)、3 割はユニーク。
        /// </summary>
        static (LogType type, int template, string message) NextLog(System.Random random, int i)
        {
            var roll = random.Next(100);
            var type = roll < 80 ? LogType.Log : roll < 95 ? LogType.Warning : LogType.Error;
            var template = random.Next(100);
            var unique = random.Next(10) < 3;
            var message = template % 4 == 0
                ? $"敵 {template} 番をスポーンしました 位置=({random.Next(100)}, 0, {random.Next(100)})"
                : unique
                    ? $"Player {template} received damage {random.Next(1000)} from source {i}\nDetail: frame={i}"
                    : $"Player {template} state changed: Idle -> Running";
            return (type, template, message);
        }

        // MiniConsole.LogSeverity の値 (Warning = 0, Error = 1, Info = 2)
        static int ToSeverity(LogType type) => type switch
        {
            LogType.Warning => 0,
            LogType.Error => 1,
            _ => 2,
        };

        // 呼び出し元ごとにスタックトレースを変える。実際のログでも毎回新しい string が来るので、インスタンスは共有させない
        static string StackTrace(int template)
        {
            var sb = new StringBuilder();
            sb.Append("UnityEngine.Debug:Log (object)\n");
            sb.Append($"Game.Characters.PlayerController{template}:UpdateState (Game.Characters.PlayerState) (at Assets/Scripts/Game/Characters/PlayerController{template}.cs:{100 + template})\n");
            sb.Append("Game.Characters.PlayerStateMachine:Tick (single) (at Assets/Scripts/Game/Characters/PlayerStateMachine.cs:57)\n");
            sb.Append("Game.Core.Systems.CharacterSystem:Update (single) (at Assets/Scripts/Game/Core/Systems/CharacterSystem.cs:212)\n");
            sb.Append("Game.Core.GameLoop:RunSystems (Game.Core.UpdatePhase,single) (at Assets/Scripts/Game/Core/GameLoop.cs:88)\n");
            sb.Append("Game.Core.GameLoop:Update () (at Assets/Scripts/Game/Core/GameLoop.cs:41)\n");
            sb.Append("Cysharp.Threading.Tasks.Internal.PlayerLoopRunner:RunCore () (at ./Library/PackageCache/com.cysharp.unitask/Runtime/Internal/PlayerLoopRunner.cs:175)\n");
            sb.Append("Cysharp.Threading.Tasks.Internal.PlayerLoopRunner:Update () (at ./Library/PackageCache/com.cysharp.unitask/Runtime/Internal/PlayerLoopRunner.cs:145)\n");
            return sb.ToString();
        }

        /// <summary>件数・文字数・C# ヒープ使用量。</summary>
        public static string Stats()
        {
            var items = Items();
            long chars = 0;
            foreach (var item in items)
            {
                chars += ((string)ItemType.GetField("message").GetValue(item))?.Length ?? 0;
                chars += ((string)ItemType.GetField("stackTrace").GetValue(item))?.Length ?? 0;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return $"Stats: items={items.Count} chars={chars:N0} (UTF-16 {chars * 2 / 1048576.0:F1} MB) monoUsed={Profiler.GetMonoUsedSizeLong() / 1048576.0:F1} MB";
        }

        public static string MeasureRebuildAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine(MeasureRebuild(false, ""));
            sb.AppendLine(MeasureRebuild(true, ""));
            sb.AppendLine(MeasureRebuild(false, "damage"));
            sb.AppendLine(MeasureRebuild(true, "damage"));
            sb.Append(MeasureRebuild(false, "存在しない文字列"));
            return sb.ToString();
        }

        /// <summary>ウィンドウの全件再構築 (Refresh) の時間。設定と検索文字列は測定後に元に戻す。</summary>
        public static string MeasureRebuild(bool collapse, string search, int repeat = 5)
        {
            var window = EditorWindow.GetWindow(WindowType);
            var settings = SettingsType.GetProperty("instance", Any).GetValue(null);
            var collapseField = SettingsType.GetField("collapse");
            var searchField = WindowType.GetField("searchText", Any);
            var refresh = WindowType.GetMethod("Refresh", Any);
            var needsRebuild = WindowType.GetField("needsRebuild", Any);
            var rows = (IList)WindowType.GetField("rows", Any).GetValue(window);

            var oldCollapse = collapseField.GetValue(settings);
            var oldSearch = searchField.GetValue(window);
            collapseField.SetValue(settings, collapse);
            searchField.SetValue(window, search);

            // 1 回目は JIT 等を含むので捨てる
            needsRebuild.SetValue(window, true);
            refresh.Invoke(window, null);

            var times = new double[repeat];
            for (var i = 0; i < repeat; i++)
            {
                needsRebuild.SetValue(window, true);
                var sw = Stopwatch.StartNew();
                refresh.Invoke(window, null);
                times[i] = sw.Elapsed.TotalMilliseconds;
            }

            var rowCount = rows.Count;
            collapseField.SetValue(settings, oldCollapse);
            searchField.SetValue(window, oldSearch);
            needsRebuild.SetValue(window, true);
            refresh.Invoke(window, null);

            Array.Sort(times);
            return $"Rebuild collapse={collapse} search='{search}': rows={rowCount} median={times[repeat / 2]:F1} ms (min {times[0]:F1} / max {times[repeat - 1]:F1})";
        }

        /// <summary>ドメインリロードを要求する。結果はリロード後に Console と <see cref="LastReload"/> で確認する。</summary>
        public static void RequestReload()
        {
            SessionState.SetBool(ReloadLogKey, true);
            EditorUtility.RequestScriptReload();
        }

        public static string LastReload() => SessionState.GetString(ReloadResultKey, "(none)");

        /// <summary>
        /// unity CLI の eval はメインスレッドで 5 秒を超えるとタイムアウトするので、時間のかかる計測
        /// (Emit など) は次のエディタ更新で実行し、結果は <see cref="LastResult"/> で後から取る。
        /// 例: <c>MiniConsoleBench.Bench.Defer(() => MiniConsoleBench.Bench.Emit(50000, true));</c>
        /// </summary>
        public static string Defer(Func<string> job)
        {
            SessionState.SetString(DeferredResultKey, "(running)");
            EditorApplication.delayCall += () =>
            {
                string result;
                try
                {
                    result = job();
                }
                catch (Exception e)
                {
                    result = $"(failed) {e}";
                }

                SessionState.SetString(DeferredResultKey, result);
            };
            return "Started. Poll MiniConsoleBench.Bench.LastResult()";
        }

        public static string LastResult() => SessionState.GetString(DeferredResultKey, "(none)");

        /// <summary>miniConsole と Unity 標準の Console の両方を、コンパイルエラーも含めて全部消す。</summary>
        public static string Clear()
        {
            Type.GetType("UnityEditor.LogEntries, UnityEditor").GetMethod("Clear", Any).Invoke(null, null);
            Items().Clear();
            StoreType.GetMethod("NotifyChanged", Any).Invoke(null, null);
            return "Cleared";
        }

        static object Store() => StoreType.GetProperty("instance", Any).GetValue(null);
        static IList Items() => (IList)StoreType.GetField("items", Any).GetValue(Store());
    }
}
