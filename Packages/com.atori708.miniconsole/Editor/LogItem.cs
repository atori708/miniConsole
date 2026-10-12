using System;
using UnityEngine;

namespace MiniConsole
{
    internal enum LogSeverity
    {
        Warning,
        Error,

        // 既存ログはドメインリロードを跨いで int で保持されるので、値がずれないよう末尾に追加する
        Info,
    }

    /// <summary>表示する LogSeverity の集合。ビット位置は LogSeverity の値に対応する。</summary>
    [Flags]
    internal enum SeverityMask : byte
    {
        None = 0,
        Warning = 1 << (int)LogSeverity.Warning,
        Error = 1 << (int)LogSeverity.Error,
        Info = 1 << (int)LogSeverity.Info,
        All = Warning | Error | Info,
    }

    internal static class SeverityMaskExtensions
    {
        public static SeverityMask ToMask(this LogSeverity severity) => (SeverityMask)(1 << (int)severity);
    }

    internal enum LogOrigin
    {
        Log,
        Exception,
        Compiler,
    }

    [Serializable]
    internal sealed class LogItem
    {
        public LogSeverity severity;
        public LogOrigin origin;
        public string message;
        public string stackTrace;
        public string file;
        public int line;

        /// <summary>"[HH:mm:ss]" 固定 10 文字。受け取り時に一度だけ作り、表示時は使い回す。</summary>
        public string timestamp;

        /// <summary>file / line が計算済みか。スタックトレースの解析は必要になるまで遅延させる。</summary>
        public bool resolved;

        // Collapse で同一ログとみなすためのキー。LogStore.Add で計算する
        // (旧バージョンで保存されたログは未計算なので、初回アクセス時に計算する)
        [SerializeField] Hash128 collapseKey;

        // message と重複する内容なので保存しない (ドメインリロード後は次回表示時に作り直す)
        [NonSerialized] string firstLine;

        /// <summary>
        /// (severity, message, stackTrace) のハッシュ。Collapse 再構築のたびに約 1KB のスタックトレースを
        /// ハッシュ・比較し直すと重いので、一度だけ計算して保持する。
        /// コンパイルエラーはスタックトレースが無いので、代わりに file:line を使う。
        /// </summary>
        public Hash128 CollapseKey
        {
            get
            {
                if (!collapseKey.isValid) collapseKey = ComputeCollapseKey();
                return collapseKey;
            }
        }

        /// <summary>message の 1 行目。初回アクセス時に一度だけ切り出し、以降は使い回す。</summary>
        public string FirstLine => firstLine ??= ExtractFirstLine(message);

        Hash128 ComputeCollapseKey()
        {
            var hash = new Hash128();
            hash.Append((int)severity);

            // 文字列は連結して流し込まれるので、長さも入れて "ab"+"c" と "a"+"bc" を区別する
            AppendString(ref hash, message);
            if (origin == LogOrigin.Compiler)
            {
                AppendString(ref hash, file);
                hash.Append(line);
            }
            else
            {
                AppendString(ref hash, stackTrace);
            }

            return hash;
        }

        static void AppendString(ref Hash128 hash, string text)
        {
            text ??= string.Empty;
            hash.Append(text.Length);
            hash.Append(text);
        }

        static string ExtractFirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var newline = text.IndexOf('\n');
            if (newline < 0) return text;
            return text.Substring(0, newline > 0 && text[newline - 1] == '\r' ? newline - 1 : newline);
        }
    }
}
