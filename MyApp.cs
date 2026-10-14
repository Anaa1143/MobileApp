using DX12GameProgramming.Enums;
using ProiectSPG.Structs;
using SharpDX;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Windows.Forms;
using Resource = SharpDX.Direct3D12.Resource;
using ShaderResourceViewDimension = SharpDX.Direct3D12.ShaderResourceViewDimension;

namespace ProiectSPG
{
    public enum RenderLayer
    {
        Opaque = 0,
        Sky = 1,
        Transparent = 2,
        AlphaTested = 3
    }

    // =========================================================================
    // STICKMAN – om simplu din boxuri/sfere
    // =========================================================================
    public class Stickman
    {
        public Vector3 Position;
        public float Angle;
        public float Speed;
        public float WalkCycle;
        public bool IsPlayerControlled;

        public float RandomAngle;
        public float RandomTimer;

        public RenderItem Head, Torso, ArmL, ArmR, LegL, LegR;

        public Stickman(Vector3 pos, float angle, float speed, bool isPlayer)
        {
            Position = pos;
            Angle = angle;
            Speed = speed;
            IsPlayerControlled = isPlayer;
            RandomAngle = angle;
            RandomTimer = 0;
        }
    }

    public class MyApp : D3DApp
    {
        private readonly IList<FrameResource> frameResources = new List<FrameResource>(NUMBER_OF_FRAME_RESOURCES);
        private readonly IList<AutoResetEvent> fenceEvents = new List<AutoResetEvent>(NUMBER_OF_FRAME_RESOURCES);
        private int currentFrameResourceIndex;

        private DescriptorHeap srvDescriptorHeap;
        private DescriptorHeap[] descriptorHeaps;
        private RootSignature rootSignature;

        private readonly IDictionary<string, ShaderBytecode> shaders = new Dictionary<string, ShaderBytecode>();
        private readonly IDictionary<string, PipelineState> pipelineStates = new Dictionary<string, PipelineState>();
        private readonly IDictionary<string, MeshGeometry> geometries = new Dictionary<string, MeshGeometry>();
        private readonly IDictionary<string, Material> materials = new Dictionary<string, Material>();
        private readonly IDictionary<string, Texture> textures = new Dictionary<string, Texture>();

        private InputLayoutDescription inputLayout;
        private readonly IList<RenderItem> allRenderItems = new List<RenderItem>();

        private readonly IDictionary<RenderLayer, IList<RenderItem>> renderItemLayers =
            new Dictionary<RenderLayer, IList<RenderItem>>
            {
                [RenderLayer.Opaque] = new List<RenderItem>(),
                [RenderLayer.Sky] = new List<RenderItem>(),
                [RenderLayer.AlphaTested] = new List<RenderItem>(),
                [RenderLayer.Transparent] = new List<RenderItem>()
            };

        private int skyTexHeapIndex;
        private PassConstants mainPassCB = PassConstants.Default;
        private readonly Camera camera = new Camera();
        private Point lastMousePosition;

        private readonly Random rng = new Random(42);

        // =========================================================================
        // MASINA JUCATOR
        // =========================================================================
        private Vector3 carPosition = new Vector3(-35.0f, 1.0f, -55.0f);
        private float carAngle = 0.0f;
        private float carSpeed = 20.0f;
        private float carRotSpeed = 2.0f;
        private float wheelSpin = 0.0f;
        private Vector3 oldCarPos;

        private RenderItem carBodyRI, carRoofRI, carHoodRI;
        private RenderItem wheelFLRI, wheelFRRI, wheelRLRI, wheelRRRI;

        // =========================================================================
        // MASINA AI – merge automat pe circuit
        // =========================================================================
        private static readonly Vector3[] aiWaypoints = new Vector3[]
        {
            new Vector3(-35f, 1f, -55f),
            new Vector3(-35f, 1f,  55f),
            new Vector3( 35f, 1f,  55f),
            new Vector3( 35f, 1f, -55f),
        };
        private int aiWaypointIndex = 0;
        private Vector3 aiCarPosition = new Vector3(-35f, 1f, 20f);
        private float aiCarAngle = 0.0f;
        private float aiWheelSpin = 0.0f;
        private float aiCarSpeed = 14.0f;

        private RenderItem aiBodyRI, aiRoofRI, aiHoodRI;
        private RenderItem aiWheelFLRI, aiWheelFRRI, aiWheelRLRI, aiWheelRRRI;

        // =========================================================================
        // STICKMEN
        // =========================================================================
        private readonly List<Stickman> stickmen = new List<Stickman>();

        // =========================================================================
        // SEMAFOARE
        // =========================================================================
        private struct TrafficLight
        {
            public Vector3 Position;
            public float Timer;
            public int Phase;
            public RenderItem PostRI, RedRI, YellowRI, GreenRI;
        }
        private readonly List<TrafficLight> trafficLights = new List<TrafficLight>();

