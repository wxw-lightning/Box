using UnityEngine;

namespace Sokoban
{
    /// <summary>
    /// 单个格子的内容（对应经典 Sokoban 符号）。用单枚举便于 Odin TableMatrix 点击绘制。
    /// Floor 地板 / Wall 墙 / Target 目标 / Box 箱子 / BoxOnTarget 箱在目标 /
    /// Player 玩家 / PlayerOnTarget 玩家在目标。
    /// </summary>
    public enum CellType : byte
    {
        Floor = 0,
        Wall = 1,
        Target = 2,
        Box = 3,
        BoxOnTarget = 4,
        Player = 5,
        PlayerOnTarget = 6,
    }

    /// <summary>把 CellType 拆解为「地形 / 目标 / 占用物」的查询，运行时生成与编辑器共用，保证一致。</summary>
    public static class CellTypeExtensions
    {
        public static bool IsWall(this CellType c) => c == CellType.Wall;

        public static bool IsTarget(this CellType c) =>
            c == CellType.Target || c == CellType.BoxOnTarget || c == CellType.PlayerOnTarget;

        public static bool HasBox(this CellType c) =>
            c == CellType.Box || c == CellType.BoxOnTarget;

        public static bool HasPlayer(this CellType c) =>
            c == CellType.Player || c == CellType.PlayerOnTarget;

        /// <summary>编辑器/调试用的单字符符号。</summary>
        public static char ToSymbol(this CellType c)
        {
            switch (c)
            {
                case CellType.Wall: return '#';
                case CellType.Target: return '.';
                case CellType.Box: return '$';
                case CellType.BoxOnTarget: return '*';
                case CellType.Player: return '@';
                case CellType.PlayerOnTarget: return '+';
                default: return ' ';
            }
        }

        /// <summary>编辑器网格 / UI 用的着色。</summary>
        public static Color ToColor(this CellType c)
        {
            switch (c)
            {
                case CellType.Wall: return new Color(0.25f, 0.27f, 0.33f);
                case CellType.Target: return new Color(1f, 0.78f, 0.25f);
                case CellType.Box: return new Color(0.72f, 0.52f, 0.30f);
                case CellType.BoxOnTarget: return new Color(0.30f, 0.78f, 0.36f);
                case CellType.Player: return new Color(0.27f, 0.55f, 0.92f);
                case CellType.PlayerOnTarget: return new Color(0.45f, 0.70f, 0.95f);
                default: return new Color(0.82f, 0.82f, 0.82f); // Floor
            }
        }
    }
}
