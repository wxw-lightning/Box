using System.IO;
using Sirenix.OdinInspector.Editor;
using Sirenix.Utilities.Editor;
using UnityEditor;
using UnityEngine;

namespace Sokoban.Editor
{
    /// <summary>
    /// 关卡编辑器（Tools/Sokoban/Level Editor），布局参考目标设计：
    /// 左侧关卡列表；右侧「自动生成预览」开关、关卡信息(资源名只读+定位、关卡名)、
    /// 实时可玩性指示、清空场景预览、进入关卡编辑(彩色画笔+快速填充+网格)。
    /// 每关是独立 <see cref="LevelAsset"/> 资产，顺序由 <see cref="LevelDatabase"/> 维护。
    /// </summary>
    public class LevelEditorWindow : OdinMenuEditorWindow
    {
        private const string DbPath = "Assets/GameData/LevelDatabase.asset";
        private const string LevelFolder = "Assets/Levels";

        private LevelDatabase _db;
        private CellType _brush = CellType.Wall;
        private int _newWidth = 8;
        private int _newHeight = 7;

        private bool _autoPreview = true;
        private LevelAsset _lastSelected;

        [MenuItem("Tools/Sokoban/Level Editor")]
        private static void Open()
        {
            var w = GetWindow<LevelEditorWindow>("关卡编辑器");
            w.minSize = new Vector2(760, 480);
            w.Show();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            LevelEditing.ShowGrid = false;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            LevelEditing.ShowGrid = false;
            LevelPreview.Clear();
        }

        protected override OdinMenuTree BuildMenuTree()
        {
            EnsureDatabase();
            var tree = new OdinMenuTree(supportsMultiSelect: false);
            tree.Config.DrawSearchToolbar = true;

            if (_db != null)
            {
                for (int i = 0; i < _db.levels.Count; i++)
                {
                    var lvl = _db.levels[i];
                    string title = lvl != null ? $"Level{i} ({lvl.levelName})" : $"Level{i} (缺失)";
                    tree.Add(title, lvl);
                }
            }
            return tree;
        }

        protected override void OnBeginDrawEditors()
        {
            var selected = MenuTree?.Selection?.SelectedValue as LevelAsset;
            HandleSelectionChange(selected);

            // 顶部：自动预览开关 + 列表级操作。
            SirenixEditorGUI.BeginHorizontalToolbar();
            _autoPreview = GUILayout.Toggle(_autoPreview, " 自动生成预览", "Button", GUILayout.Width(110));
            GUILayout.Space(8);
            if (_db == null)
            {
                GUILayout.Label("未找到 LevelDatabase");
                if (SirenixEditorGUI.ToolbarButton(new GUIContent("创建数据库(含示例)")))
                    CreateDatabaseWithSamples();
            }
            else
            {
                if (SirenixEditorGUI.ToolbarButton(new GUIContent("新建关卡"))) NewLevel();
                if (_db.levels.Count == 0 && SirenixEditorGUI.ToolbarButton(new GUIContent("补种示例")))
                    SeedSamples();
                if (selected != null)
                {
                    if (SirenixEditorGUI.ToolbarButton(new GUIContent("复制"))) Duplicate(selected);
                    if (SirenixEditorGUI.ToolbarButton(new GUIContent("删除"))) Delete(selected);
                    if (SirenixEditorGUI.ToolbarButton(new GUIContent("上移"))) Move(selected, -1);
                    if (SirenixEditorGUI.ToolbarButton(new GUIContent("下移"))) Move(selected, +1);
                }
            }
            GUILayout.FlexibleSpace();
            SirenixEditorGUI.EndHorizontalToolbar();

            if (selected == null)
                return;

            // ===== 关卡信息 =====
            SirenixEditorGUI.Title("关卡信息", null, TextAlignment.Left, true, true);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("资源名", GUILayout.Width(48));
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField(selected.name); // 文件名只读
            if (GUILayout.Button("定位", GUILayout.Width(60))) Ping(selected);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("关卡名", GUILayout.Width(48));
            // Delayed：失焦/回车才提交，避免每键重建菜单导致丢失输入焦点。
            string newName = EditorGUILayout.DelayedTextField(selected.levelName);
            if (newName != selected.levelName)
            {
                selected.levelName = newName;
                EditorUtility.SetDirty(selected);
                ForceMenuTreeRebuild(); // 刷新左侧标签
            }
            EditorGUILayout.EndHorizontal();

            // 实时可玩性指示（取代弹窗校验 + 大段只读统计）。
            DrawPlayabilityBar(selected);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("清空场景预览", GUILayout.Width(110))) LevelPreview.Clear();
            EditorGUILayout.EndHorizontal();

