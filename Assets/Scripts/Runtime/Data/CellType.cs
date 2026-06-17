using UnityEngine;

namespace Sokoban
{
    /// <summary>
    /// 单个格子的内容（对应经典 Sokoban 符号）。用单枚举便于 Odin TableMatrix 点击绘制。
    /// 三属性（鸣潮元素主题）：A=气动(绿)（沿用 Target/Box 旧值），B=湮灭(紫)（TargetB/BoxB），C=衍射(黄)（TargetC/BoxC）。每个箱子需停在同属性目标才算过关。
    /// Floor 地板 / Wall 墙 / Target 气动目标 / Box 气动箱 / BoxOnTarget 气动箱在气动目标 /
    /// Player 玩家 / PlayerOnTarget 玩家在目标 / TargetB 湮灭目标 / BoxB 湮灭箱 / BoxBOnTarget 湮灭箱锁定 /
    /// TargetC 衍射目标 / BoxC 衍射箱 / BoxCOnTarget 衍射箱在衍射目标。
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
        TargetB = 7,
        BoxB = 8,
        BoxBOnTarget = 9,
        TargetC = 10,
        BoxC = 11,
        BoxCOnTarget = 12,
    }

    /// <summary>把 CellType 拆解为「地形 / 目标 / 占用物」的查询，运行时生成与编辑器共用，保证一致。
    /// 双属性：A 系=绿、B 系=紫。<see cref="BoxKindOf"/>/<see cref="TargetKindOf"/> 返回 0=A 1=B。</summary>
    public static class CellTypeExtensions
    {
        public static bool IsWall(this CellType c) => c == CellType.Wall;

        /// <summary>A 系（绿）目标。</summary>
        public static bool IsTargetA(this CellType c) =>
            c == CellType.Target || c == CellType.BoxOnTarget || c == CellType.PlayerOnTarget;

        /// <summary>B 系（紫/湮灭）目标。</summary>
        public static bool IsTargetB(this CellType c) =>
            c == CellType.TargetB || c == CellType.BoxBOnTarget;

        /// <summary>C 系（黄/衍射）目标。</summary>
        public static bool IsTargetC(this CellType c) =>
            c == CellType.TargetC || c == CellType.BoxCOnTarget;

        public static bool IsTarget(this CellType c) => c.IsTargetA() || c.IsTargetB() || c.IsTargetC();

        public static bool HasBox(this CellType c) =>
            c == CellType.Box || c == CellType.BoxOnTarget ||
            c == CellType.BoxB || c == CellType.BoxBOnTarget ||
            c == CellType.BoxC || c == CellType.BoxCOnTarget;

        public static bool HasPlayer(this CellType c) =>
            c == CellType.Player || c == CellType.PlayerOnTarget;

        /// <summary>箱子属性：0=气动(绿) 1=湮灭(紫) 2=衍射(黄)。仅在 <see cref="HasBox"/> 时有意义。</summary>
        public static byte BoxKindOf(this CellType c)
        {
            if (c == CellType.BoxB || c == CellType.BoxBOnTarget) return 1;
            if (c == CellType.BoxC || c == CellType.BoxCOnTarget) return 2;
            return 0;
        }

        /// <summary>目标属性：0=气动(绿) 1=湮灭(紫) 2=衍射(黄)。仅在 <see cref="IsTarget"/> 时有意义。</summary>
        public static byte TargetKindOf(this CellType c)
        {
            if (c.IsTargetB()) return 1;
            if (c.IsTargetC()) return 2;
            return 0;
        }

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
                case CellType.TargetB: return ',';
                case CellType.BoxB: return '%';
                case CellType.BoxBOnTarget: return '&';
                case CellType.TargetC: return ';';
                case CellType.BoxC: return '!';
                case CellType.BoxCOnTarget: return '=';
                default: return ' ';
            }
        }

        /// <summary>编辑器网格 / UI 用的着色。A 系=绿、B 系=紫；「已满足」由 <see cref="Satisfied"/> 统一提亮表示。</summary>
        public static Color ToColor(this CellType c)
        {
            switch (c)
            {
                case CellType.Wall: return new Color(0.25f, 0.27f, 0.33f);
                case CellType.Target: return new Color(0.35f, 0.62f, 0.40f);   // 目标A 绿（暗）
                case CellType.Box: return new Color(0.30f, 0.72f, 0.42f);      // 箱子A 绿
                case CellType.BoxOnTarget: return Satisfied(new Color(0.30f, 0.72f, 0.42f));
                case CellType.TargetB: return new Color(0.55f, 0.42f, 0.72f);  // 目标B 紫（暗）
                case CellType.BoxB: return new Color(0.62f, 0.40f, 0.85f);     // 湮灭箱 紫
                case CellType.BoxBOnTarget: return new Color(0.40f, 0.30f, 0.50f); // 湮灭箱锁定：暗紫，示意已变为不可推动的墙
                case CellType.TargetC: return new Color(0.68f, 0.60f, 0.18f);  // 衍射目标 黄（暗金）
                case CellType.BoxC: return new Color(0.93f, 0.82f, 0.22f);     // 衍射箱 黄
                case CellType.BoxCOnTarget: return Satisfied(new Color(0.93f, 0.82f, 0.22f));
                case CellType.Player: return new Color(0.27f, 0.55f, 0.92f);
                case CellType.PlayerOnTarget: return new Color(0.45f, 0.70f, 0.95f);
                default: return new Color(0.82f, 0.82f, 0.82f); // Floor
            }
        }

        /// <summary>「箱子已停在匹配目标」的提亮色：向白插值，保留自身属性色相（绿仍是绿、紫仍是紫）。</summary>
        public static Color Satisfied(Color baseColor) => Color.Lerp(baseColor, Color.white, 0.35f);
    }
}
