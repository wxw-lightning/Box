using System.Collections.Generic;
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

        // 撤销栈：每次结构性修改（绘格/调整尺寸/填充/清空）前快照 cells，撤销时还原最近一步。
        // 仅针对当前所选关卡，切换关卡时清空（见 HandleSelectionChange）。
        private const int MaxUndo = 50;
        private readonly List<UndoSnapshot> _undoStack = new List<UndoSnapshot>();

        /// <summary>一步撤销快照：记录归属关卡与修改前的 cells 副本。</summary>
        private readonly struct UndoSnapshot
        {
            public readonly LevelAsset Asset;
            public readonly CellType[,] Cells;
            public UndoSnapshot(LevelAsset asset, CellType[,] cells) { Asset = asset; Cells = cells; }
        }

        // 可解性检测（手动按钮触发，见 DrawSolverBar）。结果缓存；关卡被结构性修改或切换关卡后置 stale。
        // 搜索预算，上限保证编辑器不被复杂关卡卡死（见 LevelSolver）。
        private const int SolverMaxStates = 200000;
        private const double SolverMaxSeconds = 2.0;
        private LevelSolver.SolveResult? _solveResult;
        private bool _solverStale = true;

        [MenuItem("Tools/Sokoban/Level Editor")]
        private static void Open()
        {
            var w = GetWindow<LevelEditorWindow>("关卡编辑器");
            w.minSize = new Vector2(760, 480);
            w.Show();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
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

            // Ctrl/Cmd+Z 撤销；编辑文本框（关卡名/提示）时让文本框自行处理，不拦截。
            var hotkey = Event.current;
            if (hotkey.type == EventType.KeyDown && hotkey.keyCode == KeyCode.Z
                && (hotkey.control || hotkey.command) && !EditorGUIUtility.editingTextField)
            {
                Undo();
                hotkey.Use();
            }

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

            // 教学/提示：纯文本多行，运行时显示在关卡 UI 上。不影响左侧菜单。
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("提示", GUILayout.Width(48));
            string newHint = EditorGUILayout.TextArea(selected.hint ?? "",
                EditorStyles.textArea, GUILayout.MinHeight(48));
            if (newHint != selected.hint)
            {
                selected.hint = newHint;
                EditorUtility.SetDirty(selected);
            }
            EditorGUILayout.EndHorizontal();

            // 实时可玩性指示（取代弹窗校验 + 大段只读统计）。
            DrawPlayabilityBar(selected);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("清空场景预览", GUILayout.Width(110))) LevelPreview.Clear();
            EditorGUILayout.EndHorizontal();

            // ===== 关卡编辑（始终可见）=====
            SirenixEditorGUI.Title("关卡编辑", null, TextAlignment.Left, true, true);
            DrawBrushBar();
            DrawQuickFillBar(selected);

            SirenixEditorGUI.BeginHorizontalToolbar();
            GUILayout.Label("尺寸 W×H", GUILayout.Width(58));
            _newWidth = Mathf.Max(1, EditorGUILayout.IntField(_newWidth, GUILayout.Width(38)));
            _newHeight = Mathf.Max(1, EditorGUILayout.IntField(_newHeight, GUILayout.Width(38)));
            if (SirenixEditorGUI.ToolbarButton(new GUIContent("调整尺寸")))
            {
                PushUndo(selected);
                selected.Resize(_newWidth, _newHeight);
                MarkAndPreview(selected);
            }
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(_undoStack.Count == 0))
            {
                if (SirenixEditorGUI.ToolbarButton(new GUIContent($"撤销 Ctrl+Z ({_undoStack.Count})")))
                    Undo();
            }
            SirenixEditorGUI.EndHorizontalToolbar();

            GUILayout.Space(6);
            DrawGridSection(selected);
        }

        // ---------- 固定像素尺寸的网格自绘（不随窗口缩放） ----------

        private const float CellSize = 60f; // 单元像素尺寸；改这里调整大小

        private void DrawGridSection(LevelAsset lvl)
        {
            int W = lvl.Width, H = lvl.Height;
            var before = GetPlayerCells(lvl);
            var e = Event.current;
            bool painted = false;

            for (int row = 0; row < H; row++)
            {
                EditorGUILayout.BeginHorizontal();
                for (int col = 0; col < W; col++)
                {
                    Rect r = GUILayoutUtility.GetRect(CellSize, CellSize,
                        GUILayout.Width(CellSize), GUILayout.Height(CellSize));

                    if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag)
                        && e.button == 0 && r.Contains(e.mousePosition))
                    {
                        if (e.type == EventType.MouseDown)
                            PushUndo(lvl); // 一次落笔=一步撤销（拖动连画归为同一步）
                        lvl.cells[col, row] = LevelEditing.Brush;
                        painted = true;
                        e.Use();
                    }

                    EditorGUI.DrawRect(new Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2),
                        lvl.cells[col, row].ToColor());

                    char sym = lvl.cells[col, row].ToSymbol();
                    if (sym != ' ')
                        GUI.Label(r, sym.ToString(), CellLabelStyle);
                }
                GUILayout.EndHorizontal();
            }

            if (painted)
            {
                EnforceSinglePlayer(lvl, before);
                EditorUtility.SetDirty(lvl);
                if (_autoPreview && LevelPreview.HasPreview)
                    LevelPreview.Generate(lvl);
                Repaint();
                SceneView.RepaintAll();
            }
        }

        private static GUIStyle _cellLabelStyle;
        private static GUIStyle CellLabelStyle => _cellLabelStyle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
        };

        // ---------- 可玩性指示 / 画笔 / 快速填充 ----------

        private static readonly (CellType type, string label)[] Brushes =
        {
            (CellType.Wall, "墙"),
            (CellType.Target, "气动目标"), (CellType.TargetB, "湮灭目标"), (CellType.TargetC, "衍射目标"),
            (CellType.Box, "气动箱"), (CellType.BoxB, "湮灭箱"), (CellType.BoxC, "衍射箱"),
            (CellType.Player, "玩家"),
            (CellType.Floor, "地板/橡皮"),
        };

        private void DrawPlayabilityBar(LevelAsset lvl)
        {
            int boxesA = lvl.CountBoxesA(), targetsA = lvl.CountTargetsA();
            int boxesB = lvl.CountBoxesB(), targetsB = lvl.CountTargetsB();
            int boxesC = lvl.CountBoxesC(), targetsC = lvl.CountTargetsC();
            int players = lvl.CountPlayers();
            EditorGUILayout.LabelField(
                $"尺寸 {lvl.Width}×{lvl.Height}      气动 箱{boxesA}/目标{targetsA}   湮灭 箱{boxesB}/目标{targetsB}   衍射 箱{boxesC}/目标{targetsC}   玩家 {players}");

            string reason = (boxesA + boxesB + boxesC) == 0 ? "没有箱子"
                          : boxesA != targetsA ? "气动箱数 ≠ 目标数"
                          : boxesB != targetsB ? "湮灭箱数 ≠ 目标数"
                          : boxesC != targetsC ? "衍射箱数 ≠ 目标数"
                          : players != 1 ? "玩家数应恰为 1"
                          : null;
            EditorGUILayout.HelpBox(reason == null ? "✓ 可玩（数量配平）" : "✗ " + reason,
                reason == null ? MessageType.Info : MessageType.Error);

            // 真实可解性：数量配平只是必要条件，下面用求解器判断是否「确实有解」。
            DrawSolverBar(lvl, basicValid: reason == null);
        }

        // 可解性检测 UI：按钮触发求解，结果三态缓存显示。仅在数量/玩家预检通过时可用。
        private void DrawSolverBar(LevelAsset lvl, bool basicValid)
        {
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!basicValid))
            {
                if (GUILayout.Button("检测是否可解", GUILayout.Width(110)))
                    RunSolver(lvl);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (!basicValid)
                return; // 先修数量/玩家配平，再谈可解性

            if (_solverStale || _solveResult == null)
            {
                EditorGUILayout.HelpBox("○ 尚未检测可解性（点击「检测是否可解」）", MessageType.None);
                return;
            }

            var r = _solveResult.Value;
            switch (r.Status)
            {
                case LevelSolver.Solvability.Solvable:
                    EditorGUILayout.HelpBox($"✓ 确实有解（最少 {r.Moves} 步）", MessageType.Info);
                    break;
                case LevelSolver.Solvability.Unsolvable:
                    EditorGUILayout.HelpBox($"✗ 无解（已穷尽 {r.StatesExplored} 个状态，无可行解法）", MessageType.Error);
                    break;
                default:
                    EditorGUILayout.HelpBox(
                        $"? 无法判定：超出搜索上限（已搜 {r.StatesExplored} 状态），建议简化关卡后重试", MessageType.Warning);
                    break;
            }
        }

        private void RunSolver(LevelAsset lvl)
        {
            try
            {
                EditorUtility.DisplayProgressBar("可解性检测", "正在搜索可行解法…", 0.5f);
                _solveResult = LevelSolver.Analyze(lvl, SolverMaxStates, SolverMaxSeconds);
                _solverStale = false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
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
                PushUndo(lvl);
                lvl.cells = LevelAsset.CreateEmpty(lvl.Width, lvl.Height);
                MarkAndPreview(lvl);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void FillAll(LevelAsset lvl, CellType t)
        {
            PushUndo(lvl);
            for (int x = 0; x < lvl.Width; x++)
                for (int y = 0; y < lvl.Height; y++)
                    lvl.cells[x, y] = t;
            MarkAndPreview(lvl);
        }

        private void AddBorder(LevelAsset lvl)
        {
            PushUndo(lvl);
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

        // ---------- 撤销 ----------

        /// <summary>在修改 <paramref name="lvl"/> 之前调用：把当前 cells 快照压栈。</summary>
        private void PushUndo(LevelAsset lvl)
        {
            if (lvl == null || lvl.cells == null) return;
            _undoStack.Add(new UndoSnapshot(lvl, (CellType[,])lvl.cells.Clone()));
            if (_undoStack.Count > MaxUndo)
                _undoStack.RemoveAt(0); // 超出上限丢弃最旧一步
            _solverStale = true; // 任何结构性修改都使已缓存的可解性结果失效
        }

        /// <summary>还原最近一步修改。</summary>
        private void Undo()
        {
            if (_undoStack.Count == 0) return;
            var snap = _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            if (snap.Asset == null) return; // 关卡已被删除
            snap.Asset.cells = snap.Cells;
            EditorUtility.SetDirty(snap.Asset);
            if (_autoPreview && LevelPreview.HasPreview)
                LevelPreview.Generate(snap.Asset);
            Repaint();
            SceneView.RepaintAll();
        }

        // ---------- 维持「至多一个玩家」 ----------

        private List<Vector2Int> GetPlayerCells(LevelAsset lvl)
        {
            var list = new List<Vector2Int>();
            for (int x = 0; x < lvl.Width; x++)
                for (int y = 0; y < lvl.Height; y++)
                    if (lvl.cells[x, y].HasPlayer())
                        list.Add(new Vector2Int(x, y));
            return list;
        }

        private void EnforceSinglePlayer(LevelAsset lvl, List<Vector2Int> before)
        {
            var current = GetPlayerCells(lvl);
            if (current.Count <= 1) return;

            // 优先保留「本次新画的」玩家（涂改前不存在的那个）；找不到则保留最后一个。
            int keepIdx = current.FindIndex(c => !before.Contains(c));
            if (keepIdx < 0) keepIdx = current.Count - 1;

            for (int i = 0; i < current.Count; i++)
            {
                if (i == keepIdx) continue;
                var c = current[i];
                // 清除该处玩家：在目标上则回到对应属性目标（保留绿/紫），否则回到地板。
                var here = lvl.cells[c.x, c.y];
                lvl.cells[c.x, c.y] = here.IsTargetB() ? CellType.TargetB
                                    : here.IsTargetA() ? CellType.Target
                                    : CellType.Floor;
            }
        }

        // ---------- 选择变化 / 自动预览 ----------

        private void HandleSelectionChange(LevelAsset selected)
        {
            if (selected == _lastSelected) return;
            _lastSelected = selected;
            _undoStack.Clear(); // 撤销历史按关卡隔离
            _solveResult = null; // 可解性结果按关卡隔离
            _solverStale = true;
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
            copy.hint = src.hint;
            EditorUtility.SetDirty(copy);
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
