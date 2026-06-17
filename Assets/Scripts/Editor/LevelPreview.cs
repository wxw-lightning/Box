using System.Collections.Generic;
using UnityEngine;

namespace Sokoban.Editor
{
    /// <summary>
    /// 在 Scene 视图里用真实 GameObject 搭出某一关的 3D 预览（墙/地板/目标/箱子/玩家），
    /// 可在场景里旋转查看。预览对象标记为 DontSave，不会写入场景文件。
    /// 几何与运行时 <see cref="LevelSpawnSystem"/> 保持一致：world = (col, y, -row)。
    /// </summary>
    public static class LevelPreview
    {
        private const string RootName = "__SokobanLevelPreview__";
        private static GameObject _root;
        private static readonly Dictionary<Color, Material> _mats = new Dictionary<Color, Material>();

        public static bool HasPreview => FindRoot() != null;

        public static int Generate(LevelAsset level)
        {
            Clear();
            if (level == null || level.cells == null) return 0;

            _root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
            int count = 0;

            // 几何（y / scale / 展开规则）来自共享的 CellVisuals，保证与运行时 LevelSpawnSystem 一致。
            System.Span<CellVisual> visuals = stackalloc CellVisual[CellVisuals.MaxPerCell];
            for (int row = 0; row < level.Height; row++)
            {
                for (int col = 0; col < level.Width; col++)
                {
                    var c = level.cells[col, row];
                    int n = CellVisuals.Collect(c, visuals);
                    for (int k = 0; k < n; k++)
                    {
                        var v = visuals[k];
                        Spawn(col, row, v.Y, v.Scale, ColorFor(v.Kind, c), v.Kind.ToString());
                        count++;
                    }
                }
            }
            return count;
        }

        // 预览着色：与运行时 BoxColorSystem 等价——按属性（A 绿 / B 紫）取色，箱在匹配目标上提亮。
        private static Color ColorFor(VisualKind kind, CellType cell) => kind switch
        {
            VisualKind.Wall => CellType.Wall.ToColor(),
            VisualKind.Floor => CellType.Floor.ToColor(),
            VisualKind.Target => (cell.TargetKindOf() switch
            {
                2 => CellType.TargetC,
                1 => CellType.TargetB,
                _ => CellType.Target,
            }).ToColor(),
            VisualKind.Box => BoxColor(cell),
            VisualKind.Player => CellType.Player.ToColor(),
            _ => Color.gray,
        };

        private static Color BoxColor(CellType cell) => cell switch
        {
            CellType.BoxOnTarget => CellType.BoxOnTarget.ToColor(),
            CellType.BoxBOnTarget => CellType.BoxBOnTarget.ToColor(),
            CellType.BoxCOnTarget => CellType.BoxCOnTarget.ToColor(),
            CellType.BoxB => CellType.BoxB.ToColor(),
            CellType.BoxC => CellType.BoxC.ToColor(),
            _ => CellType.Box.ToColor(),
        };

        public static void Clear()
        {
            var root = FindRoot();
            if (root != null)
                Object.DestroyImmediate(root);
            _root = null;
        }

        private static GameObject FindRoot()
        {
            if (_root != null) return _root;
            _root = GameObject.Find(RootName);
            return _root;
        }

        private static void Spawn(int col, int row, float y, Vector3 scale, Color color, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(_root.transform, false);
            go.transform.localScale = scale;
            go.transform.position = new Vector3(col, y, -row);
            go.GetComponent<Renderer>().sharedMaterial = GetMaterial(color);
        }

        private static Material GetMaterial(Color color)
        {
            if (_mats.TryGetValue(color, out var m) && m != null)
                return m;

            var shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard")
                         ?? Shader.Find("Sprites/Default");
            m = new Material(shader) { hideFlags = HideFlags.DontSave, color = color };
            _mats[color] = m;
            return m;
        }
    }
}
