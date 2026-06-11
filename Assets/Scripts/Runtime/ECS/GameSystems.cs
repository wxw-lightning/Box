using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Transforms;
using Unity.Rendering;
using UnityEngine;

namespace Sokoban
{
    /// <summary>读传统输入 → 写 MoveCommand / ControlRequest。主线程（SystemBase 非 Burst）。</summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class SokobanInputSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<GameState>();
            RequireForUpdate<MoveCommand>();
        }

        protected override void OnUpdate()
        {
            var ctrl = SystemAPI.GetSingletonRW<ControlRequest>();
            if (Input.GetKeyDown(KeyCode.R)) ctrl.ValueRW.Reset = true;
            if (Input.GetKeyDown(KeyCode.U)) ctrl.ValueRW.Undo = true;
            if (Input.GetKeyDown(KeyCode.N)) ctrl.ValueRW.LevelDelta = +1;
            if (Input.GetKeyDown(KeyCode.P)) ctrl.ValueRW.LevelDelta = -1;

            var state = SystemAPI.GetSingleton<GameState>();
            if (state.Won || state.Animating)
                return;

            int2 dir = default;
            bool has = false;
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) { dir = new int2(0, -1); has = true; }
            else if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) { dir = new int2(0, 1); has = true; }
            else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) { dir = new int2(-1, 0); has = true; }
            else if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) { dir = new int2(1, 0); has = true; }

            if (has)
            {
                var cmd = SystemAPI.GetSingletonRW<MoveCommand>();
                cmd.ValueRW.Dir = dir;
                cmd.ValueRW.HasValue = true;
            }
        }
    }

    /// <summary>
    /// 推箱核心规则（移植自 v1）：撞墙不动；箱子前方为墙或另一箱子则整步失败（不能一次推两个）。
    /// 成功则更新逻辑格、压撤销栈、给玩家/箱子加平滑动画。
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class MovementSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<GameState>();

        protected override void OnUpdate()
        {
            var cmdRef = SystemAPI.GetSingletonRW<MoveCommand>();
            if (!cmdRef.ValueRO.HasValue)
                return;
            cmdRef.ValueRW.HasValue = false;
            int2 dir = cmdRef.ValueRO.Dir;

            // 按值读取（下方 Animate 是结构性变更，会让 RefRW 句柄失效；状态写入推迟到末尾重新获取）。
            var state = SystemAPI.GetSingleton<GameState>();
            if (state.Won || state.Animating)
                return;
            int W = state.Width;
            int H = state.Height;

            if (!SystemAPI.TryGetSingletonEntity<Player>(out var playerEntity))
                return;

            var singleton = SystemAPI.GetSingletonEntity<GameState>();
            var grid = EntityManager.GetBuffer<GridCell>(singleton);

            int2 playerPos = EntityManager.GetComponentData<GridPosition>(playerEntity).Value;
            int2 next = playerPos + dir;
            if (IsWall(grid, W, H, next))
                return;

            // 箱子位置查询表。
            var boxMap = new NativeHashMap<int2, Entity>(16, Allocator.Temp);
            foreach (var (gp, ent) in SystemAPI.Query<RefRO<GridPosition>>().WithAll<Box>().WithEntityAccess())
                boxMap.TryAdd(gp.ValueRO.Value, ent);

            float duration = SystemAPI.ManagedAPI.TryGetSingleton<RenderResources>(out var res) ? res.MoveDuration : 0.12f;
            var undo = EntityManager.GetBuffer<UndoStep>(singleton);

            if (boxMap.TryGetValue(next, out var boxEntity))
            {
                int2 beyond = next + dir;
                if (IsWall(grid, W, H, beyond) || boxMap.ContainsKey(beyond))
                {
                    boxMap.Dispose();
                    return; // 箱子前方被挡，不能推
                }

                undo.Add(new UndoStep { Player = playerPos, Box = boxEntity, BoxFrom = next, HasBox = true });
                EntityManager.SetComponentData(boxEntity, new GridPosition { Value = beyond });
                Animate(boxEntity, beyond, duration);
                EntityManager.SetComponentData(playerEntity, new GridPosition { Value = next });
                Animate(playerEntity, next, duration);
            }
            else
            {
                undo.Add(new UndoStep { Player = playerPos, HasBox = false });
                EntityManager.SetComponentData(playerEntity, new GridPosition { Value = next });
                Animate(playerEntity, next, duration);
            }

            boxMap.Dispose();

            // 结构性变更已完成，重新获取单例 RW 再写状态。
            var stateRW = SystemAPI.GetSingletonRW<GameState>();
            stateRW.ValueRW.Moves += 1;
            stateRW.ValueRW.Animating = true;
        }

        private void Animate(Entity e, int2 toCell, float duration)
        {
            var lt = EntityManager.GetComponentData<LocalTransform>(e);
            float3 from = lt.Position;
            float3 to = LevelSpawnSystem.CellToWorld(toCell, from.y);
            EntityManager.AddComponentData(e, new MoveAnimation { From = from, To = to, Duration = duration, Elapsed = 0f });
        }

        public static bool IsWall(DynamicBuffer<GridCell> grid, int w, int h, int2 c)
        {
            if (c.x < 0 || c.x >= w || c.y < 0 || c.y >= h) return true;
            return (grid[c.y * w + c.x].Flags & GridFlags.Wall) != 0;
        }
    }

    /// <summary>插值移动动画；全部结束后清 Animating 标志，放行下一步输入。</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MovementSystem))]
    public partial class MoveAnimationSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<GameState>();

        protected override void OnUpdate()
        {
            float dt = SystemAPI.Time.DeltaTime;
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            int active = 0;

            foreach (var (lt, anim, e) in
                     SystemAPI.Query<RefRW<LocalTransform>, RefRW<MoveAnimation>>().WithEntityAccess())
            {
                anim.ValueRW.Elapsed += dt;
                float u = anim.ValueRO.Duration <= 0f ? 1f : math.saturate(anim.ValueRO.Elapsed / anim.ValueRO.Duration);
                float s = u * u * (3f - 2f * u); // smoothstep
                lt.ValueRW.Position = math.lerp(anim.ValueRO.From, anim.ValueRO.To, s);

                if (u >= 1f)
                {
                    lt.ValueRW.Position = anim.ValueRO.To;
                    ecb.RemoveComponent<MoveAnimation>(e);
                }
                else active++;
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();

            SystemAPI.GetSingletonRW<GameState>().ValueRW.Animating = active > 0;
        }
    }

    /// <summary>所有箱子都在目标格 → 过关。逻辑位置即时判定（不必等动画）。</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MovementSystem))]
    public partial class WinSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<GameState>();

        protected override void OnUpdate()
        {
            var stateRef = SystemAPI.GetSingletonRW<GameState>();
            if (stateRef.ValueRO.Won || stateRef.ValueRO.Animating)
                return; // 等动画落定再判定，避免过关提示比箱子早 ~0.12s 弹出
            int W = stateRef.ValueRO.Width;
            var singleton = SystemAPI.GetSingletonEntity<GameState>();
            var grid = EntityManager.GetBuffer<GridCell>(singleton);

            bool all = true;
            int count = 0;
            foreach (var gp in SystemAPI.Query<RefRO<GridPosition>>().WithAll<Box>())
            {
                count++;
                int2 p = gp.ValueRO.Value;
                if ((grid[p.y * W + p.x].Flags & GridFlags.Target) == 0) { all = false; break; }
            }
            if (count > 0 && all)
                stateRef.ValueRW.Won = true;
        }
    }

    /// <summary>箱子在目标格→绿，否则棕（每实体 URP _BaseColor 覆盖）。</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MovementSystem))]
    public partial class BoxColorSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<GameState>();

        protected override void OnUpdate()
        {
            int W = SystemAPI.GetSingleton<GameState>().Width;
            var singleton = SystemAPI.GetSingletonEntity<GameState>();
            var grid = EntityManager.GetBuffer<GridCell>(singleton);

            float4 onColor = ToFloat4(CellType.BoxOnTarget.ToColor());
            float4 offColor = ToFloat4(CellType.Box.ToColor());

            foreach (var (gp, col) in
                     SystemAPI.Query<RefRO<GridPosition>, RefRW<URPMaterialPropertyBaseColor>>().WithAll<Box>())
            {
                int2 p = gp.ValueRO.Value;
                bool onTarget = (grid[p.y * W + p.x].Flags & GridFlags.Target) != 0;
                col.ValueRW.Value = onTarget ? onColor : offColor;
            }
        }

        private static float4 ToFloat4(Color c) => new float4(c.r, c.g, c.b, c.a);
    }

    /// <summary>处理撤销/重置/切关请求。</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(MovementSystem))]
    public partial class ControlSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<GameState>();

        protected override void OnUpdate()
        {
            var ctrl = SystemAPI.GetSingletonRW<ControlRequest>();
            var stateRef = SystemAPI.GetSingletonRW<GameState>();

            if (ctrl.ValueRO.Reset)
            {
                ctrl.ValueRW.Reset = false;
                RequestRespawn();
                return;
            }

            if (ctrl.ValueRO.LevelDelta != 0)
            {
                int delta = ctrl.ValueRO.LevelDelta;
                ctrl.ValueRW.LevelDelta = 0;
                int count = math.max(1, stateRef.ValueRO.LevelCount);
                stateRef.ValueRW.LevelIndex = math.clamp(stateRef.ValueRO.LevelIndex + delta, 0, count - 1);
                RequestRespawn();
                return;
            }

            if (ctrl.ValueRO.Undo)
            {
                ctrl.ValueRW.Undo = false;
                DoUndo();
            }
        }

        private void RequestRespawn() => SystemAPI.GetSingletonRW<RespawnRequest>().ValueRW.Value = true;

        private void DoUndo()
        {
            var singleton = SystemAPI.GetSingletonEntity<GameState>();
            var undo = EntityManager.GetBuffer<UndoStep>(singleton);
            if (undo.Length == 0)
                return;

            UndoStep step = undo[undo.Length - 1];
            undo.RemoveAt(undo.Length - 1);
            int movesBefore = SystemAPI.GetSingleton<GameState>().Moves;

            // Snap 内部可能移除 MoveAnimation（结构性变更），故状态写入推迟到最后重新获取 RW。
            if (SystemAPI.TryGetSingletonEntity<Player>(out var playerEntity))
            {
                EntityManager.SetComponentData(playerEntity, new GridPosition { Value = step.Player });
                Snap(playerEntity, step.Player);
            }
            if (step.HasBox && EntityManager.Exists(step.Box))
            {
                EntityManager.SetComponentData(step.Box, new GridPosition { Value = step.BoxFrom });
                Snap(step.Box, step.BoxFrom);
            }

            var stateRW = SystemAPI.GetSingletonRW<GameState>();
            stateRW.ValueRW.Moves = math.max(0, movesBefore - 1);
            stateRW.ValueRW.Won = false;
        }

        // 撤销瞬时生效：直接对齐 transform 并移除可能在进行的动画。
        private void Snap(Entity e, int2 cell)
        {
            var lt = EntityManager.GetComponentData<LocalTransform>(e);
            lt.Position = LevelSpawnSystem.CellToWorld(cell, lt.Position.y);
            EntityManager.SetComponentData(e, lt);
            if (EntityManager.HasComponent<MoveAnimation>(e))
                EntityManager.RemoveComponent<MoveAnimation>(e);
        }
    }
}