        // =========================================================================
        // COLIZIUNI
        // =========================================================================
        private readonly List<(Vector3 center, float radius)> colliders = new List<(Vector3, float)>();

        // Helper: decalajul sinusoidal al raului la un Z dat
        private static float RiverSinOffset(float z) => (float)Math.Sin(z * 0.045f) * 7.0f;

        public MyApp() { MainWindowCaption = "Oras Demo"; }

        private FrameResource CurrentFrameResource => frameResources[currentFrameResourceIndex];
        private AutoResetEvent CurrentFenceEvent => fenceEvents[currentFrameResourceIndex];

        // =========================================================================
        public override void Initialize()
        {
            base.Initialize();
            CommandList.Reset(DirectCmdListAlloc, null);
            camera.Position = new Vector3(0.0f, 35.0f, -130.0f);

            LoadTextures();
            CreateRootSignature();
            CreateDescriptorHeaps();
            CreateShadersAndInputLayout();
            CreateShapeGeometries();
            CreateMaterials();
            CreateRenderItems();
            CreateFrameResources();
            CreatePipelineStateObjects();

            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);
            FlushCommandQueue();
        }

        protected override void OnResize()
        {
            base.OnResize();
            camera.SetLens(MathUtil.PiOverFour, AspectRatio, 1.0f, 1000.0f);
        }

        // =========================================================================
        protected override void Update(GameTimer gameTimer)
        {
            OnKeyboardInput(gameTimer);

            currentFrameResourceIndex = (currentFrameResourceIndex + 1) % NUMBER_OF_FRAME_RESOURCES;
            if (CurrentFrameResource.Fence != 0 && Fence.CompletedValue < CurrentFrameResource.Fence)
            {
                Fence.SetEventOnCompletion(CurrentFrameResource.Fence,
                    CurrentFenceEvent.SafeWaitHandle.DangerousGetHandle());
                CurrentFenceEvent.WaitOne();
            }

            float dt = gameTimer.DeltaTime;

            UpdateBillboards();
            UpdateTrafficLights(dt);
            UpdateStickmen(gameTimer);
            UpdateAICar(dt);
            UpdateObjectCBs();
            UpdateMaterialBuffer();
            UpdateMainPassCB(gameTimer);

            ApplyCarTransforms();

            // Coliziuni – sarim colizorul raului daca suntem pe pod (Z in [-7, +7])
            foreach (var col in colliders)
            {
                // Colizorul raului (centru la X=70, radius 12) e ignorat pe zona podului
                if (col.center.X > 65f && col.radius > 10f && Math.Abs(carPosition.Z) < 8.0f)
                    continue;

                float dx = Math.Abs(carPosition.X - col.center.X);
                float dz = Math.Abs(carPosition.Z - col.center.Z);
                float carR = 3.0f;
                if (dx < (carR + col.radius) && dz < (carR + col.radius))
                {
                    carPosition = oldCarPos;
                    ApplyCarTransforms();
                    break;
                }
            }

            float time = (float)gameTimer.TotalTime;
            foreach (var ri in allRenderItems)
            {
                if (ri.Material != null && ri.Material.Name == "water")
                {
                    ri.TexTransform = Matrix.Translation(0.0f, time * 0.025f, 0.0f);
                    ri.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
                }
            }
        }

        // =========================================================================
        // KEYBOARD
        // =========================================================================
        private void OnKeyboardInput(GameTimer gt)
        {
            float dt = gt.DeltaTime;

            if (IsKeyDown(Keys.W) || IsKeyDown(Keys.Up)) camera.Walk(12.0f * dt);
            if (IsKeyDown(Keys.S) || IsKeyDown(Keys.Down)) camera.Walk(-12.0f * dt);
            if (IsKeyDown(Keys.A) || IsKeyDown(Keys.Left)) camera.Strafe(-12.0f * dt);
            if (IsKeyDown(Keys.D) || IsKeyDown(Keys.Right)) camera.Strafe(12.0f * dt);
            if (IsKeyDown(Keys.R)) camera.Position = new Vector3(0.0f, 35.0f, -130.0f);
            camera.UpdateViewMatrix();

            oldCarPos = carPosition;
            if (IsKeyDown(Keys.J)) carAngle -= carRotSpeed * dt;
            if (IsKeyDown(Keys.L)) carAngle += carRotSpeed * dt;
            Vector3 fwd = new Vector3((float)Math.Sin(carAngle), 0f, (float)Math.Cos(carAngle));
            if (IsKeyDown(Keys.I)) { carPosition += fwd * carSpeed * dt; wheelSpin -= carSpeed * dt * 1.5f; }
            if (IsKeyDown(Keys.K)) { carPosition -= fwd * carSpeed * dt; wheelSpin += carSpeed * dt * 1.5f; }

            Stickman player = stickmen.FirstOrDefault(s => s.IsPlayerControlled);
            if (player != null)
            {
                float pSpeed = 5.0f;
                bool moving = false;
                if (IsKeyDown(Keys.NumPad8) || IsKeyDown(Keys.G))
                {
                    Vector3 pf = new Vector3((float)Math.Sin(player.Angle), 0f, (float)Math.Cos(player.Angle));
                    player.Position += pf * pSpeed * dt;
                    player.WalkCycle += dt * 8f;
                    moving = true;
                }
                if (IsKeyDown(Keys.NumPad2) || IsKeyDown(Keys.H))
                {
                    Vector3 pf = new Vector3((float)Math.Sin(player.Angle), 0f, (float)Math.Cos(player.Angle));
                    player.Position -= pf * pSpeed * dt;
                    player.WalkCycle += dt * 8f;
                    moving = true;
                }
                if (IsKeyDown(Keys.NumPad4) || IsKeyDown(Keys.B)) { player.Angle -= 2.5f * dt; moving = true; }
                if (IsKeyDown(Keys.NumPad6) || IsKeyDown(Keys.N)) { player.Angle += 2.5f * dt; moving = true; }
                if (!moving && player.WalkCycle != 0) player.WalkCycle = 0;
            }
        }

