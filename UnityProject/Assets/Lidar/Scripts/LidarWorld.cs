using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityLidarViewer
{
    [Serializable] public class SceneManifest
    {
        public int version, displayPointCount, collisionVoxelCount;
        public string field, groundFile,slimCollisionFile;
        public int slimCollisionCount;
        public string[] pointFiles, collisionFiles;
        public DisplayField[] displayFields;
        public PointTile[] tiles;
        public bool hasRgb;
        public float fieldMin, fieldMax, voxelSize;
        public float[] spawn, boundsMin, boundsMax;
    }

    [Serializable] public class DisplayField
    {
        public string name, file;
    }

    [Serializable] public class SlimObstacle
    {
        public float x,y,z,radius,height;
    }
    [Serializable] public class SlimObstacleFile
    {
        public int version;
        public SlimObstacle[] colliders;
    }

    [StructLayout(LayoutKind.Sequential)] public struct LidarPoint
    {
        public Vector3 position;
        public uint rgba;
        public float intensity, scalar;
        public uint classification, returnNumber;
    }

    public sealed class LidarWorld : MonoBehaviour
    {
        public Shader pointShader;
        public string sceneFolder = "LidarScene";
        public GameObject playerPrefab;
        public Volume glowVolume;
        [Range(.5f,4f)] public float pointRadiusPixels = 1.5f;
        [Range(0,12)] public float emission = 4;
        [Range(.3f,3)] public float fieldGamma = 1.5f;
        [Range(0,2)] public float bloomStrength = .65f;
        public bool showGroundPoints = true, showGroundSurface, showCollision;
        public bool showPerformance = true;
        public int displayPointBudget = 3000000, streamMemoryMB = 256;
        public float nearDetailDistance = 20, renderDistance = 300;
        [Tooltip("Block trunks and near-ground vertical objects with tight collision proxies.")]
        public bool enableObstacleCollision = true;
        [Tooltip("Use narrow near-ground proxies instead of the original full voxel volumes.")]
        public bool useSlimObstacleCollision = true;
        [Range(.5f,1.2f)] public float slimRadiusScale = .9f;
        static readonly string[] PaletteNames = {
            "Magma", "Viridis", "Plasma", "Inferno", "Cividis",
            "Blue to red", "High contrast", "Turbo", "Coolwarm", "Grayscale"
        };
        enum FieldSource { Rgb, LegacyScalar, Classification, Intensity, ReturnNumber, Column }
        readonly List<(string name, FieldSource source, string file)> fieldOptions = new();
        int selectedField, selectedPalette, openDropdown;
        Vector2 dropdownScroll, panelScroll;
        ComputeBuffer displayValues;
        ComputeBuffer tilePlaceholder;
        PointTileStreamer streamer;
        float fps, frameSeconds;
        int frameCount;
        public int RenderedPointCount => streamer != null ? streamer.RenderedPoints : Manifest?.displayPointCount ?? 0;
        public long ResidentPointBytes => streamer != null ? streamer.ResidentBytes : (Manifest?.displayPointCount ?? 0) * 36L;
        Texture2D paletteAtlas, watermark;
        string datasetFolder, fieldError;
        public bool SettingsOpen { get; internal set; }
        Rect PanelRect => new Rect((Screen.width - 460) / 2f, (Screen.height - Mathf.Min(560, Screen.height - 40)) / 2f,
            460, Mathf.Min(560, Screen.height - 40));
        public SceneManifest Manifest { get; private set; }
        public readonly List<MeshCollider> Colliders = new List<MeshCollider>();
        public readonly List<CapsuleCollider> SlimColliders = new List<CapsuleCollider>();
        readonly List<ComputeBuffer> buffers = new List<ComputeBuffer>();
        readonly List<int> counts = new List<int>();
        readonly List<MaterialPropertyBlock> properties = new List<MaterialPropertyBlock>();
        readonly List<Mesh> meshes = new List<Mesh>();
        readonly List<MeshRenderer> obstacleRenderers = new List<MeshRenderer>();
        readonly List<MeshRenderer> slimRenderers = new List<MeshRenderer>();
        readonly List<float> slimRadii = new List<float>();
        Material points, groundMaterial, obstacleMaterial;
        MeshRenderer groundRenderer;
        Bounds bounds;
        string error;
        MK.Glow.URP.MKGlowLite mkGlow;
        bool appliedObstacleCollision;
        bool appliedSlimMode;
        float appliedRadiusScale;
        Explorer explorer;
        bool sceneReady;
        float reveal = 1, revealElapsed;
        const float PreparationShare = .65f;
        float introProgress;
        public bool IntroActive => reveal < 1;

        IEnumerator Start()
        {
            var args = Environment.GetCommandLineArgs();
            bool smokeTest = Array.IndexOf(args, "--smoke-test") >= 0;
            bool benchmark = Array.IndexOf(args, "--benchmark") >= 0;
            if (smokeTest && Application.isBatchMode)
            {
                if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
                if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            }
            bool showIntro = !Application.isBatchMode && !benchmark;
            if (showIntro)
            {
                reveal = 0;
                yield return null;
                yield return null;
            }
            try
            {
                int index=Array.IndexOf(args,"--scene");
                if(index>=0)
                {
                    if(index+1>=args.Length)throw new ArgumentException("--scene requires a folder containing manifest.json.");
                    sceneFolder=Path.GetFullPath(args[index+1]);
                }
                if (benchmark && Application.isBatchMode)
                    throw new InvalidOperationException("The rendering benchmark requires a visible player window.");
            }
            catch (Exception e)
            {
                ReportLoadFailure(e, smokeTest || benchmark);
            }
            if (error != null) yield break;
            using (var loader = Load(showIntro))
            {
                while (true)
                {
                    bool more = false;
                    try { more = loader.MoveNext(); }
                    catch (Exception e) { ReportLoadFailure(e, smokeTest || benchmark); }
                    if (!more) break;
                    yield return loader.Current;
                }
            }
            if (!sceneReady) yield break;
            if (smokeTest) StartCoroutine(RuntimeSmokeTest());
            if (benchmark) StartCoroutine(Benchmark());
        }

        void ReportLoadFailure(Exception exception, bool exit)
        {
            error = exception.Message;
            Debug.LogException(exception);
            if (exit) Application.Quit(1);
        }

        IEnumerator RuntimeSmokeTest()
        {
            yield return new WaitForSeconds(3);
            if (streamer != null)
            {
                float deadline = Time.realtimeSinceStartup + 20;
                while (streamer.RenderedPoints == 0 && streamer.Error == null && Time.realtimeSinceStartup < deadline) yield return null;
                SelectField("classification");
                if (Manifest.hasRgb) SelectField("RGB");
                SelectField(Manifest.field);
                int originalMemory = streamMemoryMB;
                streamMemoryMB = 16;
                yield return new WaitForSeconds(2);
                bool streamingOk = streamer.Error == null && streamer.RenderedPoints > 0 &&
                    streamer.RenderedPoints <= displayPointBudget && streamer.ResidentBytes <= 16L * 1048576;
                Debug.Log(streamingOk ? "LIDAR_STREAMING_PASS field switching, bounded residency, background loads and visible point budget."
                    : "LIDAR_STREAMING_FAIL tile loads, field switching or point/cache limits.");
                streamMemoryMB = originalMemory;
                if (!streamingOk) { Application.Quit(1); yield break; }
            }
            var explorer=FindFirstObjectByType<Explorer>();
            bool startsInFirstPerson = explorer && !explorer.ThirdPerson &&
                explorer.CurrentPlayer && explorer.CurrentPlayer.GetComponent<StarterAssets.FirstPersonController>();
            Debug.Log(startsInFirstPerson ? "LIDAR_FIRST_PERSON_PASS starts in first person." : "LIDAR_FIRST_PERSON_FAIL incorrect starting perspective.");
            bool startupMousePaired = startsInFirstPerson &&
                explorer.CurrentPlayer.GetComponent<PlayerInput>().devices.Any(device => device is Mouse);
            Debug.Log(startupMousePaired ? "LIDAR_STARTUP_MOUSE_PASS first-person controller owns the mouse before switching views."
                : "LIDAR_STARTUP_MOUSE_FAIL first-person controller has no mouse.");
            bool ok=startupMousePaired&&explorer.CurrentBody&&explorer.CurrentBody.isGrounded&&mkGlow&&mkGlow.IsActive()&&
                Colliders[0].enabled&&(!enableObstacleCollision||!useSlimObstacleCollision||
                    (SlimColliders.Count==Manifest.slimCollisionCount&&SlimColliders.TrueForAll(c=>c.enabled)&&Colliders.GetRange(1,Colliders.Count-1).TrueForAll(c=>!c.enabled)));
            if (explorer)
            {
                explorer.SetThirdPerson(true);
                var animator = explorer.CharacterAnimator;
                bool outfitOk = animator && animator.isHuman && animator.GetBoneTransform(HumanBodyBones.Head) &&
                    animator.GetComponentsInChildren<MeshRenderer>().Any(mesh => mesh.name == "Main compartment") &&
                    animator.GetComponentsInChildren<SkinnedMeshRenderer>().Any(skin => skin.name == "Man_Jacket_Mesh" &&
                        skin.sharedMaterials.All(material => material && material.shader.name == "Universal Render Pipeline/Lit"));
                Debug.Log(outfitOk ? "LIDAR_ADVENTURE_PASS humanoid rig and URP jacket active." : "LIDAR_ADVENTURE_FAIL rig or jacket missing.");
                ok &= outfitOk;
                ok &= !SettingsOpen;
                bool avatarScaleOk = true;
                foreach (bool thirdPerson in new[] { true, false })
                {
                    explorer.SetThirdPerson(thirdPerson);
                    var body = explorer.CurrentBody;
                    avatarScaleOk &= Mathf.Abs(body.height * body.transform.lossyScale.y -
                        (body.height - Explorer.HeightReduction)) < .01f;
                    var input = explorer.CurrentPlayer.GetComponent<UnityEngine.InputSystem.PlayerInput>();
                    explorer.SetSettingsOpen(true);
                    ok &= SettingsOpen && !input.inputIsActive && Cursor.visible && Cursor.lockState == CursorLockMode.None;
                    explorer.SetSettingsOpen(false);
                    ok &= !SettingsOpen && input.inputIsActive && !Cursor.visible &&
                        (Application.isBatchMode || Cursor.lockState == CursorLockMode.Locked);
                }
                ok &= avatarScaleOk;
                Debug.Log(avatarScaleOk ? "LIDAR_AVATAR_SCALE_PASS both controllers shortened by 0.33 m."
                    : "LIDAR_AVATAR_SCALE_FAIL incorrect controller height.");
                Debug.Log(ok ? "SETTINGS_POPUP_PASS starts hidden, releases input on open and restores input on close in both perspectives."
                    : "SETTINGS_POPUP_FAIL settings or runtime validation failed.");
            }
            Debug.Log(ok?"LIDAR_RUNTIME_PASS scan, vendor controller, tight collision proxies and MK Glow Lite active.":"LIDAR_RUNTIME_FAIL controller, collision or MK Glow validation failed.");
            Application.Quit(ok?0:1);
        }

        IEnumerator Benchmark()
        {
            int previousVsync = QualitySettings.vSyncCount;
            int previousFrameRate = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            yield return null;
            var explorer = FindFirstObjectByType<Explorer>();
            if (explorer)
            {
                explorer.SetCapture(false);
                explorer.enabled = false;
            }
            yield return new WaitForSecondsRealtime(8);
            var samples = new List<float>();
            double drawn = 0;
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 12)
            {
                yield return null;
                samples.Add(Time.unscaledDeltaTime);
                drawn += RenderedPointCount;
            }
            samples.Sort();
            double total = 0;
            foreach (float sample in samples) total += sample;
            double averageFps = samples.Count / total;
            float p95 = samples[Mathf.Min(samples.Count - 1, Mathf.FloorToInt(samples.Count * .95f))] * 1000;
            bool ok = RenderedPointCount > 0 && (streamer == null || streamer.Error == null);
            Debug.Log($"LIDAR_BENCHMARK version={Manifest.version} fps={averageFps:F2} p95Ms={p95:F2} " +
                $"drawn={drawn / samples.Count:F0} residentMB={ResidentPointBytes / 1048576.0:F2} success={ok}");
            if (explorer) explorer.enabled = true;
            QualitySettings.vSyncCount = previousVsync;
            Application.targetFrameRate = previousFrameRate;
        }

        IEnumerator<object> Load(bool showIntro)
        {
            string folder = Path.Combine(Application.streamingAssetsPath, sceneFolder);
            datasetFolder = folder;
            Manifest = JsonUtility.FromJson<SceneManifest>(File.ReadAllText(Path.Combine(folder,"manifest.json")));
            if (Manifest.version != 1 && Manifest.version != 2) throw new InvalidDataException("Unsupported LiDAR scene version.");
            if (!pointShader) pointShader = Shader.Find("UnityLidarViewer/Points");
            if (!pointShader || !pointShader.isSupported) throw new InvalidOperationException("Point shader unavailable; use a desktop DX11/DX12 GPU.");
            points = new Material(pointShader);
            paletteAtlas = Resources.Load<Texture2D>("LidarPalettes");
            watermark = Resources.Load<Texture2D>("QuantitativeEcology");
            if (!paletteAtlas) throw new InvalidOperationException("Missing color palettes.");
            displayValues = new ComputeBuffer(1, sizeof(float));
            points.SetBuffer("_DisplayValues", displayValues);
            tilePlaceholder = new ComputeBuffer(1, 16);
            points.SetBuffer("_TilePoints", tilePlaceholder);
            points.SetInt("_Streamed", 0);
            if (Manifest.version == 2) streamer = new PointTileStreamer(folder, Manifest, points);
            var pointFiles = Manifest.version == 1 ? Manifest.pointFiles : Array.Empty<string>();
            int preparationSteps = pointFiles.Length + Manifest.collisionFiles.Length + 3;
            int completedSteps = 0;
            int pointOffset = 0;
            foreach (string name in pointFiles)
            {
                byte[] data = File.ReadAllBytes(Path.Combine(folder,name));
                if (System.Text.Encoding.ASCII.GetString(data,0,4)!="LP01") throw new InvalidDataException(name);
                int n = BitConverter.ToInt32(data,4);
                if (data.Length != 8+(long)n*32) throw new InvalidDataException("Invalid point buffer length: "+name);
                LidarPoint[] p = new LidarPoint[n];
                var pin = GCHandle.Alloc(p,GCHandleType.Pinned);
                try { Marshal.Copy(data,8,pin.AddrOfPinnedObject(),n*32); }
                finally { pin.Free(); }
                var buffer = new ComputeBuffer(n,32);
                buffer.SetData(p); buffers.Add(buffer); counts.Add(n);
                var block=new MaterialPropertyBlock();block.SetBuffer("_Points",buffer);
                block.SetInt("_PointOffset", pointOffset);
                properties.Add(block);
                pointOffset += n;
                introProgress = PreparationShare * ++completedSteps / preparationSteps;
                if (showIntro) yield return null;
            }
            if (Manifest.version == 1 && pointOffset != Manifest.displayPointCount) throw new InvalidDataException("Point count does not match the manifest.");
            ConfigureDisplayFields();
            introProgress = PreparationShare * ++completedSteps / preparationSteps;
            if (showIntro) yield return null;
            bounds = new Bounds(); bounds.SetMinMax(V(Manifest.boundsMin),V(Manifest.boundsMax)); bounds.Expand(2);
            if(!glowVolume||!glowVolume.profile.TryGet(out mkGlow))throw new InvalidOperationException("MK Glow Lite volume is not configured.");
            groundMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            groundMaterial.color = new Color(.14f,.18f,.19f);
            obstacleMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            obstacleMaterial.color = new Color(.1f,.6f,.65f);
            groundRenderer = CreateMesh(folder,Manifest.groundFile,groundMaterial,true).GetComponent<MeshRenderer>();
            introProgress = PreparationShare * ++completedSteps / preparationSteps;
            int collisionIndex = 0;
            foreach (string name in Manifest.collisionFiles)
            {
                obstacleRenderers.Add(CreateMesh(folder,name,obstacleMaterial,false,false).GetComponent<MeshRenderer>());
                introProgress = PreparationShare * ++completedSteps / preparationSteps;
                if (showIntro && ++collisionIndex % 64 == 0) yield return null;
            }
            LoadSlimObstacles(folder);
            SetObstacleCollision(enableObstacleCollision);
            Physics.SyncTransforms();
            if(!playerPrefab)throw new InvalidOperationException("Missing Starter Assets player prefab. Run LiDAR > Create Walkthrough Scene.");
            var player = Instantiate(playerPrefab,V(Manifest.spawn),Quaternion.identity);
            player.name="Starter Assets controllers";
            explorer = player.GetComponent<Explorer>();
            explorer.world = this;
            player.SetActive(true);
            introProgress = PreparationShare;
            sceneReady = true;
            Debug.Log($"LIDAR_LOADED points={Manifest.displayPointCount} colliders={Colliders.Count}");
        }

        static Vector3 V(float[] a) => new Vector3(a[0],a[1],a[2]);

        void ConfigureDisplayFields()
        {
            if (Manifest.displayFields == null || Manifest.hasRgb)
                fieldOptions.Add(("RGB", FieldSource.Rgb, null));
            if (Manifest.displayFields != null)
            {
                foreach (var field in Manifest.displayFields)
                    fieldOptions.Add((field.name, FieldSource.Column, field.file));
            }
            else
            {
                fieldOptions.Add(("intensity", FieldSource.Intensity, null));
                fieldOptions.Add(("classification", FieldSource.Classification, null));
                fieldOptions.Add(("return_number", FieldSource.ReturnNumber, null));
                if (!string.IsNullOrEmpty(Manifest.field) && !fieldOptions.Exists(f => f.name == Manifest.field))
                    fieldOptions.Add((Manifest.field, FieldSource.LegacyScalar, null));
            }
            int initial = fieldOptions.FindIndex(f => f.name == Manifest.field);
            SelectField(initial < 0 ? 0 : initial);
        }

        public bool SelectField(string name)
        {
            int index = fieldOptions.FindIndex(f => f.name == name);
            if (index < 0) return false;
            SelectField(index);
            return selectedField == index && fieldError == null;
        }

        public void SelectPalette(int index)
        {
            selectedPalette = Mathf.Clamp(index, 0, PaletteNames.Length - 1);
        }

        void SelectField(int index)
        {
            try
            {
                var field = fieldOptions[index];
                if (streamer != null)
                    streamer.SelectField(field.source == FieldSource.Rgb ? -1 : index - (Manifest.hasRgb ? 1 : 0));
                else if (field.source == FieldSource.Column)
                {
                    var data = File.ReadAllBytes(Path.Combine(datasetFolder, field.file));
                    if (data.Length != (long)Manifest.displayPointCount * sizeof(float))
                        throw new InvalidDataException("Invalid field file length.");
                    var values = new float[Manifest.displayPointCount];
                    Buffer.BlockCopy(data, 0, values, 0, data.Length);
                    if (displayValues.count != values.Length)
                    {
                        var replacement = new ComputeBuffer(values.Length, sizeof(float));
                        displayValues.Release();
                        displayValues = replacement;
                        points.SetBuffer("_DisplayValues", displayValues);
                    }
                    displayValues.SetData(values);
                }
                selectedField = index;
                fieldError = null;
            }
            catch (Exception e)
            {
                fieldError = $"Could not load {fieldOptions[index].name}: {e.Message}";
                Debug.LogWarning(fieldError);
            }
        }

        GameObject CreateMesh(string folder,string file,Material material,bool visible,bool collide=true)
        {
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(folder,file)));
            if (new string(reader.ReadChars(4))!="LM01") throw new InvalidDataException(file);
            int n=reader.ReadInt32(),m=reader.ReadInt32();
            var vertices = new Vector3[n]; var indices = new int[m];
            for(int i=0;i<n;i++) vertices[i]=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
            for(int i=0;i<m;i++) indices[i]=reader.ReadInt32();
            var mesh = new Mesh { name=file, indexFormat=IndexFormat.UInt32 };
            mesh.vertices=vertices;mesh.triangles=indices;mesh.RecalculateNormals();mesh.RecalculateBounds();meshes.Add(mesh);
            var go = new GameObject(file);go.transform.SetParent(transform,false);
            go.AddComponent<MeshFilter>().sharedMesh=mesh;
            var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;renderer.enabled=visible;
            var collider=go.AddComponent<MeshCollider>();collider.sharedMesh=mesh;collider.enabled=collide;Colliders.Add(collider);
            return go;
        }

        public void SetObstacleCollision(bool enabled)
        {
            enableObstacleCollision=enabled;appliedObstacleCollision=enabled;
            appliedSlimMode=useSlimObstacleCollision;appliedRadiusScale=slimRadiusScale;
            // Index zero is always the DEM; visual toggles never disable its collider.
            for(int i=1;i<Colliders.Count;i++)Colliders[i].enabled=enabled&&!useSlimObstacleCollision;
            for(int i=0;i<SlimColliders.Count;i++)
            {
                SlimColliders[i].radius=slimRadii[i]*slimRadiusScale;
                SlimColliders[i].enabled=enabled&&useSlimObstacleCollision;
                var debug=slimRenderers[i].transform;
                debug.localScale=new Vector3(SlimColliders[i].radius*2,SlimColliders[i].height*.5f,SlimColliders[i].radius*2);
            }
            Physics.SyncTransforms();
        }

        void LoadSlimObstacles(string folder)
        {
            if(string.IsNullOrEmpty(Manifest.slimCollisionFile)||!File.Exists(Path.Combine(folder,Manifest.slimCollisionFile)))
            {
                if(useSlimObstacleCollision){enableObstacleCollision=false;Debug.LogWarning("Export has no slim colliders; using DEM only. Regenerate the export for tight tree collisions.");}
                return;
            }
            var data=JsonUtility.FromJson<SlimObstacleFile>(File.ReadAllText(Path.Combine(folder,Manifest.slimCollisionFile)));
            if(data.version!=1)throw new InvalidDataException("Unsupported slim collision file.");
            foreach(var obstacle in data.colliders)
            {
                var go=new GameObject("Tight trunk / vertical obstacle");go.transform.SetParent(transform,false);
                // Physical obstacles use a separate layer, so the camera and ground
                // probes can follow the DEM without being pushed around by trunks.
                go.layer=9;go.transform.localPosition=new Vector3(obstacle.x,obstacle.y+obstacle.height*.5f,obstacle.z);
                var collider=go.AddComponent<CapsuleCollider>();collider.radius=obstacle.radius;
                collider.height=obstacle.height;collider.contactOffset=.003f;
                SlimColliders.Add(collider);slimRadii.Add(obstacle.radius);
                var visual=GameObject.CreatePrimitive(PrimitiveType.Cylinder);visual.name="Tight collision preview";
                visual.transform.SetParent(go.transform,false);visual.layer=9;
                var primitiveCollider=visual.GetComponent<Collider>();primitiveCollider.enabled=false;Destroy(primitiveCollider);
                var renderer=visual.GetComponent<MeshRenderer>();renderer.sharedMaterial=obstacleMaterial;renderer.enabled=false;
                slimRenderers.Add(renderer);
            }
        }

        void Update()
        {
            if (!sceneReady) return;
            frameSeconds += Time.unscaledDeltaTime;
            frameCount++;
            if (frameSeconds >= .5f) { fps = frameCount / frameSeconds; frameCount = 0; frameSeconds = 0; }
            if(appliedObstacleCollision!=enableObstacleCollision||appliedSlimMode!=useSlimObstacleCollision||appliedRadiusScale!=slimRadiusScale)SetObstacleCollision(enableObstacleCollision);
            if (IntroActive)
            {
                revealElapsed += Time.unscaledDeltaTime;
                float available = streamer == null || streamer.TargetPoints == 0 || revealElapsed > 8
                    ? 1 : (float)streamer.RenderedPoints / streamer.TargetPoints;
                reveal = Input.GetKeyDown(KeyCode.Escape) ? 1 :
                    Mathf.MoveTowards(reveal, Mathf.Clamp(available, .08f, 1), Time.unscaledDeltaTime / 2.4f);
                introProgress = Mathf.Max(introProgress, Mathf.Lerp(PreparationShare, 1, reveal));
            }
            if(mkGlow)mkGlow.bloomIntensity.value=bloomStrength * Mathf.SmoothStep(0, 1, reveal);
            if(Input.GetKeyDown(KeyCode.G)) showGroundPoints=!showGroundPoints;
            if(Input.GetKeyDown(KeyCode.T)) showGroundSurface=!showGroundSurface;
            if(Input.GetKeyDown(KeyCode.C)) showCollision=!showCollision;
            if(Input.GetKeyDown(KeyCode.F)) SelectField((selectedField+1)%fieldOptions.Count);
            if(Input.GetKeyDown(KeyCode.P)) SelectPalette((selectedPalette+1)%PaletteNames.Length);
            groundRenderer.enabled=showGroundSurface;
            foreach(var r in obstacleRenderers) r.enabled=showCollision&&!useSlimObstacleCollision;
            foreach(var r in slimRenderers) r.enabled=showCollision&&useSlimObstacleCollision;
            points.SetFloat("_PointRadiusPixels",pointRadiusPixels);points.SetFloat("_Emission",emission);
            points.SetFloat("_Reveal", reveal);
            points.SetTexture("_Palette",paletteAtlas);
            points.SetFloat("_PaletteRow",(selectedPalette+.5f)/PaletteNames.Length);
            points.SetFloat("_FieldGamma",fieldGamma);points.SetFloat("_FieldMin",Manifest.fieldMin);
            points.SetFloat("_FieldMax",Manifest.fieldMax);
            points.SetInt("_FieldSource",(int)fieldOptions[selectedField].source);
            points.SetInt("_LegacyIntensityRange",Manifest.field == "intensity" ? 1 : 0);
            points.SetInt("_ShowGround",showGroundPoints?1:0);
            if (explorer)
            {
                Color tint = fieldOptions[selectedField].source == FieldSource.Rgb
                    ? new Color(.90f, .92f, .94f)
                    : Color.Lerp(new Color(.94f, .94f, .96f),
                        paletteAtlas.GetPixel(paletteAtlas.width * 2 / 3, selectedPalette), .08f);
                explorer.SetAvatarLighting(tint);
            }
            if (streamer != null)
            {
                streamer.PointBudget = displayPointBudget;
                streamer.MemoryBudgetMB = streamMemoryMB;
                streamer.NearDistance = nearDetailDistance;
                streamer.ViewDistance = renderDistance;
                streamer.Tick(Camera.main, gameObject.layer);
            }
            for(int i=0;i<buffers.Count;i++)
            {
                Graphics.DrawProcedural(points,bounds,MeshTopology.Triangles,6,counts[i],null,properties[i],ShadowCastingMode.Off,false,gameObject.layer);
            }
        }

        void OnGUI()
        {
            if (!sceneReady || IntroActive)
            {
                DrawIntro();
                return;
            }
            if (watermark)
            {
                Color previous = GUI.color;
                GUI.color = new Color(1, 1, 1, .5f);
                GUI.DrawTexture(new Rect(12, Screen.height - watermark.height*.5f - 12,
                    watermark.width*.5f, watermark.height*.5f), watermark);
                GUI.color = previous;
            }
            if(error!=null) GUI.Box(new Rect(12,12,850,70),"LiDAR load failed: "+error);
            if (showPerformance && Manifest != null)
            {
                Rect stats = new Rect(Screen.width - 382, 12, 370, 58);
                DrawPanelBackground(stats);
                GUI.Label(new Rect(stats.x + 10, stats.y + 5, stats.width - 20, stats.height - 10),
                    $"{fps:0} FPS  ·  Drawn {RenderedPointCount:N0}\n" +
                    (streamer == null ? $"Point memory {ResidentPointBytes / 1048576f:0} MB" :
                    $"Cache {ResidentPointBytes / 1048576f:0}/{streamMemoryMB} MB  ·  Queued {streamer.PendingLoads}"));
            }
            if (!SettingsOpen) { openDropdown = 0; return; }
            if(Manifest==null || fieldOptions.Count==0) return;
            DrawPanelBackground(PanelRect);
            GUI.ModalWindow(0, PanelRect, DrawSettings, "Settings · Esc to resume");
        }

        void DrawIntro()
        {
            bool splash = !sceneReady || reveal < .04f;
            if (splash) GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.blackTexture);
            float width = Mathf.Min(360, Screen.width * .7f);
            var track = new Rect((Screen.width - width) * .5f, Screen.height * .5f - 5, width, 10);
            if (error != null)
            {
                GUI.Box(new Rect(track.x, track.y - 25, track.width, 60), error);
                return;
            }
            Color previous = GUI.color;
            GUI.color = new Color(.20f, .22f, .25f);
            GUI.DrawTexture(track, Texture2D.whiteTexture);
            GUI.color = new Color(.82f, .89f, .97f);
            var fill = new Rect(track.x + 2, track.y + 2, (track.width - 4) * introProgress, track.height - 4);
            GUI.DrawTexture(fill, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        static void DrawPanelBackground(Rect rect)
        {
            Color previous = GUI.color;
            GUI.color = new Color(.025f, .03f, .04f, .97f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        void DrawSettings(int windowId)
        {
            Rect panel = PanelRect;
            GUILayout.BeginArea(new Rect(12, 28, panel.width-24, panel.height-40));
            panelScroll = GUILayout.BeginScrollView(panelScroll);
            GUILayout.Label($"Unity LiDAR Viewer | {Manifest.displayPointCount:N0} points");
            showPerformance = GUILayout.Toggle(showPerformance, "Show performance counters");
            if (streamer != null)
            {
                GUILayout.Label($"Drawn {streamer.RenderedPoints:N0} · Resident {streamer.ResidentPoints:N0} · {fps:0} FPS");
                GUILayout.Label($"Point budget {displayPointBudget / 1000000f:0.00} million");
                displayPointBudget = Mathf.RoundToInt(GUILayout.HorizontalSlider(displayPointBudget / 1000000f, .5f, 10) * 1000000);
                GUILayout.Label($"Point cache {streamMemoryMB} MB");
                streamMemoryMB = Mathf.RoundToInt(GUILayout.HorizontalSlider(streamMemoryMB, 128, 2048) / 16) * 16;
                GUILayout.Label($"Full detail within {nearDetailDistance:0} m · View distance {renderDistance:0} m");
                nearDetailDistance = GUILayout.HorizontalSlider(nearDetailDistance, 5, 60);
                renderDistance = GUILayout.HorizontalSlider(renderDistance, 60, 600);
                if (streamer.Error != null) GUILayout.Label(streamer.Error);
            }
            DrawDropdown(1, $"Field: {fieldOptions[selectedField].name}", fieldOptions.Count,
                i => fieldOptions[i].name, SelectField);
            bool rgb = fieldOptions[selectedField].source == FieldSource.Rgb;
            GUI.enabled = !rgb;
            DrawDropdown(2, rgb ? "Palette: source RGB" : $"Palette: {PaletteNames[selectedPalette]}",
                PaletteNames.Length, i => PaletteNames[i], SelectPalette);
            GUI.enabled = true;
            if (fieldError != null) GUILayout.Label(fieldError);
            GUILayout.Label("WASD move · Shift run · Space jump · V first/third person");
            GUILayout.Label("Mouse look · Esc settings / resume · R respawn");
            GUILayout.Label("P next palette · F next field · C collisions");
            GUILayout.Label("G ground points · T DEM surface (collision stays active)");
            enableObstacleCollision=GUILayout.Toggle(enableObstacleCollision,"Tight tree / solid-object collision");
            GUILayout.Label($"Point radius: {pointRadiusPixels:0.0} px");
            pointRadiusPixels=GUILayout.HorizontalSlider(pointRadiusPixels,.5f,4f);
            GUILayout.Label("Emission");emission=GUILayout.HorizontalSlider(emission,0,12);
            GUILayout.Label("Intensity contrast");fieldGamma=GUILayout.HorizontalSlider(fieldGamma,.3f,3);
            GUILayout.Label("MK Glow Lite bloom");bloomStrength=GUILayout.HorizontalSlider(bloomStrength,0,2);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawDropdown(int id, string label, int count, Func<int,string> name, Action<int> select)
        {
            if (GUILayout.Button(label + (openDropdown == id ? "  ▲" : "  ▼")))
            {
                openDropdown = openDropdown == id ? 0 : id;
                dropdownScroll = Vector2.zero;
                GUIUtility.ExitGUI();
            }
            if (openDropdown != id) return;
            dropdownScroll = GUILayout.BeginScrollView(dropdownScroll, GUILayout.Height(115));
            for (int i = 0; i < count; i++)
            {
                if (!GUILayout.Button(name(i))) continue;
                select(i);
                openDropdown = 0;
                GUIUtility.ExitGUI();
            }
            GUILayout.EndScrollView();
        }

        void OnDestroy()
        {
            streamer?.Dispose();
            tilePlaceholder?.Release();
            foreach(var b in buffers)b.Release();
            displayValues?.Release();
            foreach(var m in meshes)Destroy(m);
            if(points)Destroy(points);if(groundMaterial)Destroy(groundMaterial);if(obstacleMaterial)Destroy(obstacleMaterial);
        }
    }
}
