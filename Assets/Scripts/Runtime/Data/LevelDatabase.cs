using System.Collections.Generic;
using UnityEngine;

namespace Sokoban
{
    /// <summary>
    /// 关卡集合：一个**有序的 <see cref="LevelAsset"/> 引用列表**，决定游戏里的关卡顺序与切换。
    /// 每关本体是独立的 .asset（见 <see cref="LevelAsset"/>）；本资产只保存引用与顺序。
    /// 运行时由 <see cref="SokobanBootstrap"/> 引用；增删改/排序由关卡编辑器窗口操作。
    /// </summary>
    [CreateAssetMenu(fileName = "LevelDatabase", menuName = "Sokoban/Level Database", order = 1)]
    public class LevelDatabase : ScriptableObject
    {
        public List<LevelAsset> levels = new List<LevelAsset>();

        public int Count => levels.Count;

        public LevelAsset Get(int index) =>
            (index >= 0 && index < levels.Count) ? levels[index] : null;
    }
}