        // =========================================================================
        // APLICA TRANSFORMARILE MASINII JUCATOR
        // =========================================================================
        private void ApplyCarTransforms()
        {
            Matrix rot = Matrix.RotationY(carAngle);
            Vector3 W(Vector3 local) => carPosition + Vector3.TransformCoordinate(local, rot);

            carBodyRI.World = Matrix.Scaling(4.0f, 1.0f, 8.0f) * rot * Matrix.Translation(carPosition);
            carBodyRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

            Vector3 roofPos = W(new Vector3(0f, 1.0f, -0.5f));
            carRoofRI.World = Matrix.Scaling(3.2f, 0.9f, 4.5f) * rot * Matrix.Translation(roofPos);
            carRoofRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

            Vector3 hoodPos = W(new Vector3(0f, 0.3f, 3.2f));
            carHoodRI.World = Matrix.Scaling(3.6f, 0.4f, 1.8f) * rot * Matrix.Translation(hoodPos);
            carHoodRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

            Vector3[] wOff = {
                new Vector3(-2.2f, -0.7f,  2.8f), new Vector3( 2.2f, -0.7f,  2.8f),
                new Vector3(-2.2f, -0.7f, -2.8f), new Vector3( 2.2f, -0.7f, -2.8f),
            };
            RenderItem[] wh = { wheelFLRI, wheelFRRI, wheelRLRI, wheelRRRI };
            Matrix spin = Matrix.RotationX(wheelSpin);
            for (int i = 0; i < 4; i++)
            {
                Vector3 wp = W(wOff[i]);
                wh[i].World = Matrix.Scaling(1.1f, 1.1f, 0.7f) * spin * rot * Matrix.Translation(wp);
                wh[i].NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
            }
        }

        // =========================================================================
        // MASINA AI – navigare pe waypoints
        // =========================================================================
        private void UpdateAICar(float dt)
        {
            Vector3 target = aiWaypoints[aiWaypointIndex];
            Vector3 toTarget = target - aiCarPosition;
            toTarget.Y = 0;
            float dist = toTarget.Length();

            if (dist < 4.0f)
            {
                aiWaypointIndex = (aiWaypointIndex + 1) % aiWaypoints.Length;
                target = aiWaypoints[aiWaypointIndex];
                toTarget = target - aiCarPosition;
                toTarget.Y = 0;
            }

            float targetAngle = (float)Math.Atan2(toTarget.X, toTarget.Z);
            float angleDiff = targetAngle - aiCarAngle;
            while (angleDiff > (float)Math.PI) angleDiff -= (float)(Math.PI * 2);
            while (angleDiff < -(float)Math.PI) angleDiff += (float)(Math.PI * 2);
            aiCarAngle += angleDiff * Math.Min(dt * 4.0f, 1.0f);

            Vector3 aiFwd = new Vector3((float)Math.Sin(aiCarAngle), 0f, (float)Math.Cos(aiCarAngle));
            aiCarPosition += aiFwd * aiCarSpeed * dt;
            aiCarPosition.Y = 1.0f;
            aiWheelSpin -= aiCarSpeed * dt * 1.5f;

            ApplyAICarTransforms();
        }