            // ===== 关卡编辑 =====
            SirenixEditorGUI.Title("关卡编辑", null, TextAlignment.Left, true, true);
            if (GUILayout.Button(LevelEditing.ShowGrid ? "退出关卡编辑" : "进入关卡编辑", GUILayout.Height(26)))
                LevelEditing.ShowGrid = !LevelEditing.ShowGrid;

            if (LevelEditing.ShowGrid)
            {
                DrawBrushBar();
                DrawQuickFillBar(selected);

                SirenixEditorGUI.BeginHorizontalToolbar();
                GUILayout.Label("尺寸 W×H", GUILayout.Width(58));
                _newWidth = Mathf.Max(1, EditorGUILayout.IntField(_newWidth, GUILayout.Width(38)));
                _newHeight = Mathf.Max(1, EditorGUILayout.IntField(_newHeight, GUILayout.Width(38)));
                if (SirenixEditorGUI.ToolbarButton(new GUIContent("调整尺寸")))
                {
                    selected.Resize(_newWidth, _newHeight);
                    MarkAndPreview(selected);
                }
                GUILayout.FlexibleSpace();
                SirenixEditorGUI.EndHorizontalToolbar();
                // 网格本体由下方默认 Inspector（cells 的 TableMatrix，受 ShowGrid 控制）绘制。
            }
        }

        // ---------- 可玩性指示 / 画笔 / 快速填充 ----------

        private static readonly (CellType type, string label)[] Brushes =
        {
            (CellType.Wall, "墙"), (CellType.Target, "目标"),
            (CellType.Box, "箱子"), (CellType.Player, "玩家"),
            (CellType.Floor, "地板/橡皮"),
        };

        private void DrawPlayabilityBar(LevelAsset lvl)
        {
            int boxes = lvl.CountBoxes(), targets = lvl.CountTargets(), players = lvl.CountPlayers();
            EditorGUILayout.LabelField($"尺寸 {lvl.Width}×{lvl.Height}      箱子 {boxes} / 目标 {targets} / 玩家 {players}");

            string reason = boxes == 0 ? "没有箱子"
                          : boxes != targets ? "箱数 ≠ 目标数"
                          : players != 1 ? "玩家数应恰为 1"
                          : null;
            EditorGUILayout.HelpBox(reason == null ? "✓ 可玩" : "✗ " + reason,
                reason == null ? MessageType.Info : MessageType.Error);
        }

