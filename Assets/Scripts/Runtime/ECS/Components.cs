using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Sokoban
{
    // ---------- 动态实体组件 ----------

    /// <summary>玩家/箱子所在的逻辑格子（col,row）。视觉位置由 LocalTransform 跟随。</summary>
    public struct GridPosition : IComponentData { public int2 Value; }

    public struct Player : IComponentData { } // tag：唯一玩家
    public struct Box : IComponentData { }    // tag：箱子

    /// <summary>平滑移动动画：在 Duration 内把 LocalTransform.Position 从 From 插值到 To。</summary>
    public struct MoveAnimation : IComponentData
    {
        public float3 From;
        public float3 To;
        public float Duration;
        public float Elapsed;
    }

    /// <summary>关卡元素标记，重生时据此批量销毁旧关卡所有实体。</summary>
    public struct LevelElement : IComponentData { }

    // ---------- 单例（都挂在同一个「游戏单例实体」上） ----------

    public struct GameState : IComponentData
    {
        public int Width;
        public int Height;
        public int LevelIndex;
        public int LevelCount;
        public int Moves;
        public bool Won;
        public bool Animating; // 有移动动画进行中
    }

    /// <summary>静态层网格标志。索引 = row*Width+col。</summary>
    public struct GridCell : IBufferElementData { public byte Flags; }

    public static class GridFlags
    {
        public const byte Wall = 1 << 0;
        public const byte Target = 1 << 1;
    }

    /// <summary>本帧的移动指令（由输入系统写，移动系统消费）。</summary>
    public struct MoveCommand : IComponentData { public int2 Dir; public bool HasValue; }

    /// <summary>撤销/重置/切关请求。</summary>
    public struct ControlRequest : IComponentData
    {
        public bool Undo;
        public bool Reset;
        public int LevelDelta; // +1 下一关 / -1 上一关
    }

    /// <summary>置 true 触发 LevelSpawnSystem 重建当前关卡。</summary>
    public struct RespawnRequest : IComponentData { public bool Value; }

    /// <summary>撤销栈元素。</summary>
    public struct UndoStep : IBufferElementData
    {
        public int2 Player;
        public Entity Box;
        public int2 BoxFrom;
        public bool HasBox;
    }

    /// <summary>
    /// 托管单例：存放编辑器拖入的网格、五类 URP 材质与关卡数据库，供生成/重置系统读取。
    /// （class 托管组件，可持有 UnityEngine 对象引用。）
    /// </summary>
    public class RenderResources : IComponentData
    {
        public Mesh CubeMesh;
        public Material Floor;
        public Material Wall;
        public Material Target;
        public Material BoxMat;
        public Material PlayerMat;
        public LevelDatabase Db;
        public float MoveDuration = 0.12f;
    }
}
