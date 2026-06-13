using System;
using UnityEngine;

namespace Sokoban
{
    /// <summary>一个格子会生成的视觉元素种类。其整数值同时用作 RenderMeshArray 里的材质索引。</summary>
    public enum VisualKind
    {
        Floor = 0,
        Wall = 1,
        Target = 2,
        Box = 3,
        Player = 4,
    }

    /// <summary>单个视觉元素的装配参数：材质索引 + Y 高度 + 非均匀缩放（用 Vector3 以便运行时/编辑器共用）。</summary>
    public readonly struct CellVisual
    {
        public readonly VisualKind Kind;
        public readonly int MaterialIndex; // == (int)Kind
        public readonly float Y;
        public readonly Vector3 Scale;

        public CellVisual(VisualKind kind, float y, Vector3 scale)
        {
            Kind = kind;
            MaterialIndex = (int)kind;
            Y = y;
            Scale = scale;
        }
    }

    /// <summary>
    /// 单元格视觉布局的唯一来源：每种元素的 Y 高度与缩放，以及「一个格子展开成哪些元素」的规则。
    /// 由运行时 <see cref="LevelSpawnSystem"/>、编辑器 <see cref="Sokoban.Editor.LevelPreview"/> 与
    /// <see cref="LevelAsset.EntityCount"/> 共用，保证三者几何一致、不会各自漂移。
    /// （颜色已由 <see cref="CellTypeExtensions.ToColor"/> 集中，此处只管几何。）
    /// </summary>
    public static class CellVisuals
    {
        /// <summary>一个格子最多展开的元素数：地板 + 目标 + 箱子/玩家。</summary>
        public const int MaxPerCell = 3;

        public static readonly CellVisual Floor  = new CellVisual(VisualKind.Floor,  -0.05f, new Vector3(1f,    0.1f,  1f));
        public static readonly CellVisual Wall   = new CellVisual(VisualKind.Wall,    0.5f,  new Vector3(1f,    1f,    1f));
        public static readonly CellVisual Target = new CellVisual(VisualKind.Target,  0.03f, new Vector3(0.45f, 0.04f, 0.45f));
        public static readonly CellVisual Box    = new CellVisual(VisualKind.Box,     0.4f,  new Vector3(0.8f,  0.8f,  0.8f));
        public static readonly CellVisual Player = new CellVisual(VisualKind.Player,  0.5f,  new Vector3(0.7f,  1f,    0.7f));

        /// <summary>
        /// 按生成顺序把某格子展开为视觉元素，写入 <paramref name="buffer"/>（容量需 ≥ <see cref="MaxPerCell"/>），返回数量。
        /// 规则同经典 Sokoban：墙占满整格；否则铺地板，再叠目标，再叠箱子或玩家。
        /// </summary>
        public static int Collect(CellType c, Span<CellVisual> buffer)
        {
            if (c.IsWall())
            {
                buffer[0] = Wall;
                return 1;
            }

            int n = 0;
            buffer[n++] = Floor;
            if (c.IsTarget()) buffer[n++] = Target;
            if (c.HasBox()) buffer[n++] = Box;
            else if (c.HasPlayer()) buffer[n++] = Player;
            return n;
        }
    }
}