        private void ApplyAICarTransforms()
        {
            Matrix rot = Matrix.RotationY(aiCarAngle);
            Vector3 W(Vector3 local) => aiCarPosition + Vector3.TransformCoordinate(local, rot);

            aiBodyRI.World = Matrix.Scaling(4.0f, 1.0f, 8.0f) * rot * Matrix.Translation(aiCarPosition);
            aiBodyRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

            Vector3 roofPos = W(new Vector3(0f, 1.0f, -0.5f));
            aiRoofRI.World = Matrix.Scaling(3.2f, 0.9f, 4.5f) * rot * Matrix.Translation(roofPos);
            aiRoofRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

            Vector3 hoodPos = W(new Vector3(0f, 0.3f, 3.2f));
            aiHoodRI.World = Matrix.Scaling(3.6f, 0.4f, 1.8f) * rot * Matrix.Translation(hoodPos);
            aiHoodRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

            Vector3[] wOff = {
                new Vector3(-2.2f, -0.7f,  2.8f), new Vector3( 2.2f, -0.7f,  2.8f),
                new Vector3(-2.2f, -0.7f, -2.8f), new Vector3( 2.2f, -0.7f, -2.8f),
            };
            RenderItem[] wh = { aiWheelFLRI, aiWheelFRRI, aiWheelRLRI, aiWheelRRRI };
            Matrix spin = Matrix.RotationX(aiWheelSpin);
            for (int i = 0; i < 4; i++)
            {
                Vector3 wp = W(wOff[i]);
                wh[i].World = Matrix.Scaling(1.1f, 1.1f, 0.7f) * spin * rot * Matrix.Translation(wp);
                wh[i].NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
            }
        }

        // =========================================================================
        // SEMAFOARE
        // =========================================================================
        private void UpdateTrafficLights(float dt)
        {
            float[] phaseDurations = { 5.0f, 1.0f, 5.0f };
            for (int i = 0; i < trafficLights.Count; i++)
            {
                var tl = trafficLights[i];
                tl.Timer += dt;
                if (tl.Timer >= phaseDurations[tl.Phase])
                {
                    tl.Timer = 0;
                    tl.Phase = (tl.Phase + 1) % 3;
                }

                tl.GreenRI.Material = tl.Phase == 0 ? materials["semGreen"] : materials["semOff"];
                tl.YellowRI.Material = tl.Phase == 1 ? materials["semYellow"] : materials["semOff"];
                tl.RedRI.Material = tl.Phase == 2 ? materials["semRed"] : materials["semOff"];
                tl.GreenRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
                tl.YellowRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
                tl.RedRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

                trafficLights[i] = tl;
            }
        }

        // =========================================================================
        // STICKMEN
        // =========================================================================
        private void UpdateStickmen(GameTimer gt)
        {
            float dt = gt.DeltaTime;
            float mapLimit = 125.0f; // era 90 – marit ca sa se plimbe si prin cartierele noi

            foreach (var sm in stickmen)
            {
                if (!sm.IsPlayerControlled)
                {
                    sm.RandomTimer -= dt;
                    if (sm.RandomTimer <= 0)
                    {
                        sm.RandomAngle = (float)(rng.NextDouble() * Math.PI * 2.0);
                        sm.RandomTimer = 2.0f + (float)rng.NextDouble() * 4.0f;
                    }
                    float angleDiff = sm.RandomAngle - sm.Angle;
                    while (angleDiff > (float)Math.PI) angleDiff -= (float)(Math.PI * 2);
                    while (angleDiff < -(float)Math.PI) angleDiff += (float)(Math.PI * 2);
                    sm.Angle += angleDiff * dt * 3.0f;

                    Vector3 dir = new Vector3((float)Math.Sin(sm.Angle), 0f, (float)Math.Cos(sm.Angle));
                    sm.Position += dir * sm.Speed * dt;
                    sm.WalkCycle += dt * sm.Speed * 1.5f;

                    if (Math.Abs(sm.Position.X) > mapLimit || Math.Abs(sm.Position.Z) > mapLimit)
                    {
                        sm.RandomAngle = (float)Math.Atan2(-sm.Position.X, -sm.Position.Z);
                        sm.RandomTimer = 1.0f;
                    }
                    // Nu intra in rau (rau la X=+70 centru, lat ~24)
                    if (sm.Position.X > 50.0f && !(sm.Position.X > 35.0f && sm.Position.X < 80.0f && Math.Abs(sm.Position.Z) < 6.0f))
                    {
                        sm.Position.X = 50.0f;
                        sm.RandomAngle = (float)Math.Atan2(-1, 0);
                    }
                }

                float wc = sm.WalkCycle;
                float legSwing = (float)Math.Sin(wc) * 0.5f;
                float armSwing = -(float)Math.Sin(wc) * 0.4f;

                Matrix smRot = Matrix.RotationY(sm.Angle);
                Vector3 P = sm.Position;
                Vector3 WL(Vector3 local) => P + Vector3.TransformCoordinate(local, smRot);

                Vector3 headPos = WL(new Vector3(0, 1.9f, 0));
                sm.Head.World = Matrix.Scaling(0.35f, 0.35f, 0.35f) * Matrix.Translation(headPos);
                sm.Head.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

                Vector3 torsoPos = WL(new Vector3(0, 1.15f, 0));
                sm.Torso.World = Matrix.Scaling(0.28f, 0.7f, 0.18f) * smRot * Matrix.Translation(torsoPos);
                sm.Torso.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

                Vector3 armLBase = WL(new Vector3(-0.22f, 1.45f, 0));
                Matrix armLRot = Matrix.RotationX(armSwing) * smRot;
                sm.ArmL.World = Matrix.Scaling(0.12f, 0.55f, 0.12f) * armLRot * Matrix.Translation(armLBase);
                sm.ArmL.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

                Vector3 armRBase = WL(new Vector3(0.22f, 1.45f, 0));
                Matrix armRRot = Matrix.RotationX(-armSwing) * smRot;
                sm.ArmR.World = Matrix.Scaling(0.12f, 0.55f, 0.12f) * armRRot * Matrix.Translation(armRBase);
                sm.ArmR.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

                Vector3 legLBase = WL(new Vector3(-0.12f, 0.7f, 0));
                Matrix legLRot = Matrix.RotationX(legSwing) * smRot;
                sm.LegL.World = Matrix.Scaling(0.14f, 0.65f, 0.14f) * legLRot * Matrix.Translation(legLBase);
                sm.LegL.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

                Vector3 legRBase = WL(new Vector3(0.12f, 0.7f, 0));
                Matrix legRRot = Matrix.RotationX(-legSwing) * smRot;
                sm.LegR.World = Matrix.Scaling(0.14f, 0.65f, 0.14f) * legRRot * Matrix.Translation(legRBase);
                sm.LegR.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
            }
        }