        private void DrawBrushBar()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("画笔", GUILayout.Width(32));
            var prev = GUI.backgroundColor;
            foreach (var (type, label) in Brushes)
            {
                GUI.backgroundColor = type.ToColor();
                bool active = _brush == type;
                bool now = GUILayout.Toggle(active, label, "Button", GUILayout.Width(64), GUILayout.Height(22));
                if (now && !active) _brush = type;
            }
            GUI.backgroundColor = prev;
            LevelEditing.Brush = _brush;
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawQuickFillBar(LevelAsset lvl)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("快速填充", GUILayout.Width(56));
            if (GUILayout.Button("全地板", GUILayout.Width(70))) FillAll(lvl, CellType.Floor);
            if (GUILayout.Button("四周加墙", GUILayout.Width(80))) AddBorder(lvl);
            if (GUILayout.Button("清空", GUILayout.Width(70)))
            {
                lvl.cells = LevelAsset.CreateEmpty(lvl.Width, lvl.Height);
                MarkAndPreview(lvl);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void FillAll(LevelAsset lvl, CellType t)
        {
            for (int x = 0; x < lvl.Width; x++)
                for (int y = 0; y < lvl.Height; y++)
                    lvl.cells[x, y] = t;
            MarkAndPreview(lvl);
        }

        private void AddBorder(LevelAsset lvl)
        {
            int W = lvl.Width, H = lvl.Height;
            for (int x = 0; x < W; x++) { lvl.cells[x, 0] = CellType.Wall; lvl.cells[x, H - 1] = CellType.Wall; }
            for (int y = 0; y < H; y++) { lvl.cells[0, y] = CellType.Wall; lvl.cells[W - 1, y] = CellType.Wall; }
            MarkAndPreview(lvl);
        }

        private void MarkAndPreview(LevelAsset lvl)
        {
            EditorUtility.SetDirty(lvl);
            if (_autoPreview && LevelPreview.HasPreview)
                LevelPreview.Generate(lvl);
        }

        protected override void OnEndDrawEditors()
        {
            // 网格涂改后标脏并（按需）刷新预览。
            if (GUI.changed && _db != null)
            {
                var selected = MenuTree?.Selection?.SelectedValue as LevelAsset;
                if (selected != null)
                {
                    EditorUtility.SetDirty(selected);
                    if (_autoPreview && LevelPreview.HasPreview)
                        LevelPreview.Generate(selected);
                }
            }
        }

        // ---------- 选择变化 / 自动预览 ----------

        private void HandleSelectionChange(LevelAsset selected)
        {
            if (selected == _lastSelected) return;
            _lastSelected = selected;
            if (_autoPreview && selected != null)
            {
                LevelPreview.Generate(selected);
                SceneView.RepaintAll();
            }
        }

        // ---------- 数据库 / 关卡资产 CRUD ----------

        private void EnsureDatabase()
        {
            if (_db != null) return;
            var guids = AssetDatabase.FindAssets("t:LevelDatabase");
            if (guids.Length > 0)
                _db = AssetDatabase.LoadAssetAtPath<LevelDatabase>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        private void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            var leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        private LevelAsset CreateLevelAsset(string desiredName, CellType[,] cells, string levelName)
        {
            EnsureFolder(LevelFolder);
            var asset = ScriptableObject.CreateInstance<LevelAsset>();
            asset.cells = cells;
            asset.levelName = levelName;
            string path = AssetDatabase.GenerateUniqueAssetPath($"{LevelFolder}/{desiredName}.asset");
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private void NewLevel()
        {
            var asset = CreateLevelAsset($"Level{_db.levels.Count}",
                LevelAsset.CreateEmpty(_newWidth, _newHeight), $"关卡{_db.levels.Count}");
            _db.levels.Add(asset);
            EditorUtility.SetDirty(_db);
            AssetDatabase.SaveAssets();
            ForceMenuTreeRebuild();
        }

        private void Duplicate(LevelAsset src)
        {
            int i = _db.levels.IndexOf(src);
            var copy = CreateLevelAsset(src.name + "_Copy",
                (CellType[,])src.cells.Clone(), src.levelName + " Copy");
            _db.levels.Insert(i + 1, copy);
            EditorUtility.SetDirty(_db);
            AssetDatabase.SaveAssets();
            ForceMenuTreeRebuild();
        }

        private void Delete(LevelAsset asset)
        {
            // 0=移除并删文件, 1=取消, 2=仅从列表移除
            int choice = EditorUtility.DisplayDialogComplex(
                "删除关卡", $"从列表移除「{asset.levelName}」。是否同时删除资产文件？",
                "移除并删除文件", "取消", "仅从列表移除");
            if (choice == 1) return;
            bool alsoFile = choice == 0;

            _db.levels.Remove(asset);
            if (alsoFile)
                AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(asset));
            EditorUtility.SetDirty(_db);
            AssetDatabase.SaveAssets();
            ForceMenuTreeRebuild();
        }

        private void Move(LevelAsset asset, int delta)
        {
            int i = _db.levels.IndexOf(asset);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= _db.levels.Count) return;
            (_db.levels[i], _db.levels[j]) = (_db.levels[j], _db.levels[i]);
            EditorUtility.SetDirty(_db);
            ForceMenuTreeRebuild();
        }

        private void Ping(LevelAsset asset)
        {
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        // ---------- 创建数据库 + 示例关 ----------

        private void CreateDatabaseWithSamples()
        {
            EnsureFolder(Path.GetDirectoryName(DbPath).Replace('\\', '/'));
            var db = ScriptableObject.CreateInstance<LevelDatabase>();
            AssetDatabase.CreateAsset(db, DbPath);
            _db = db;
            SeedSamples();
        }

        private void SeedSamples()
        {
            _db.levels.Add(CreateLevelAsset($"Level{_db.levels.Count}", BuildSampleA(), $"关卡{_db.levels.Count}"));
            _db.levels.Add(CreateLevelAsset($"Level{_db.levels.Count}", BuildSampleB(), $"关卡{_db.levels.Count}"));
            EditorUtility.SetDirty(_db);
            AssetDatabase.SaveAssets();
            ForceMenuTreeRebuild();
        }

        private static CellType[,] BuildSampleA()
        {
            var c = LevelAsset.CreateEmpty(8, 7);
            c[1, 1] = CellType.Player;
            c[2, 2] = CellType.Box; c[2, 3] = CellType.Box; c[2, 4] = CellType.Box;
            c[5, 2] = CellType.Target; c[5, 3] = CellType.Target; c[5, 4] = CellType.Target;
            return c;
        }

        private static CellType[,] BuildSampleB()
        {
            var c = LevelAsset.CreateEmpty(6, 5);
            c[1, 1] = CellType.Player;
            c[3, 1] = CellType.Box; c[3, 2] = CellType.Box;
            c[4, 1] = CellType.Target; c[4, 2] = CellType.Target;
            return c;
        }
    }
}
