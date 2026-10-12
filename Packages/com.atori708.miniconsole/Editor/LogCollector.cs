using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;

namespace MiniConsole
{
    /// <summary>
    /// ウィンドウを開いていなくても、エディタ起動 / ドメインリロード直後からログを集める。
    /// </summary>
    [InitializeOnLoad]
    internal static class LogCollector
    {
        // ワーカースレッドからも届くので一旦キューに積み、メインスレッドで取り込む
        static readonly ConcurrentQueue<(string message, string stackTrace, LogType type, DateTime utcTime)> Pending = new();

        static readonly Regex CompilerPrefixRegex = new(@"^.*?\(\d+,\d+\):\s*(?:error|warning)\s+", RegexOptions.Compiled);

        static LogCollector()
        {
            Application.logMessageReceivedThreaded += OnLogReceived;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += Flush;
            AssemblyReloadEvents.beforeAssemblyReload += Flush;
        }

        static void OnLogReceived(string message, string stackTrace, LogType type)
        {
            message ??= string.Empty;

            // コンパイルエラーは LogType.Error としてこちらにも流れてくる
            // CompilationPipeline 側で拾っているので二重登録しないよう捨てる
            // (コンパイラ出力が LogType.Log で来ることはないので、Log では判定自体を省く)
            if (type != LogType.Log && IsCompilerOutput(message)) return;

            // ログを出したスレッドでは軽い UtcNow を取るだけ。現地時刻への変換と文字列化は Flush で行う
            Pending.Enqueue((message, stackTrace, type, DateTime.UtcNow));
        }

        /// <summary>
        /// Determine whether the error message stems from a compilation error.
        /// Regular expressions are not used, prioritizing execution speed.
        /// </summary>
        static bool IsCompilerOutput(string message) =>
            message.Contains("): error CS", StringComparison.Ordinal) ||
            message.Contains("): warning CS", StringComparison.Ordinal);

        static void Flush()
        {
            if (Pending.IsEmpty) return;

            var store = LogStore.instance;
            while (Pending.TryDequeue(out var log))
            {
                var item = new LogItem
                {
                    severity = ToSeverity(log.type),
                    origin = log.type == LogType.Exception ? LogOrigin.Exception : LogOrigin.Log,
                    message = log.message,
                    stackTrace = log.stackTrace,
                    timestamp = FormatTimestamp(log.utcTime.ToLocalTime()),
                };
                store.Add(item);
            }

            LogStore.NotifyChanged();
        }

        /// <summary>
        /// "[HH:mm:ss]" (Unity の Console と同じ形式)。長さが固定なので書式解析をせず 1 文字ずつ直接埋める。
        /// </summary>
        static string FormatTimestamp(DateTime time) => string.Create(10, time, (span, t) =>
        {
            span[0] = '[';
            WriteTwoDigits(span, 1, t.Hour);
            span[3] = ':';
            WriteTwoDigits(span, 4, t.Minute);
            span[6] = ':';
            WriteTwoDigits(span, 7, t.Second);
            span[9] = ']';
        });

        static void WriteTwoDigits(Span<char> span, int index, int value)
        {
            span[index] = (char)('0' + value / 10);
            span[index + 1] = (char)('0' + value % 10);
        }

        static LogSeverity ToSeverity(LogType type) => type switch
        {
            LogType.Log => LogSeverity.Info,
            LogType.Warning => LogSeverity.Warning,
            _ => LogSeverity.Error, // Error / Assert / Exception
        };

        /// <summary>file / line が必要になったときに初めてスタックトレースを解析する。</summary>
        internal static void EnsureResolved(LogItem item)
        {
            if (item.resolved) return;

            (item.file, item.line) = LogLocationResolver.Resolve(item.message, item.stackTrace);
            item.resolved = true;
        }

        static void OnCompilationStarted(object _)
        {
            // 前回のコンパイルエラーは常に消す。Clear on Recompile ならそれ以外のログも消す
            if (ConsoleSettings.instance.clearOnRecompile) LogStore.instance.RemoveWhere(i => true);
            else LogStore.instance.RemoveWhere(i => i.origin == LogOrigin.Compiler);
            LogStore.NotifyChanged();
        }

        static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
        {
            var store = LogStore.instance;
            var timestamp = FormatTimestamp(DateTime.Now);
            foreach (var message in messages)
            {
                if (message.type != CompilerMessageType.Error && message.type != CompilerMessageType.Warning) continue;

                store.Add(new LogItem
                {
                    severity = message.type == CompilerMessageType.Error ? LogSeverity.Error : LogSeverity.Warning,
                    origin = LogOrigin.Compiler,
                    message = CompilerPrefixRegex.Replace(message.message, string.Empty),
                    file = LogLocationResolver.NormalizePath(message.file) ?? message.file,
                    line = message.line,
                    timestamp = timestamp,
                    resolved = true,
                });
            }

            LogStore.NotifyChanged();
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode && ConsoleSettings.instance.clearOnPlay)
            {
                LogStore.instance.ClearLogs();
            }
        }
    }

    /// <summary>Clear on Build: Player ビルド開始時に Unity から呼ばれる。</summary>
    internal sealed class ClearOnBuildProcessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (ConsoleSettings.instance.clearOnBuild) LogStore.instance.ClearLogs();
        }
    }
}