        // =========================================================================
        // BILLBOARD UPDATE
        // =========================================================================
        private void UpdateBillboards()
        {
            foreach (var ri in renderItemLayers[RenderLayer.Transparent])
            {
                if (ri.Material.Name == "tree" || ri.Material.Name == "tree2")
                {
                    Vector3 pos = new Vector3(ri.World.M41, ri.World.M42, ri.World.M43);
                    Vector3 toCam = camera.Position - pos;
                    toCam.Y = 0; toCam.Normalize();
                    float angle = (float)Math.Atan2(toCam.X, toCam.Z);
                    ri.World = Matrix.Scaling(10.0f, 10.0f, 10.0f)
                             * Matrix.RotationY(angle)
                             * Matrix.Translation(pos);
                    ri.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
                }
            }
        }

        // =========================================================================
        protected override void Draw(GameTimer gameTimer)
        {
            CommandAllocator cmdListAlloc = CurrentFrameResource.CommandAllocator;
            cmdListAlloc.Reset();
            CommandList.Reset(cmdListAlloc, pipelineStates["opaque"]);
            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);
            CommandList.ClearRenderTargetView(CurrentBackBufferView, Color.LightSteelBlue);
            CommandList.ClearDepthStencilView(DepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);
            CommandList.SetRenderTargets(CurrentBackBufferView, DepthStencilView);
            CommandList.SetDescriptorHeaps(descriptorHeaps.Length, descriptorHeaps);
            CommandList.SetGraphicsRootSignature(rootSignature);
            CommandList.SetGraphicsRootConstantBufferView(1, CurrentFrameResource.PassCB.Resource.GPUVirtualAddress);
            CommandList.SetGraphicsRootShaderResourceView(2, CurrentFrameResource.MaterialBuffer.Resource.GPUVirtualAddress);
            GpuDescriptorHandle skyTex = srvDescriptorHeap.GPUDescriptorHandleForHeapStart;
            skyTex += skyTexHeapIndex * CbvSrvUavDescriptorSize;
            CommandList.SetGraphicsRootDescriptorTable(3, skyTex);
            CommandList.SetGraphicsRootDescriptorTable(4, srvDescriptorHeap.GPUDescriptorHandleForHeapStart);

