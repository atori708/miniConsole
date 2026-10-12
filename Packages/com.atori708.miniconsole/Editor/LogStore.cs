using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MiniConsole
{
    /// <summary>
    /// 収集したログの保持先。ScriptableSingleton なのでドメインリロードを跨いで残る。
    /// </summary>
    internal sealed class LogStore : ScriptableSingleton<LogStore>
    {
        const int MaxItems = 50000;

        // 上限を超えたときにまとめて捨てる件数。1件ずつ捨てると追加のたびに Generation が変わり、
        // ウィンドウの差分更新が効かず毎回全件作り直しになるため、まとめて捨てて頻度を下げる。
        const int TrimCount = MaxItems / 10;

        [SerializeField] List<LogItem> items = new();

        internal static event Action Changed;

        public IReadOnlyList<LogItem> Items => items;

        /// <summary>
        /// 末尾への追加以外の変更（削除）があるたびに増える。
        /// 値が変わっていなければ、前回見た件数以降が新しく追加された分だと判断できる。
        /// </summary>
        public int Generation { get; private set; }

        public void Add(LogItem item)
        {
            // Collapse 再構築のたびに計算しないよう、受け取り時に一度だけ計算しておく
            _ = item.CollapseKey;
            items.Add(item);
            if (items.Count > MaxItems) TrimOldest();
        }

        /// <summary>
        /// コンパイルエラーは古くても残したいので、それ以外の最古のものから TrimCount 件を捨てる。
        /// 残すものを前に詰める 1 パスで行い、RemoveAt の繰り返しによる配列シフトを避ける。
        /// </summary>
        void TrimOldest()
        {
            var write = 0;
            var removed = 0;
            for (var read = 0; read < items.Count; read++)
            {
                var current = items[read];
                if (removed < TrimCount && current.origin != LogOrigin.Compiler)
                {
                    removed++;
                    continue;
                }

                items[write++] = current;
            }

            items.RemoveRange(write, items.Count - write);

            // 全部コンパイルエラーで捨てられなかった場合も上限は守る
            if (removed == 0) items.RemoveRange(0, Math.Min(TrimCount, items.Count));

            Generation++;
        }

        public void RemoveWhere(Predicate<LogItem> match)
        {
            if (items.RemoveAll(match) > 0) Generation++;
        }

        /// <summary>標準 Console と同様に、コンパイルエラー以外を消す。</summary>
        public void ClearLogs()
        {
            RemoveWhere(i => i.origin != LogOrigin.Compiler);
            NotifyChanged();
        }

        public static void NotifyChanged() => Changed?.Invoke();
    }
}
