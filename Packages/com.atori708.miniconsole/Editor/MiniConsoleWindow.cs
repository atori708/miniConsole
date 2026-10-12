using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditorInternal;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace MiniConsole
{
    internal sealed class MiniConsoleWindow : EditorWindow, IHasCustomMenu
    {
        const string ShowTimestampClass = "mc-list--show-timestamp";
        const string UxmlFileName = "MiniConsoleWindow.uxml";
        const string IconClass = "mc-icon";
        const string ErrorIconClass = "mc-icon--error";
        const string WarningIconClass = "mc-icon--warning";
        const string InfoIconClass = "mc-icon--info";
        const string HiddenClass = "mc-hidden";

        sealed class Row
        {
            public LogItem item;
            public int count;
        }

        sealed class RowView
        {
            public Row row;
            public VisualElement icon;
            public Label timestamp;
            public Label message;
            public Label count;
        }

        readonly List<Row> rows = new();
        readonly Dictionary<UnityEngine.Hash128, Row> groups = new();
        readonly List<(string file, int line)> detailLinks = new();
        ListView logList;
        Label detailLabel;
        LogItem shownItem;
        bool shownFullPath;
        ToolbarToggle errorToggle;
        ToolbarToggle warningToggle;
        ToolbarToggle logToggle;
        string searchText = string.Empty;
        bool dirty = true;

        // 差分更新用: LogStore.Items のどこまでを rows に反映済みか
        bool needsRebuild = true;
        int appliedGeneration;
        int appliedCount;
        int errorCount;
        int warningCount;
        int logCount;

        [MenuItem("Window/General/miniConsole")]
        static void Open()
        {
            GetWindow<MiniConsoleWindow>("miniConsole");
        }

        void OnEnable() => LogStore.Changed += MarkDirty;
        void OnDisable() => LogStore.Changed -= MarkDirty;
        void MarkDirty() => dirty = true;

        void CreateGUI()
        {
            var scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
            var uxmlPath = Path.Combine(Path.GetDirectoryName(scriptPath) ?? string.Empty, UxmlFileName).Replace('\\', '/');
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (tree == null)
            {
                rootVisualElement.Add(new Label($"UXML not found: {uxmlPath}"));
                return;
            }

            tree.CloneTree(rootVisualElement);
            var root = rootVisualElement;
            var settings = ConsoleSettings.instance;

            root.Q<ToolbarButton>("clearButton").clicked += () => LogStore.instance.ClearLogs();
            BindToggle(root.Q<ToolbarToggle>("collapseToggle"), settings.collapse, v => settings.collapse = v, true);
            var clearMenu = root.Q<ToolbarMenu>("clearMenu");
            AddClearOption(clearMenu, "Clear on Play", () => settings.clearOnPlay, v => settings.clearOnPlay = v);
            AddClearOption(clearMenu, "Clear on Build", () => settings.clearOnBuild, v => settings.clearOnBuild = v);
            AddClearOption(clearMenu, "Clear on Recompile", () => settings.clearOnRecompile, v => settings.clearOnRecompile = v);

            errorToggle = root.Q<ToolbarToggle>("errorToggle");
            warningToggle = root.Q<ToolbarToggle>("warningToggle");
            logToggle = root.Q<ToolbarToggle>("logToggle");
            BindSeverityToggle(logToggle, SeverityMask.Info, InfoIconClass);
            BindSeverityToggle(warningToggle, SeverityMask.Warning, WarningIconClass);
            BindSeverityToggle(errorToggle, SeverityMask.Error, ErrorIconClass);

            root.Q<ToolbarSearchField>("searchField").RegisterValueChangedCallback(e =>
            {
                searchText = e.newValue ?? string.Empty;
                needsRebuild = true;
                dirty = true;
            });

            detailLabel = root.Q<Label>("detailLabel");
            detailLabel.enableRichText = true;
            detailLabel.selection.isSelectable = true;
            detailLabel.RegisterCallback<PointerUpLinkTagEvent>(e =>
            {
                if (!int.TryParse(e.linkID, out var index) || index < 0 || index >= detailLinks.Count) return;
                OpenLocation(detailLinks[index].file, detailLinks[index].line);
            });

            logList = root.Q<ListView>("logList");
            logList.EnableInClassList(ShowTimestampClass, settings.showTimestamp);
            logList.itemsSource = rows;
            logList.makeItem = MakeRow;
            logList.bindItem = BindRow;
            logList.itemsChosen += chosen =>
            {
                if (chosen.FirstOrDefault() is Row row) OpenLocation(row.item);
            };
            logList.selectionChanged += selected => ShowDetail(selected.FirstOrDefault() as Row);
            logList.RegisterCallback<ValidateCommandEvent>(e =>
            {
                if (e.commandName == "Copy" && logList.selectedItem != null) e.StopPropagation();
            });
            logList.RegisterCallback<ExecuteCommandEvent>(e =>
            {
                if (e.commandName != "Copy" || logList.selectedItem is not Row row) return;
                Copy(new[] { row });
                e.StopPropagation();
            });

            needsRebuild = true;
            dirty = true;
            root.schedule.Execute(() =>
            {
                if (dirty) Refresh();
            }).Every(100);
        }

        void BindToggle(ToolbarToggle toggle, bool initial, Action<bool> setter, bool affectsRows)
        {
            toggle.SetValueWithoutNotify(initial);
            toggle.RegisterValueChangedCallback(e =>
            {
                setter(e.newValue);
                ConsoleSettings.instance.SaveSettings();
                if (affectsRows) needsRebuild = true;
                dirty = true;
            });
        }

        // ウィンドウ右上の ⋮ メニュー
        public void AddItemsToMenu(GenericMenu menu)
        {
            var settings = ConsoleSettings.instance;
            menu.AddItem(new UnityEngine.GUIContent("Open Editor Log"), false, InternalEditorUtility.OpenEditorConsole);
            menu.AddItem(new UnityEngine.GUIContent("Show Timestamp"), settings.showTimestamp, () =>
            {
                settings.showTimestamp = !settings.showTimestamp;
                settings.SaveSettings();
                logList?.EnableInClassList(ShowTimestampClass, settings.showTimestamp);
            });
            menu.AddItem(new UnityEngine.GUIContent("Show File Full Path"), settings.fullPath, () =>
            {
                settings.fullPath = !settings.fullPath;
                settings.SaveSettings();
                // 詳細欄のリンク表記だけが変わる。Refresh 経由で ShowDetail が描き直す
                dirty = true;
            });
        }

        // Unity の Console と同じく、Clear 横の ▾ メニューにチェック付きで並べる
        static void AddClearOption(ToolbarMenu menu, string name, Func<bool> getter, Action<bool> setter)
        {
            menu.menu.AppendAction(name, _ =>
                {
                    setter(!getter());
                    ConsoleSettings.instance.SaveSettings();
                },
                _ => getter() ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
        }

        void BindSeverityToggle(ToolbarToggle toggle, SeverityMask flag, string iconClass)
        {
            var settings = ConsoleSettings.instance;
            BindToggle(toggle, (settings.visibleSeverities & flag) != 0, v =>
                settings.visibleSeverities = v ? settings.visibleSeverities | flag : settings.visibleSeverities & ~flag, true);
            AddSeverityIcon(toggle, iconClass);
        }

        // ListView の行と同じ丸アイコンを、トグルのラベル(件数)の手前に差し込む
        static void AddSeverityIcon(ToolbarToggle toggle, string severityClass)
        {
            var icon = new VisualElement();
            icon.AddToClassList(IconClass);
            icon.AddToClassList(severityClass);
            icon.AddToClassList("mc-severity-toggle__icon");

            var input = toggle.Q(className: Toggle.inputUssClassName) ?? toggle;
            input.Insert(0, icon);
        }

        VisualElement MakeRow()
        {
            var element = new VisualElement();
            element.AddToClassList("mc-row");

            var view = new RowView
            {
                icon = new VisualElement(),
                timestamp = new Label { enableRichText = false },
                message = new Label { enableRichText = false },
                count = new Label(),
            };
            view.icon.AddToClassList(IconClass);
            view.icon.AddToClassList("mc-row__icon");
            view.timestamp.AddToClassList("mc-row__timestamp");
            view.message.AddToClassList("mc-row__message");
            view.count.AddToClassList("mc-row__count");

            element.Add(view.icon);
            element.Add(view.timestamp);
            element.Add(view.message);
            element.Add(view.count);
            element.userData = view;

            element.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                if (view.row == null) return;
                var item = view.row.item;
                LogCollector.EnsureResolved(item);
                evt.menu.AppendAction("Open", _ => OpenLocation(item),
                    string.IsNullOrEmpty(item.file) ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
                evt.menu.AppendAction("Copy", _ => Copy(new[] { view.row }));
                evt.menu.AppendAction("Copy All Visible", _ => Copy(rows));
            }));

            return element;
        }

        void BindRow(VisualElement element, int index)
        {
            var view = (RowView)element.userData;
            var row = rows[index];
            var item = row.item;
            view.row = row;

            view.icon.EnableInClassList(ErrorIconClass, item.severity == LogSeverity.Error);
            view.icon.EnableInClassList(WarningIconClass, item.severity == LogSeverity.Warning);
            view.icon.EnableInClassList(InfoIconClass, item.severity == LogSeverity.Info);

            view.timestamp.text = item.timestamp;
            view.message.text = item.FirstLine;
            view.count.text = row.count.ToString();
            view.count.EnableInClassList(HiddenClass, row.count <= 1);
        }

        /// <summary>
        /// 通常は前回以降に追加されたログだけを rows に足す。
        /// 削除・フィルタ変更・Collapse 切り替えがあったときだけ全件作り直す。
        /// </summary>
        void Refresh()
        {
            dirty = false;
            if (logList == null) return;

            var store = LogStore.instance;
            var items = store.Items;
            var rebuild = needsRebuild || store.Generation != appliedGeneration || items.Count < appliedCount;
            if (!rebuild && items.Count == appliedCount)
            {
                ShowDetail(logList.selectedItem as Row);
                return;
            }

            var scroller = logList.Q<ScrollView>()?.verticalScroller;
            var stickToBottom = scroller == null || scroller.value >= scroller.highValue - 1f;
            var selected = (logList.selectedItem as Row)?.item;

            if (rebuild)
            {
                rows.Clear();
                groups.Clear();
                errorCount = 0;
                warningCount = 0;
                logCount = 0;
                appliedCount = 0;
                appliedGeneration = store.Generation;
                needsRebuild = false;
            }

            var settings = ConsoleSettings.instance;
            for (var i = appliedCount; i < items.Count; i++)
            {
                Append(items[i], settings);
            }

            appliedCount = items.Count;

            errorToggle.text = FormatToolbarCount(errorCount);
            warningToggle.text = FormatToolbarCount(warningCount);
            logToggle.text = FormatToolbarCount(logCount);
            logList.RefreshItems();

            // 差分追加なら既存行の index は変わらないので、選択の付け直しは作り直したときだけでよい
            if (rebuild)
            {
                var index = selected == null
                    ? -1
                    : rows.FindIndex(r => r.item == selected || (settings.collapse && r.item.CollapseKey == selected.CollapseKey));
                logList.SetSelectionWithoutNotify(index >= 0 ? new[] { index } : Array.Empty<int>());
            }

            ShowDetail(logList.selectedItem as Row);

            if (stickToBottom && rows.Count > 0)
            {
                logList.schedule.Execute(() => logList.ScrollToItem(-1));
            }
        }

        static string FormatToolbarCount(int count) => count > 999 ? "999+" : count.ToString();

        void Append(LogItem item, ConsoleSettings settings)
        {
            switch (item.severity)
            {
                case LogSeverity.Error: errorCount++; break;
                case LogSeverity.Warning: warningCount++; break;
                case LogSeverity.Info: logCount++; break;
            }

            if (!IsVisible(item, settings)) return;

            if (!settings.collapse)
            {
                rows.Add(new Row { item = item, count = 1 });
                return;
            }

            var key = item.CollapseKey;
            if (groups.TryGetValue(key, out var existing))
            {
                existing.count++;
                return;
            }

            existing = new Row { item = item, count = 1 };
            groups.Add(key, existing);
            rows.Add(existing);
        }

        bool IsVisible(LogItem item, ConsoleSettings settings)
        {
            if ((settings.visibleSeverities & item.severity.ToMask()) == 0) return false;
            if (searchText.Length == 0) return true;

            return (item.message?.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }

        /// <summary>
        /// メッセージ全文 + 「ファイル名:行番号」だけに削ったスタックトレース（各行はクリックで開けるリンク）。
        /// </summary>
        void ShowDetail(Row row)
        {
            if (detailLabel == null) return;

            var settings = ConsoleSettings.instance;
            var item = row?.item;
            if (item == shownItem && settings.fullPath == shownFullPath) return;
            shownItem = item;
            shownFullPath = settings.fullPath;
            detailLinks.Clear();

            if (item == null)
            {
                detailLabel.text = string.Empty;
                return;
            }

            var frames = LogLocationResolver.ParseFrames(item.stackTrace);
            if (frames.Count == 0)
            {
                LogCollector.EnsureResolved(item);
                if (!string.IsNullOrEmpty(item.file)) frames.Add((item.file, item.line));
            }

            var linkColor = EditorGUIUtility.isProSkin ? "#4C9AFF" : "#0B5CD5";
            var builder = new StringBuilder();
            builder.Append("<noparse>").Append((item.message ?? string.Empty).TrimEnd()).Append("</noparse>");
            if (frames.Count > 0) builder.Append('\n');

            foreach (var (file, line) in frames)
            {
                builder.Append('\n')
                    .Append("<link=\"").Append(detailLinks.Count).Append("\">")
                    .Append("<color=").Append(linkColor).Append("><u>")
                    .Append(FormatLocation(file, line, settings.fullPath))
                    .Append("</u></color></link>");
                detailLinks.Add((file, line));
            }

            detailLabel.text = builder.ToString();
        }

        static void Copy(IEnumerable<Row> targets)
        {
            var showTimestamp = ConsoleSettings.instance.showTimestamp;
            var builder = new StringBuilder();
            foreach (var row in targets)
            {
                var item = row.item;
                LogCollector.EnsureResolved(item);
                if (showTimestamp && item.timestamp != null) builder.Append(item.timestamp).Append(' ');
                builder.Append(item.severity).Append(": ").Append(item.FirstLine);
                var location = FormatLocation(item.file, item.line, true);
                if (location.Length > 0) builder.Append(" (").Append(location).Append(')');
                if (row.count > 1) builder.Append(" x").Append(row.count);
                builder.AppendLine();
            }

            EditorGUIUtility.systemCopyBuffer = builder.ToString();
        }

        static void OpenLocation(LogItem item)
        {
            LogCollector.EnsureResolved(item);
            OpenLocation(item.file, item.line);
        }

        static void OpenLocation(string file, int line)
        {
            if (string.IsNullOrEmpty(file)) return;

            if (line <= 0) line = -1;
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(file);
            if (asset != null)
            {
                AssetDatabase.OpenAsset(asset, line);
                return;
            }

            // Library/PackageCache 配下など AssetDatabase で引けないファイル
            var fullPath = Path.GetFullPath(file);
            if (File.Exists(fullPath)) InternalEditorUtility.OpenFileAtLineExternal(fullPath, line);
        }

        static string FormatLocation(string file, int line, bool fullPath)
        {
            if (string.IsNullOrEmpty(file)) return string.Empty;
            var name = fullPath ? file : Path.GetFileName(file);
            return line > 0 ? $"{name}:{line}" : name;
        }
    }
}
