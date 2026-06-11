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

            for (int row = 0; row < level.Height; row++)
            {
                for (int col = 0; col < level.Width; col++)
                {
                    var c = level.cells[col, row];
                    if (c.IsWall())
                    {
                        Spawn(col, row, 0.5f, new Vector3(1f, 1f, 1f), CellType.Wall.ToColor(), "Wall");
                        count++;
                        continue;
                    }

                    Spawn(col, row, -0.05f, new Vector3(1f, 0.1f, 1f), CellType.Floor.ToColor(), "Floor");
                    count++;

                    if (c.IsTarget())
                    {
                        Spawn(col, row, 0.03f, new Vector3(0.45f, 0.04f, 0.45f), CellType.Target.ToColor(), "Target");
                        count++;
                    }
                    if (c.HasBox())
                    {
                        var color = (c == CellType.BoxOnTarget ? CellType.BoxOnTarget : CellType.Box).ToColor();
                        Spawn(col, row, 0.4f, new Vector3(0.8f, 0.8f, 0.8f), color, "Box");
                        count++;
                    }
                    else if (c.HasPlayer())
                    {
                        Spawn(col, row, 0.5f, new Vector3(0.7f, 1f, 0.7f), CellType.Player.ToColor(), "Player");
                        count++;
                    }
                }
            }
            return count;
        }

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
