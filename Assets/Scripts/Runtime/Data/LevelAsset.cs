using System;
using UnityEngine;
using Sirenix.OdinInspector;
using Sirenix.Serialization;

namespace Sokoban
{
    /// <summary>
    /// 单关卡资产（每关一个 .asset 文件）。资源名 = 资产文件名(<c>this.name</c>)，关卡名 = <see cref="levelName"/>。
    /// 用 Odin <see cref="SerializedScriptableObject"/> 才能序列化 <c>CellType[,]</c>。
    /// <c>cells[col, row]</c>，col∈[0,Width) row∈[0,Height)。
    /// </summary>
    [CreateAssetMenu(fileName = "Level", menuName = "Sokoban/Level Asset", order = 0)]
    public class LevelAsset : SerializedScriptableObject
    {
        // 关卡名由编辑器窗口的「关卡名 / 应用」渲染，这里从默认 Inspector 隐藏避免重复。
        [HideInInspector]
        public string levelName = "新关卡";

        // 教学/提示文字（纯文本，可多行），运行时显示在关卡 UI 上。空串表示本关无提示。
        // 由编辑器窗口的「教学提示」文本域渲染，这里从默认 Inspector 隐藏避免重复。
        [HideInInspector]
        [TextArea(2, 5)]
        public string hint = "";

        // 网格由编辑器窗口用固定像素尺寸自绘（见 LevelEditorWindow.DrawGridSection），
        // 不用 Odin TableMatrix（其 SquareCells 会按可用宽度缩放、忽略 RowHeight）。
        // 这里从默认 Inspector 隐藏，仅保留 Odin 序列化到 .asset。
        [HideInInspector]
        [OdinSerialize]
        public CellType[,] cells = CreateEmpty(8, 7);

        public int Width => cells == null ? 0 : cells.GetLength(0);
        public int Height => cells == null ? 0 : cells.GetLength(1);

        public static CellType[,] CreateEmpty(int width, int height)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            var grid = new CellType[width, height];
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                    grid[x, y] = (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                        ? CellType.Wall : CellType.Floor;
            return grid;
        }

        /// <summary>改变网格尺寸，保留左上角重叠区域内容。</summary>
        public void Resize(int width, int height)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            var grid = CreateEmpty(width, height);
            int w = Mathf.Min(width, Width);
            int h = Mathf.Min(height, Height);
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    grid[x, y] = cells[x, y];
            cells = grid;
        }

        /// <summary>从另一关深拷贝内容（用于复制关卡）。</summary>
        public void CopyFrom(LevelAsset other)
        {
            levelName = other.levelName;
            hint = other.hint;
            cells = (CellType[,])other.cells.Clone();
        }

        // ---------- 统计 ----------

        public int CellCount => Width * Height;
        public int CountWalls() => Count(c => c.IsWall());
        public int CountFloors() => Count(c => !c.IsWall());     // 非墙格子运行时都会铺地板
        public int CountTargets() => Count(c => c.IsTarget());
        public int CountBoxes() => Count(c => c.HasBox());
        public int CountPlayers() => Count(c => c.HasPlayer());

        // 按属性统计（A=绿 / B=紫）：可玩性要求每属性箱数与目标数各自配平。
        public int CountTargetsA() => Count(c => c.IsTargetA());
        public int CountTargetsB() => Count(c => c.IsTargetB());
        public int CountTargetsC() => Count(c => c.IsTargetC());
        public int CountBoxesA() => Count(c => c.HasBox() && c.BoxKindOf() == 0);
        public int CountBoxesB() => Count(c => c.HasBox() && c.BoxKindOf() == 1);
        public int CountBoxesC() => Count(c => c.HasBox() && c.BoxKindOf() == 2);

        /// <summary>预览/运行时会生成的对象总数：墙 + 地板 + 目标 + 箱子 + 玩家。</summary>
        public int EntityCount()
        {
            Span<CellVisual> visuals = stackalloc CellVisual[CellVisuals.MaxPerCell];
            int n = 0;
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    n += CellVisuals.Collect(cells[x, y], visuals);
            return n;
        }

        private int Count(Func<CellType, bool> pred)
        {
            int n = 0;
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    if (pred(cells[x, y])) n++;
            return n;
        }
    }

    /// <summary>关卡编辑器的共享状态（当前画笔）。供 <see cref="LevelAsset"/> 的绘制方法读取。</summary>
    public static class LevelEditing
    {
        public static CellType Brush = CellType.Wall;
    }
}
