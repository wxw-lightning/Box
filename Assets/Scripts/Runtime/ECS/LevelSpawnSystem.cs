using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sokoban
{
    /// <summary>
    /// 监听 <see cref="RespawnRequest"/>，销毁旧关卡实体并按当前关卡数据重建：
    /// 写静态网格缓冲（墙/目标），为地板/墙/目标/箱子/玩家创建可渲染实体（Entities Graphics + URP）。
    ///
    /// 版本敏感点（Entities 1.x）：
    ///  - 渲染装配用 <c>RenderMeshUtility.AddComponents(entity, em, in desc, rma, MaterialMeshInfo)</c>。
    ///  - 非均匀缩放用 <see cref="PostTransformMatrix"/>（LocalTransform 只有均匀 Scale）。
    ///  - 每实体颜色覆盖用 <see cref="URPMaterialPropertyBaseColor"/>（映射 URP 的 _BaseColor）。
    /// 若安装的 Entities 小版本 API 略有差异，按官方文档微调此文件即可，其余逻辑系统不受影响。
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class LevelSpawnSystem : SystemBase
    {
        // 各类元素的视觉参数：y 高度 + 非均匀缩放。
        private static readonly float3 FloorScale = new float3(1f, 0.1f, 1f);
        private static readonly float3 WallScale = new float3(1f, 1f, 1f);
        private static readonly float3 TargetScale = new float3(0.45f, 0.04f, 0.45f);
        private static readonly float3 BoxScale = new float3(0.8f, 0.8f, 0.8f);
        private static readonly float3 PlayerScale = new float3(0.7f, 1f, 0.7f);

        protected override void OnCreate()
        {
            RequireForUpdate<RespawnRequest>();
            RequireForUpdate<RenderResources>();
            RequireForUpdate<GameState>();
        }

        protected override void OnUpdate()
        {
            var reqRef = SystemAPI.GetSingletonRW<RespawnRequest>();
            if (!reqRef.ValueRO.Value)
                return;
            reqRef.ValueRW.Value = false;

            var res = SystemAPI.ManagedAPI.GetSingleton<RenderResources>();
            if (res.Db == null || res.Db.Count == 0)
            {
                Debug.LogWarning("[Sokoban] LevelDatabase 为空，无法生成关卡。");
                return;
            }

            var em = EntityManager;

            // 销毁旧关卡所有元素。
            var oldQuery = SystemAPI.QueryBuilder().WithAll<LevelElement>().Build();
            em.DestroyEntity(oldQuery);

            var singleton = SystemAPI.GetSingletonEntity<GameState>();
            var stateRef = SystemAPI.GetSingletonRW<GameState>();

            int idx = math.clamp(stateRef.ValueRO.LevelIndex, 0, res.Db.Count - 1);
            var level = res.Db.Get(idx);
            if (level == null)
            {
                Debug.LogWarning($"[Sokoban] 第 {idx} 关引用为空，已跳过生成。");
                return;
            }
            int W = level.Width, H = level.Height;

            stateRef.ValueRW.Width = W;
            stateRef.ValueRW.Height = H;
            stateRef.ValueRW.LevelIndex = idx;
            stateRef.ValueRW.LevelCount = res.Db.Count;
            stateRef.ValueRW.Moves = 0;
            stateRef.ValueRW.Won = false;
            stateRef.ValueRW.Animating = false;

            // —— 第 1 遍：填静态网格缓冲（不可与下方建实体混在一个循环：建实体是结构性变更，会让 grid 句柄失效）。
            var grid = em.GetBuffer<GridCell>(singleton);
            grid.Clear();
            grid.ResizeUninitialized(W * H);
            for (int row = 0; row < H; row++)
            {
                for (int col = 0; col < W; col++)
                {
                    var c = level.cells[col, row];
                    byte flags = 0;
                    if (c.IsWall()) flags |= GridFlags.Wall;
                    if (c.IsTarget()) flags |= GridFlags.Target;
                    grid[row * W + col] = new GridCell { Flags = flags };
                }
            }

            // 清空撤销栈。
            em.GetBuffer<UndoStep>(singleton).Clear();

            // 共享渲染数据（一份 RenderMeshArray，5 材质 + 1 网格，利于批处理）。
            var materials = new Material[] { res.Floor, res.Wall, res.Target, res.BoxMat, res.PlayerMat };
            var rma = new RenderMeshArray(materials, new[] { res.CubeMesh });
            var desc = new RenderMeshDescription(ShadowCastingMode.On, receiveShadows: true);

            // —— 第 2 遍：建可渲染实体（结构性变更，此后不再触碰 grid 缓冲）。
            // 防御：即使数据里误放了多个玩家，也只标记第一个为 Player，避免破坏单例查询。
            bool playerPlaced = false;
            for (int row = 0; row < H; row++)
            {
                for (int col = 0; col < W; col++)
                {
                    var c = level.cells[col, row];
                    var cell = new int2(col, row);

                    if (c.IsWall())
                    {
                        CreateVisual(em, rma, desc, res.CubeMesh, 1, cell, 0.5f, WallScale);
                        continue;
                    }

                    CreateVisual(em, rma, desc, res.CubeMesh, 0, cell, -0.05f, FloorScale);
                    if (c.IsTarget())
                        CreateVisual(em, rma, desc, res.CubeMesh, 2, cell, 0.03f, TargetScale);

                    if (c.HasBox())
                    {
                        var e = CreateVisual(em, rma, desc, res.CubeMesh, 3, cell, 0.4f, BoxScale);
                        em.AddComponentData(e, new GridPosition { Value = cell });
                        em.AddComponent<Box>(e);
                        em.AddComponentData(e, new URPMaterialPropertyBaseColor { Value = ToFloat4(CellType.Box.ToColor()) });
                    }
                    else if (c.HasPlayer() && !playerPlaced)
                    {
                        playerPlaced = true;
                        var e = CreateVisual(em, rma, desc, res.CubeMesh, 4, cell, 0.5f, PlayerScale);
                        em.AddComponentData(e, new GridPosition { Value = cell });
                        em.AddComponent<Player>(e);
                        em.AddComponentData(e, new URPMaterialPropertyBaseColor { Value = ToFloat4(CellType.Player.ToColor()) });
                    }
                }
            }
        }

        private Entity CreateVisual(EntityManager em, RenderMeshArray rma, RenderMeshDescription desc,
            Mesh mesh, int materialIndex, int2 cell, float y, float3 scale)
        {
            var e = em.CreateEntity();
            RenderMeshUtility.AddComponents(e, em, in desc, rma,
                MaterialMeshInfo.FromRenderMeshArrayIndices(materialIndex, 0));

            float3 pos = CellToWorld(cell, y);
            em.AddComponentData(e, LocalTransform.FromPosition(pos)); // 均匀 scale=1
            em.AddComponentData(e, new PostTransformMatrix { Value = float4x4.Scale(scale) });
            em.AddComponent<LevelElement>(e);

            // AddComponents 已添加 RenderBounds，这里用网格实际 bounds 修正。
            // 直接构造 AABB（避免依赖 Unity.Mathematics.Extensions.Hybrid 的 ToAABB 扩展）。
            var b = mesh.bounds;
            em.SetComponentData(e, new RenderBounds
            {
                Value = new AABB { Center = b.center, Extents = b.extents }
            });
            return e;
        }

        public static float3 CellToWorld(int2 cell, float y) => new float3(cell.x, y, -cell.y);

        private static float4 ToFloat4(Color c) => new float4(c.r, c.g, c.b, c.a);
    }
}