            CommandList.PipelineState = pipelineStates["opaque"];
            DrawRenderItems(CommandList, renderItemLayers[RenderLayer.Opaque]);
            CommandList.PipelineState = pipelineStates["transparent"];
            DrawRenderItems(CommandList, renderItemLayers[RenderLayer.Transparent]);
            CommandList.PipelineState = pipelineStates["sky"];
            DrawRenderItems(CommandList, renderItemLayers[RenderLayer.Sky]);

            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.RenderTarget, ResourceStates.Present);
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);
            SwapChain.Present(0, PresentFlags.None);
            CurrentFrameResource.Fence = ++CurrentFence;
            CommandQueue.Signal(Fence, CurrentFence);
        }

        protected override void OnMouseDown(MouseButtons button, Point location)
        {
            base.OnMouseDown(button, location);
            lastMousePosition = location;
        }

        protected override void OnMouseMove(MouseButtons button, Point location)
        {
            if ((button & MouseButtons.Left) != 0)
            {
                float dx = MathUtil.DegreesToRadians(0.25f * (location.X - lastMousePosition.X));
                float dy = MathUtil.DegreesToRadians(0.25f * (location.Y - lastMousePosition.Y));
                camera.Pitch(dy);
                camera.RotateY(dx);
            }
            lastMousePosition = location;
        }

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing)
            {
                rootSignature.Dispose();
                srvDescriptorHeap?.Dispose();
                foreach (Texture t in textures.Values) t.Dispose();
                foreach (FrameResource f in frameResources) f.Dispose();
                foreach (MeshGeometry g in geometries.Values) g.Dispose();
                foreach (PipelineState p in pipelineStates.Values) p.Dispose();
            }
            base.Dispose(isDisposing);
        }

        // =========================================================================
        private void UpdateObjectCBs()
        {
            foreach (RenderItem ri in allRenderItems)
            {
                if (ri.NumFramesDirty > 0)
                {
                    var obj = new ObjectConstants
                    {
                        World = Matrix.Transpose(ri.World),
                        TexTransform = Matrix.Transpose(ri.TexTransform),
                        MaterialIndex = ri.Material.MaterialConstantBufferIndex
                    };
                    CurrentFrameResource.ObjectCB.CopyData(ri.ObjCBIndex, ref obj);
                    ri.NumFramesDirty--;
                }
            }
        }

        private void UpdateMaterialBuffer()
        {
            var cb = CurrentFrameResource.MaterialBuffer;
            foreach (Material mat in materials.Values)
            {
                if (mat.NumberOfFramesDirty > 0)
                {
                    var md = new MaterialData
                    {
                        DiffuseAlbedo = mat.DiffuseAlbedo,
                        FresnelR0 = mat.FresnelR0,
                        Roughness = mat.Roughness,
                        MatTransform = Matrix.Transpose(mat.MatTransform),
                        DiffuseMapIndex = mat.DiffuseSrvHeapIndex
                    };
                    cb.CopyData(mat.MaterialConstantBufferIndex, ref md);
                    mat.NumberOfFramesDirty--;
                }
            }
        }

        private void UpdateMainPassCB(GameTimer gt)
        {
            Matrix view = camera.View;
            Matrix proj = camera.Proj;
            Matrix viewProj = view * proj;

            mainPassCB.View = Matrix.Transpose(view);
            mainPassCB.InvView = Matrix.Transpose(Matrix.Invert(view));
            mainPassCB.Proj = Matrix.Transpose(proj);
            mainPassCB.InvProj = Matrix.Transpose(Matrix.Invert(proj));
            mainPassCB.ViewProj = Matrix.Transpose(viewProj);
            mainPassCB.InvViewProj = Matrix.Transpose(Matrix.Invert(viewProj));
            mainPassCB.EyePosW = camera.Position;
            mainPassCB.RenderTargetSize = new Vector2(WindowWidth, WindowHeight);
            mainPassCB.InvRenderTargetSize = 1.0f / mainPassCB.RenderTargetSize;
            mainPassCB.NearZ = 1.0f;
            mainPassCB.FarZ = 1000.0f;
            mainPassCB.TotalTime = gt.TotalTime;
            mainPassCB.DeltaTime = gt.DeltaTime;

            mainPassCB.AmbientLight = new Vector4(0.28f, 0.28f, 0.38f, 1.0f);
            mainPassCB.Lights[0].Direction = new Vector3(0.57735f, -0.57735f, 0.57735f);
            mainPassCB.Lights[0].Strength = new Vector3(0.65f);
            mainPassCB.Lights[1].Direction = new Vector3(-0.57735f, -0.57735f, 0.57735f);
            mainPassCB.Lights[1].Strength = new Vector3(0.3f);
            mainPassCB.Lights[2].Direction = new Vector3(0.0f, -0.707f, -0.707f);
            mainPassCB.Lights[2].Strength = new Vector3(0.15f);

            if (CurrentFrameResource == null) return;
            if (mainPassCB.Lights == null) mainPassCB.Lights = Light.DefaultArray;

            int lightIndex = 3;
            float flicker = 0.88f + 0.24f * (float)Math.Sin(gt.TotalTime * 4.5f);
            float[] lampZ = { -52.5f, -37.5f, -22.5f, -7.5f, 7.5f, 22.5f, 37.5f, 52.5f };

            foreach (float fz in lampZ)
            {
                if (lightIndex >= 19) break;
                mainPassCB.Lights[lightIndex].Position = new Vector3(-44.0f, 7.5f, fz);
                mainPassCB.Lights[lightIndex].Strength = new Vector3(3.2f, 2.4f, 0.7f) * flicker;
                mainPassCB.Lights[lightIndex].FalloffStart = 1.0f;
                mainPassCB.Lights[lightIndex].FalloffEnd = 24.0f;
                lightIndex++;
            }
            foreach (float fz in lampZ)
            {
                if (lightIndex >= 19) break;
                mainPassCB.Lights[lightIndex].Position = new Vector3(44.0f, 7.5f, fz);
                mainPassCB.Lights[lightIndex].Strength = new Vector3(3.2f, 2.4f, 0.7f) * flicker;
                mainPassCB.Lights[lightIndex].FalloffStart = 1.0f;
                mainPassCB.Lights[lightIndex].FalloffEnd = 24.0f;
                lightIndex++;
            }

            CurrentFrameResource.PassCB.CopyData(0, ref mainPassCB);
        }

        // =========================================================================
        private void LoadTextures()
        {
            AddTexture("grass", "grass.dds");
            AddTexture("water", "water.dds");
            AddTexture("skyCubeMap", "grasscube1024.dds");
            AddTexture("street", "street.dds");
            AddTexture("tree", "tree02S.dds");
            AddTexture("house", "house1.dds");
            AddTexture("house2", "house2.dds");
            AddTexture("house3", "house3.dds");
            AddTexture("tree2", "treeArray2.dds");
            AddTexture("pavement", "pavement.dds");
        }

        private void AddTexture(string name, string fileName)
        {
            var tex = new Texture { Name = name, FileName = $"Textures\\{fileName}" };
            tex.Resource = TextureUtilities.CreateTextureFromDDS(Device, tex.FileName);
            textures[tex.Name] = tex;
        }

        private void CreateRootSignature()
        {
            var slots = new[]
            {
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(1, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 1), RootParameterType.ShaderResourceView),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 6, 1))
            };
            rootSignature = Device.CreateRootSignature(
                new RootSignatureDescription(
                    RootSignatureFlags.AllowInputAssemblerInputLayout,
                    slots, GetStaticSamplers()).Serialize());
        }

        private void CreateDescriptorHeaps()
        {
            srvDescriptorHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription
            {
                DescriptorCount = 20,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible
            });
            descriptorHeaps = new[] { srvDescriptorHeap };

            CpuDescriptorHandle cpu = srvDescriptorHeap.CPUDescriptorHandleForHeapStart;
            Resource[] tex2D = {
                textures["grass"].Resource,   textures["water"].Resource,
                textures["street"].Resource,  textures["tree"].Resource,
                textures["house"].Resource,   textures["house2"].Resource,
                textures["house3"].Resource,  textures["tree2"].Resource,
                textures["pavement"].Resource
            };
            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = D3DHelper.DefaultShader4ComponentMapping,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new ShaderResourceViewDescription.Texture2DResource
                { MostDetailedMip = 0, ResourceMinLODClamp = 0.0f }
            };
            foreach (var t in tex2D)
            {
                srvDesc.Format = t.Description.Format;
                srvDesc.Texture2D.MipLevels = t.Description.MipLevels;
                Device.CreateShaderResourceView(t, srvDesc, cpu);
                cpu += CbvSrvUavDescriptorSize;
            }
            Resource skyT = textures["skyCubeMap"].Resource;
            srvDesc.Dimension = ShaderResourceViewDimension.TextureCube;
            srvDesc.TextureCube = new ShaderResourceViewDescription.TextureCubeResource
            { MostDetailedMip = 0, MipLevels = skyT.Description.MipLevels, ResourceMinLODClamp = 0.0f };
            srvDesc.Format = skyT.Description.Format;
            Device.CreateShaderResourceView(skyT, srvDesc, cpu);
            skyTexHeapIndex = 9;
        }

        private void CreateShadersAndInputLayout()
        {
            shaders["standardVS"] = D3DHelper.CompileShader("Shaders\\Default.hlsl", "VS", "vs_5_1");
            shaders["opaquePS"] = D3DHelper.CompileShader("Shaders\\Default.hlsl", "PS", "ps_5_1");
            shaders["skyVS"] = D3DHelper.CompileShader("Shaders\\Sky.hlsl", "VS", "vs_5_1");
            shaders["skyPS"] = D3DHelper.CompileShader("Shaders\\Sky.hlsl", "PS", "ps_5_1");
            inputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float,  0, 0),
                new InputElement("NORMAL",   0, Format.R32G32B32_Float, 12, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float,    24, 0)
            });
        }

        float GetHillHeight(float x, float z) => 0.0f;
        Vector3 GetHillNormal(float x, float z) => new Vector3(0, 1, 0);

        // =========================================================================
        private void CreateShapeGeometries()
        {
            var vertices = new List<Vertex>();
            var indices = new List<short>();

            var gridMesh = GeometryGenerator.CreateGrid(350.0f, 350.0f, 70, 70);

            var treeMesh = new GeometryGenerator.MeshData();
            treeMesh.Vertices.Add(new GeometryGenerator.Vertex { Position = new Vector3(-0.5f, 0f, 0f), Normal = new Vector3(0, 0, -1), TexC = new Vector2(0, 1) });
            treeMesh.Vertices.Add(new GeometryGenerator.Vertex { Position = new Vector3(-0.5f, 1f, 0f), Normal = new Vector3(0, 0, -1), TexC = new Vector2(0, 0) });
            treeMesh.Vertices.Add(new GeometryGenerator.Vertex { Position = new Vector3(0.5f, 1f, 0f), Normal = new Vector3(0, 0, -1), TexC = new Vector2(1, 0) });
            treeMesh.Vertices.Add(new GeometryGenerator.Vertex { Position = new Vector3(0.5f, 0f, 0f), Normal = new Vector3(0, 0, -1), TexC = new Vector2(1, 1) });
            treeMesh.Indices32.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
            SubmeshGeometry tree = AppendMeshData(treeMesh, vertices, indices);

            var boxMesh = GeometryGenerator.CreateBox(1.0f, 1.0f, 1.0f, 1);

            // RAU: centru X=+70, sinusoidal
            var riverMesh = GeometryGenerator.CreateGrid(24.0f, 310.0f, 32, 130);
            for (int i = 0; i < riverMesh.Vertices.Count; i++)
            {
                var v = riverMesh.Vertices[i]; var p = v.Position;
                p.X += RiverSinOffset(p.Z);
                p.Y = 0.05f;
                v.Position = p; v.Normal = GetHillNormal(p.X, p.Z);
                riverMesh.Vertices[i] = v;
            }

            SubmeshGeometry box = AppendMeshData(boxMesh, vertices, indices);
            SubmeshGeometry grid = AppendMeshData(gridMesh, vertices, indices);
            SubmeshGeometry sphere = AppendMeshData(GeometryGenerator.CreateSphere(0.5f, 20, 20), vertices, indices);
            SubmeshGeometry river = AppendMeshData(riverMesh, vertices, indices);

            var geo = MeshGeometry.New(Device, CommandList, vertices, indices.ToArray(), "shapeGeo");
            geo.DrawArguments["box"] = box;
            geo.DrawArguments["grid"] = grid;
            geo.DrawArguments["sphere"] = sphere;
            geo.DrawArguments["river"] = river;
            geo.DrawArguments["tree"] = tree;
            geometries[geo.Name] = geo;
        }

        private SubmeshGeometry AppendMeshData(GeometryGenerator.MeshData meshData,
            List<Vertex> vertices, List<short> indices)
        {
            var sub = new SubmeshGeometry
            {
                IndexCount = meshData.Indices32.Count,
                StartIndexLocation = indices.Count,
                BaseVertexLocation = vertices.Count
            };
            vertices.AddRange(meshData.Vertices.Select(v =>
                new Vertex { Pos = v.Position, Normal = v.Normal, TexC = v.TexC }));
            indices.AddRange(meshData.GetIndices16());
            return sub;
        }

        private void CreatePipelineStateObjects()
        {
            var opaquePSO = new GraphicsPipelineStateDescription
            {
                InputLayout = inputLayout,
                RootSignature = rootSignature,
                VertexShader = shaders["standardVS"],
                PixelShader = shaders["opaquePS"],
                RasterizerState = RasterizerStateDescription.Default(),
                BlendState = BlendStateDescription.Default(),
                DepthStencilState = DepthStencilStateDescription.Default(),
                SampleMask = unchecked((int)uint.MaxValue),
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetCount = 1,
                SampleDescription = new SampleDescription(MsaaCount, MsaaQuality),
                DepthStencilFormat = DepthStencilFormat
            };
            opaquePSO.RenderTargetFormats[0] = BackBufferFormat;
            pipelineStates["opaque"] = Device.CreateGraphicsPipelineState(opaquePSO);

            var tPSO = opaquePSO.Copy();
            tPSO.RasterizerState.CullMode = CullMode.None;
            tPSO.BlendState.RenderTarget[0] = new RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                LogicOpEnable = false,
                SourceBlend = BlendOption.SourceAlpha,
                DestinationBlend = BlendOption.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceAlphaBlend = BlendOption.One,
                DestinationAlphaBlend = BlendOption.Zero,
                AlphaBlendOperation = BlendOperation.Add,
                LogicOp = LogicOperation.Noop,
                RenderTargetWriteMask = ColorWriteMaskFlags.All
            };
            pipelineStates["transparent"] = Device.CreateGraphicsPipelineState(tPSO);

            var skyPSO = opaquePSO.Copy();
            skyPSO.RasterizerState.CullMode = CullMode.None;
            skyPSO.DepthStencilState.DepthComparison = Comparison.LessEqual;
            skyPSO.VertexShader = shaders["skyVS"];
            skyPSO.PixelShader = shaders["skyPS"];
            pipelineStates["sky"] = Device.CreateGraphicsPipelineState(skyPSO);
        }

        private void CreateFrameResources()
        {
            for (int i = 0; i < NUMBER_OF_FRAME_RESOURCES; i++)
            {
                frameResources.Add(new FrameResource(Device, 1, allRenderItems.Count, materials.Count));
                fenceEvents.Add(new AutoResetEvent(false));
            }
        }

