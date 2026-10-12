using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace MiniConsole
{
    /// <summary>
    /// ログのスタックトレース / メッセージから「発生したファイルと行」を決める。
    /// メインスレッドからのみ呼ぶこと。
    /// </summary>
    internal static class LogLocationResolver
    {
        // "Ns.Type:Method (args) (at Assets/Foo.cs:42)" / 例外の "Ns.Type.Method () (at Assets/Foo.cs:42)"
        static readonly Regex FrameRegex = new(@"^(?<method>.*?)\s*\(at (?<path>.+):(?<line>\d+)\)\s*$", RegexOptions.Compiled);

        // スタックトレースが無いエンジン側のログ用: "Assets/Foo.prefab", "Assets/Foo.shader(12)", "Assets/Foo.cs:12"
        static readonly Regex MessagePathRegex = new(@"(?<path>(?:Assets|Packages)/[^\s'""():,<>]+\.[A-Za-z0-9]+)(?:[:(](?<line>\d+))?", RegexOptions.Compiled);
        static readonly Regex MessageLineRegex = new(@"\bline[: ]\s*(?<line>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static readonly string ProjectRoot = Path.GetFullPath(".").Replace('\\', '/').TrimEnd('/') + "/";

        static HashSet<string> hiddenMethods;

        /// <summary>
        /// スタックトレースからファイル位置を持つフレームだけを取り出す。
        /// ファイル情報の無いフレーム (Unity 内部) と [HideInCallstack] 付きメソッドのフレームは捨てる。
        /// 自前ロガーのラッパーを飛ばしたい場合は、ロガー側に [HideInCallstack] を付けてもらう。
        /// </summary>
        public static List<(string file, int line)> ParseFrames(string stackTrace)
        {
            var frames = new List<(string file, int line)>();
            if (string.IsNullOrEmpty(stackTrace)) return frames;

            foreach (var raw in stackTrace.Split('\n'))
            {
                var match = FrameRegex.Match(raw.Trim());
                if (!match.Success) continue;

                var path = NormalizePath(match.Groups["path"].Value);
                if (path == null) continue;

                if (HiddenMethods.Contains(NormalizeMethod(match.Groups["method"].Value))) continue;

                frames.Add((path, int.Parse(match.Groups["line"].Value)));
            }

            return frames;
        }

        public static (string file, int line) Resolve(string message, string stackTrace)
        {
            var frames = ParseFrames(stackTrace);

            // 自分のコード (Assets/ と埋め込みパッケージ) を最優先
            foreach (var frame in frames)
            {
                if (IsProjectPath(frame.file)) return frame;
            }

            foreach (var frame in frames)
            {
                if (frame.file.StartsWith("Library/PackageCache/", StringComparison.Ordinal)) return frame;
            }

            if (!string.IsNullOrEmpty(message))
            {
                var match = MessagePathRegex.Match(message);
                if (match.Success)
                {
                    var line = 0;
                    if (match.Groups["line"].Success)
                    {
                        line = int.Parse(match.Groups["line"].Value);
                    }
                    else
                    {
                        var lineMatch = MessageLineRegex.Match(message);
                        if (lineMatch.Success) line = int.Parse(lineMatch.Groups["line"].Value);
                    }

                    return (match.Groups["path"].Value, line);
                }
            }

            return (null, 0);
        }

        /// <summary>プロジェクト相対パスに揃える。プロジェクト外 (Unity 本体のソース等) なら null。</summary>
        internal static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("<", StringComparison.Ordinal)) return null;

            path = path.Replace('\\', '/');
            if (path.StartsWith("./", StringComparison.Ordinal)) path = path.Substring(2);

            if (Path.IsPathRooted(path))
            {
                if (!path.StartsWith(ProjectRoot, StringComparison.OrdinalIgnoreCase)) return null;
                path = path.Substring(ProjectRoot.Length);
            }

            return path;
        }

        static bool IsProjectPath(string path) =>
            path.StartsWith("Assets/", StringComparison.Ordinal) ||
            path.StartsWith("Packages/", StringComparison.Ordinal);

        // "Ns.Outer/Inner:Method (string)" → "Ns.Outer.Inner.Method"
        static string NormalizeMethod(string frame)
        {
            var paren = frame.IndexOf(" (", StringComparison.Ordinal);
            if (paren >= 0) frame = frame.Substring(0, paren);
            return NormalizeTypeName(frame.Replace(':', '.'));
        }

        static string NormalizeTypeName(string name) => name.Replace('/', '.').Replace('+', '.');

        // [HideInCallstack] が付いたメソッド (自前ロガーのラッパー等) は自動で除外する
        static HashSet<string> HiddenMethods => hiddenMethods ??= BuildHiddenMethods();

        static HashSet<string> BuildHiddenMethods()
        {
            var set = new HashSet<string>();
            foreach (var method in TypeCache.GetMethodsWithAttribute<HideInCallstackAttribute>())
            {
                if (method.DeclaringType?.FullName == null) continue;
                set.Add(NormalizeTypeName(method.DeclaringType.FullName) + "." + method.Name);
            }

            return set;
        }
    }
}
