using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;
using StarterAssets;
using MK.Glow.URP;

namespace UnityLidarViewer.Editor
{
    public static class ProjectTools
    {
        const string Starter="Assets/Starter Assets/Runtime/";
        const string Settings="Assets/Lidar/Settings/";
        static double deadline,readyAt;
        static int stage;
        static bool loaded;
        static Color[] bloomPixels;
        static double bloomDifference;
        static float originalPointRadius;

        [MenuItem("LiDAR/Create Walkthrough Scene")]
        public static void CreateScene()
        {
            ConfigurePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var root=new GameObject("LiDAR World").AddComponent<LidarWorld>();
            root.pointShader=Shader.Find("UnityLidarViewer/Points");
            root.playerPrefab=PrepareControllers();root.showGroundSurface=false;root.enableObstacleCollision=true;root.useSlimObstacleCollision=true;
            var volume=new GameObject("MK Glow Lite + tone mapping").AddComponent<Volume>();volume.isGlobal=true;
            volume.sharedProfile=CreateGlowProfile();root.glowVolume=volume;
            var sun=new GameObject("Sun").AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=1.3f;
            sun.transform.rotation=Quaternion.Euler(45,-30,0);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.45f,.5f,.55f);
            PlayerSettings.colorSpace=ColorSpace.Linear;
            PlayerSettings.defaultScreenWidth=1440;PlayerSettings.defaultScreenHeight=900;
            PlayerSettings.fullScreenMode=FullScreenMode.Windowed;PlayerSettings.resizableWindow=true;
            PlayerSettings.runInBackground=true;PlayerSettings.companyName="Local tools";PlayerSettings.productName="Unity LiDAR Viewer";
            var settings=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            var input=settings.FindProperty("activeInputHandler");if(input!=null){input.intValue=2;settings.ApplyModifiedPropertiesWithoutUndo();}
            IncludeShaders();
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(),"Assets/Scenes/UnityLidarViewer.unity");
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene("Assets/Scenes/UnityLidarViewer.unity",true)};
            AssetDatabase.SaveAssets();Debug.Log("LIDAR_SCENE_CREATED URP + Starter Assets controllers + Adventure Character + MK Glow Lite.");
        }

        static void ConfigurePipeline()
        {
            Directory.CreateDirectory(Settings);
            var renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Settings+"LidarRenderer.asset");
            if(!renderer){renderer=ScriptableObject.CreateInstance<UniversalRendererData>();AssetDatabase.CreateAsset(renderer,Settings+"LidarRenderer.asset");}
            renderer.postProcessData=AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
            if(!renderer.postProcessData)throw new Exception("URP post-processing resources are missing.");
            var glow=renderer.rendererFeatures.OfType<MKGlowLiteRendererFeature>().FirstOrDefault();
            if(!glow)
            {
                glow=ScriptableObject.CreateInstance<MKGlowLiteRendererFeature>();glow.name="MK Glow Lite";
                AssetDatabase.AddObjectToAsset(glow,renderer);renderer.rendererFeatures.Add(glow);
            }
            glow.workmode=MKGlowLiteRendererFeature.Workmode.PostProcessVolumes;glow.SetActive(true);
            var depthShading = renderer.rendererFeatures.OfType<FullScreenPassRendererFeature>()
                .FirstOrDefault(feature => feature.name == "LiDAR Depth Shading");
            if (!depthShading)
            {
                depthShading = ScriptableObject.CreateInstance<FullScreenPassRendererFeature>();
                depthShading.name = "LiDAR Depth Shading";
                AssetDatabase.AddObjectToAsset(depthShading, renderer);
                renderer.rendererFeatures.Add(depthShading);
            }
            const string depthMaterialPath = Settings + "LidarDepthShading.mat";
            var depthMaterial = AssetDatabase.LoadAssetAtPath<Material>(depthMaterialPath);
            if (!depthMaterial)
            {
                depthMaterial = new Material(Shader.Find("Hidden/UnityLidarViewer/DepthShading"));
                AssetDatabase.CreateAsset(depthMaterial, depthMaterialPath);
            }
            depthShading.injectionPoint = FullScreenPassRendererFeature.InjectionPoint.BeforeRenderingPostProcessing;
            depthShading.requirements = ScriptableRenderPassInput.Depth;
            depthShading.fetchColorBuffer = false;
            depthShading.passMaterial = depthMaterial;
            EditorUtility.SetDirty(depthShading);
            renderer.SetDirty();EditorUtility.SetDirty(renderer);
            var pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Settings+"LidarURP.asset");
            if(!pipeline){pipeline=UniversalRenderPipelineAsset.Create(renderer);AssetDatabase.CreateAsset(pipeline,Settings+"LidarURP.asset");}
            var serialized=new SerializedObject(pipeline);var renderers=serialized.FindProperty("m_RendererDataList");
            renderers.arraySize=1;renderers.GetArrayElementAtIndex(0).objectReferenceValue=renderer;
            serialized.FindProperty("m_DefaultRendererIndex").intValue=0;serialized.ApplyModifiedPropertiesWithoutUndo();
            pipeline.supportsHDR=true;pipeline.msaaSampleCount=1;pipeline.renderScale=1;
            pipeline.supportsCameraDepthTexture=true;pipeline.supportsCameraOpaqueTexture=true;
            GraphicsSettings.defaultRenderPipeline=pipeline;
            int quality=QualitySettings.GetQualityLevel();
            for(int i=0;i<QualitySettings.names.Length;i++){QualitySettings.SetQualityLevel(i,false);QualitySettings.renderPipeline=pipeline;}
            QualitySettings.SetQualityLevel(quality,false);
            var globals=typeof(UniversalRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineGlobalSettings");
            var ensure=globals?.GetMethod("Ensure",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);
            if(ensure!=null)ensure.Invoke(null,ensure.GetParameters().Select(p=>p.HasDefaultValue?p.DefaultValue:(object)true).ToArray());
            EditorUtility.SetDirty(pipeline);AssetDatabase.SaveAssets();
        }

        static VolumeProfile CreateGlowProfile()
        {
            var profile=AssetDatabase.LoadAssetAtPath<VolumeProfile>(Settings+"LidarGlow.asset");
            if(!profile){profile=ScriptableObject.CreateInstance<VolumeProfile>();AssetDatabase.CreateAsset(profile,Settings+"LidarGlow.asset");}
            if(!profile.TryGet<MKGlowLite>(out var glow)){glow=profile.Add<MKGlowLite>(true);AssetDatabase.AddObjectToAsset(glow,profile);}
            glow.active=true;glow.bloomIntensity.Override(0);glow.bloomScattering.Override(6);
            // MK's inspector initializes a newly added component by zeroing bloom.
            // This profile is already configured, so preserve it on first inspection.
            glow.isInitialized.Override(true);
            glow.bloomThreshold.Override(new MK.Glow.MinMaxRange(.15f,10));glow.allowLensSurface.Override(false);
            if(!profile.TryGet<Tonemapping>(out var tone)){tone=profile.Add<Tonemapping>(true);AssetDatabase.AddObjectToAsset(tone,profile);}
            tone.mode.Override(TonemappingMode.Neutral);
            if(!profile.TryGet<ColorAdjustments>(out var color)){color=profile.Add<ColorAdjustments>(true);AssetDatabase.AddObjectToAsset(color,profile);}
            color.postExposure.Override(-.75f);
            EditorUtility.SetDirty(profile);EditorUtility.SetDirty(glow);EditorUtility.SetDirty(tone);EditorUtility.SetDirty(color);return profile;
        }

        static GameObject PrepareControllers()
        {
            var go=new GameObject("Starter Assets first and third person");go.SetActive(false);
            var explorer=go.AddComponent<Explorer>();
            explorer.thirdPersonPrefab=PrepareAdventurePlayer();
            explorer.firstPersonPrefab=RequiredPrefab(Starter+"FirstPersonController/Prefabs/PlayerCapsule.prefab");
            explorer.mainCameraPrefab=RequiredPrefab(Starter+"ThirdPersonController/Prefabs/MainCamera.prefab");
            explorer.thirdCameraPrefab=RequiredPrefab(Starter+"ThirdPersonController/Prefabs/PlayerFollowCamera.prefab");
            explorer.firstCameraPrefab=RequiredPrefab(Starter+"FirstPersonController/Prefabs/PlayerFollowCamera.prefab");
            Directory.CreateDirectory("Assets/Lidar/Prefabs");
            var result=PrefabUtility.SaveAsPrefabAsset(go,"Assets/Lidar/Prefabs/Explorer.prefab");UnityEngine.Object.DestroyImmediate(go);return result;
        }

        static GameObject RequiredPrefab(string path)=>AssetDatabase.LoadAssetAtPath<GameObject>(path)??throw new Exception("Missing vendor prefab: "+path);

        static GameObject PrepareAdventurePlayer()
        {
            const string path = "Assets/Lidar/Prefabs/AdventurePlayer.prefab";
            var player = (GameObject)PrefabUtility.InstantiatePrefab(RequiredPrefab(Starter+"ThirdPersonController/Prefabs/PlayerArmature.prefab"));
            var outfit = (GameObject)PrefabUtility.InstantiatePrefab(RequiredPrefab("Assets/Adventure_Character/Prefabs/Man_04.prefab"));
            try
            {
                PrefabUtility.UnpackPrefabInstance(player, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                PrefabUtility.UnpackPrefabInstance(outfit, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                var cameraTarget = player.GetComponent<ThirdPersonController>().CinemachineCameraTarget.transform;
                foreach (var child in player.transform.Cast<Transform>().ToArray())
                    if (child != cameraTarget && !cameraTarget.IsChildOf(child)) UnityEngine.Object.DestroyImmediate(child.gameObject);
                var animator = player.GetComponent<Animator>();
                animator.avatar = outfit.GetComponent<Animator>().avatar;
                animator.applyRootMotion = false;
                if (!animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman)
                    throw new InvalidOperationException("Adventure Character needs a valid humanoid rig.");
                foreach (var child in outfit.transform.Cast<Transform>().ToArray())
                    child.SetParent(player.transform, false);
                foreach (var skin in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!skin.gameObject.activeInHierarchy || skin.name is "Man_Cloth_Face_Mesh" or "Man_Bag_Mesh")
                    {
                        UnityEngine.Object.DestroyImmediate(skin.gameObject);
                        continue;
                    }
                    skin.sharedMaterials = skin.sharedMaterials.Select(material => AdventureMaterial(material, skin.name)).ToArray();
                }
                AddAdventureBackpack(player.transform);
                player.name = "Adventure Character";
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                return PrefabUtility.SaveAsPrefabAsset(player, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(outfit);
                UnityEngine.Object.DestroyImmediate(player);
            }
        }

        static Material AdventureMaterial(Material source, string mesh)
        {
            if (!source) return null;
            string dye = mesh switch
            {
                "Man_Jacket_Mesh" => "DarkOrangeJacket",
                "Man_Pants_Mesh" => "KhakiPants",
                _ => null
            };
            const string folder = "Assets/Lidar/Settings/AdventureMaterials/";
            Directory.CreateDirectory(folder);
            string path = folder + (dye ?? source.name) + ".mat";
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (!shader) throw new InvalidOperationException("URP Lit is required for Adventure Character.");
            var result = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!result)
            {
                result = new Material(shader);
                AssetDatabase.CreateAsset(result, path);
            }
            var albedo = dye == null ? source.GetTexture("_MainTex") :
                AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Lidar/Textures/Adventure/" + dye + ".png");
            if (!albedo) throw new InvalidOperationException("Missing Adventure albedo: " + (dye ?? source.name));
            result.SetTexture("_BaseMap", albedo);
            result.SetColor("_BaseColor", source.GetColor("_Color"));
            var normal = source.GetTexture("_BumpMap");
            result.SetTexture("_BumpMap", normal);
            if (normal) result.EnableKeyword("_NORMALMAP");
            else result.DisableKeyword("_NORMALMAP");
            result.SetTexture("_OcclusionMap", source.GetTexture("_OcclusionMap"));
            result.SetFloat("_Smoothness", .22f);
            result.SetFloat("_Metallic", 0);
            EditorUtility.SetDirty(result);
            return result;
        }

        static void AddAdventureBackpack(Transform player)
        {
            var chest = player.GetComponentsInChildren<Transform>().First(t => t.name == "spine_03");
            var pack = new GameObject("Dark gray hiking backpack").transform;
            Vector3 origin = player.InverseTransformPoint(chest.position);
            pack.SetPositionAndRotation(player.TransformPoint(new Vector3(0, origin.y - .09f, -.19f)), player.rotation);
            pack.SetParent(chest, true);

            const string folder = "Assets/Lidar/Settings/AdventureMaterials/";
            const string meshPath = folder + "RoundedPack.asset";
            var shape = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (!shape)
            {
                var primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                shape = UnityEngine.Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
                UnityEngine.Object.DestroyImmediate(primitive);
                float Round(float value) => .5f * Mathf.Sign(value) * Mathf.Pow(Mathf.Abs(value) * 2, .52f);
                shape.vertices = shape.vertices.Select(v => new Vector3(Round(v.x), Round(v.y), Round(v.z))).ToArray();
                shape.RecalculateNormals();
                shape.RecalculateBounds();
                AssetDatabase.CreateAsset(shape, meshPath);
            }

            Material Cloth(string name, string hex)
            {
                string path = folder + name + ".mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!material)
                {
                    material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    AssetDatabase.CreateAsset(material, path);
                }
                ColorUtility.TryParseHtmlString(hex, out var color);
                material.SetColor("_BaseColor", color);
                material.SetFloat("_Smoothness", .12f);
                EditorUtility.SetDirty(material);
                return material;
            }
            void Panel(string name, Vector3 offset, Vector3 size, Material material)
            {
                var piece = new GameObject(name);
                piece.transform.SetPositionAndRotation(pack.position + player.TransformVector(offset), player.rotation);
                piece.transform.localScale = size;
                piece.transform.SetParent(pack, true);
                piece.AddComponent<MeshFilter>().sharedMesh = shape;
                piece.AddComponent<MeshRenderer>().sharedMaterial = material;
            }
            var canvas = Cloth("PackCanvas", "#4E5155");
            var trim = Cloth("PackTrim", "#303337");
            Panel("Main compartment", Vector3.zero, new Vector3(.31f, .40f, .19f), canvas);
            Panel("Outer pocket", new Vector3(0, -.075f, -.095f), new Vector3(.26f, .22f, .075f), trim);
            Panel("Pocket zipper", new Vector3(0, .008f, -.126f), new Vector3(.20f, .014f, .021f), canvas);
            foreach (float side in new[] { -.10f, .10f })
            {
                Panel("Shoulder strap top", new Vector3(side, .156f, .115f), new Vector3(.045f, .04f, .23f), trim);
                Panel("Shoulder strap front", new Vector3(side, .012f, .21f), new Vector3(.04f, .31f, .035f), trim);
            }
        }

        static Texture2D ConfigurePalette(string name)
        {
            string path=$"Assets/Lidar/Palettes/{name}.png";var importer=(TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType=TextureImporterType.Default;importer.sRGBTexture=true;importer.mipmapEnabled=false;
            importer.filterMode=FilterMode.Bilinear;importer.wrapMode=TextureWrapMode.Clamp;
            importer.textureCompression=TextureImporterCompression.Uncompressed;importer.npotScale=TextureImporterNPOTScale.None;
            importer.SaveAndReimport();return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static void IncludeShaders()
        {
            var graphics=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var shaders=graphics.FindProperty("m_AlwaysIncludedShaders");
            for(int i=shaders.arraySize-1;i>=0;i--)
            {
                var s=shaders.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if(!s||s.name=="UnityLidarViewer/Bloom"||s.name=="Standard")shaders.DeleteArrayElementAtIndex(i);
            }
            foreach(string name in new[]{"UnityLidarViewer/Points","Hidden/MK/Glow/URP/Copy"})
            {
                var shader=Shader.Find(name);if(!shader)throw new Exception("Missing shader: "+name);
                bool found=false;for(int i=0;i<shaders.arraySize;i++)if(shaders.GetArrayElementAtIndex(i).objectReferenceValue==shader)found=true;
                if(!found){int index=shaders.arraySize;shaders.InsertArrayElementAtIndex(index);shaders.GetArrayElementAtIndex(index).objectReferenceValue=shader;}
            }
            graphics.ApplyModifiedPropertiesWithoutUndo();
        }

        public static void SmokeTest()
        {
            const string scene="Assets/Scenes/UnityLidarViewer.unity";
            if(File.Exists(scene))EditorSceneManager.OpenScene(scene);else CreateScene();
            SessionState.SetBool("LidarSmokeTest",true);EditorApplication.isPlaying=true;
        }

        [InitializeOnLoadMethod]static void ResumeSmokeTest()
        {
            if(!SessionState.GetBool("LidarSmokeTest",false))return;
            deadline=EditorApplication.timeSinceStartup+150;EditorApplication.update+=Tick;
        }

        static void Tick()
        {
            if(EditorApplication.timeSinceStartup>deadline){Finish(false,"Timed out loading/testing vendor integration.");return;}
            if(!EditorApplication.isPlaying)return;
            var world=UnityEngine.Object.FindFirstObjectByType<LidarWorld>();var explorer=UnityEngine.Object.FindFirstObjectByType<Explorer>();
            if(!world||world.Manifest==null||!explorer||!explorer.CurrentBody||!explorer.View)return;
            if(!loaded){loaded=true;readyAt=EditorApplication.timeSinceStartup+8;}
            if(EditorApplication.timeSinceStartup<readyAt)return;
            string dir=Path.GetFullPath("../Artifacts");Directory.CreateDirectory(dir);
            try
            {
                if(stage==0)
                {
                    if(!explorer.ThirdPerson)
                    {
                        VerifyPlayer(explorer,false);
                        explorer.SetThirdPerson(true);
                        readyAt=EditorApplication.timeSinceStartup+2;
                        return;
                    }
                    VerifyPlayer(explorer,true);
                    if(!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset))throw new Exception("URP is not active.");
                    var shader=Shader.Find("UnityLidarViewer/Points");if(!shader.isSupported||ShaderUtil.ShaderHasError(shader))throw new Exception("Point shader invalid.");
                    if(!world.glowVolume.profile.TryGet<MKGlowLite>(out var glow))throw new Exception("MK Glow Lite volume is missing.");
                    if(UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include,FindObjectsSortMode.None).Any(b=>b&&(b.GetType().Name=="HikerOutfit"||b.GetType().Name=="ShepherdCompanion"||b.GetType().Name=="SimpleBloom")))throw new Exception("Removed custom content remains active.");
                    if(world.Colliders.Count!=world.Manifest.collisionFiles.Length+1)throw new Exception("Collider count mismatch.");
                    VerifyMinimalCollision(world);
                    // Freeze the idle pose so the bloom comparison cannot pass due
                    // to character animation instead of an actual image effect.
                    explorer.CharacterAnimator.speed=0;
                    world.SelectField("intensity");
                    world.emission = 4;
                    Capture(explorer.View,Path.Combine(dir,"preview.png"));
                    world.bloomStrength=0;world.SendMessage("Update");stage=1;readyAt=EditorApplication.timeSinceStartup+.4;
                }
                else if(stage==1)
                {
                    bloomPixels=Capture(explorer.View,Path.Combine(dir,"mk-glow-off.png"));
                    originalPointRadius=world.pointRadiusPixels;
                    world.pointRadiusPixels=originalPointRadius<2.5f?4f:.5f;
                    stage=5;readyAt=EditorApplication.timeSinceStartup+.4;
                }
                else if(stage==5)
                {
                    var resized=Capture(explorer.View,null);
                    var radiusDifference=resized.Zip(bloomPixels,(a,b)=>Math.Abs(a.r-b.r)+Math.Abs(a.g-b.g)+Math.Abs(a.b-b.b))
                        .Where((value,index)=>index%1440<450||index%1440>980).Average()/3;
                    if(radiusDifference<.01)throw new Exception("Point radius did not visibly change the render.");
                    Debug.Log($"POINT_RADIUS_PASS mean RGB difference={radiusDifference:F5}");
                    world.pointRadiusPixels=originalPointRadius;
                    world.bloomStrength=.65f;stage=2;readyAt=EditorApplication.timeSinceStartup+.4;
                }
                else if(stage==2)
                {
                    var on=Capture(explorer.View,Path.Combine(dir,"mk-glow-on.png"));
                    bloomDifference=on.Zip(bloomPixels,(a,b)=>(Math.Abs(a.r-b.r)+Math.Abs(a.g-b.g)+Math.Abs(a.b-b.b))/3)
                        .Where((v,i)=>i%1440<450||i%1440>980).Average();
                    if(bloomDifference<.001)throw new Exception("MK Glow on/off render difference too small: "+bloomDifference);
                    Vector3 before=explorer.CurrentPlayer.transform.position;explorer.SetThirdPerson(false);
                    if(Vector3.Distance(before,explorer.CurrentPlayer.transform.position)>.01f)throw new Exception("Camera mode switch teleported player.");
                    stage=3;readyAt=EditorApplication.timeSinceStartup+2;
                }
                else if(stage==3)
                {
                    VerifyPlayer(explorer,false);Capture(explorer.View,Path.Combine(dir,"preview-magma-first-person.png"));
                    world.SelectPalette(1);world.SendMessage("Update");Capture(explorer.View,Path.Combine(dir,"preview-viridis-first-person.png"));
                    if (world.Manifest.displayFields != null && !world.SelectField("classification"))
                        throw new Exception("Could not select the exported classification field.");
                    explorer.SetThirdPerson(true);stage=4;readyAt=EditorApplication.timeSinceStartup+2;
                }
                else
                {
                    VerifyPlayer(explorer,true);
                    Finish(true,$"Both controllers grounded; DEM solid; {world.SlimColliders.Count} tight near-ground colliders active; trunk blocks capsule at close range and can be passed closely on either side; full voxel collision disabled; MK Glow difference={bloomDifference:F5}.");
                }
            }
            catch(Exception e){Finish(false,e.ToString());}
        }

        static void VerifyPlayer(Explorer explorer,bool third)
        {
            if(explorer.ThirdPerson!=third||!explorer.CurrentBody.isGrounded)throw new Exception($"Vendor {(third?"third":"first")}-person controller not grounded.");
            if(third&&!explorer.CurrentPlayer.GetComponent<ThirdPersonController>())throw new Exception("Missing vendor ThirdPersonController.");
            if(!third&&!explorer.CurrentPlayer.GetComponent<FirstPersonController>())throw new Exception("Missing vendor FirstPersonController.");
            var input=explorer.CurrentPlayer.GetComponent<PlayerInput>();if(!input||!input.actions||!input.actions.Any())throw new Exception("Vendor Input System actions missing.");
            foreach(var r in explorer.CurrentPlayer.GetComponentsInChildren<Renderer>())foreach(var m in r.sharedMaterials)
                if(!m||!m.shader||!m.shader.isSupported||m.shader.name=="Hidden/InternalErrorShader"||ShaderUtil.ShaderHasError(m.shader))throw new Exception("Invalid vendor material: "+r.name);
        }

        static Color[] Capture(Camera camera,string path)
        {
            const int width=1440,height=900;var rt=new RenderTexture(width,height,24,RenderTextureFormat.ARGBHalf);rt.Create();
            var active=RenderTexture.active;
            // StandardRequest runs the complete camera/volume update path. A bare
            // SingleCameraRequest bypasses volume updates in a headless editor.
            RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest{destination=rt});RenderTexture.active=rt;
            var image=new Texture2D(width,height,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,width,height),0,0);image.Apply();
            var pixels=image.GetPixels();if(QualitySettings.activeColorSpace==ColorSpace.Linear){for(int i=0;i<pixels.Length;i++)pixels[i]=pixels[i].gamma;image.SetPixels(pixels);image.Apply();}
            if(path!=null)File.WriteAllBytes(path,image.EncodeToPNG());
            RenderTexture.active=active;rt.Release();UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(image);return pixels;
        }

        static void VerifyMinimalCollision(LidarWorld world)
        {
            if(!world.enableObstacleCollision||!world.useSlimObstacleCollision||!world.Colliders[0].enabled||world.Colliders.Skip(1).Any(c=>c.enabled))
                throw new Exception("Default collision must use solid DEM plus slim obstacles, without full voxel collision.");
            if(world.SlimColliders.Count!=world.Manifest.slimCollisionCount||world.SlimColliders.Count==0||world.SlimColliders.Any(c=>!c.enabled))
                throw new Exception("Slim obstacle colliders were not loaded/enabled.");
            foreach(var tree in world.SlimColliders)
            {
                Vector3 centre=tree.transform.position;
                if(world.SlimColliders.Any(other=>other!=tree&&Vector2.Distance(new Vector2(centre.x,centre.z),new Vector2(other.transform.position.x,other.transform.position.z))<2))continue;
                var probe=new GameObject("Tight trunk clearance probe");
                var capsule=probe.AddComponent<CharacterController>();capsule.height=1.8f;capsule.radius=.18f;
                capsule.center=Vector3.up*.9f;capsule.skinWidth=.018f;capsule.contactOffset=.003f;capsule.minMoveDistance=0;capsule.stepOffset=0;
                float distance=tree.radius+.18f+.25f;
                try
                {
                    capsule.enabled=false;probe.transform.position=centre-Vector3.right*distance-Vector3.up*.1f;capsule.enabled=true;Physics.SyncTransforms();
                    capsule.Move(Vector3.right*distance*2);
                    float separation=centre.x-probe.transform.position.x;
                    if(separation<.06f||separation>tree.radius+.18f+.06f)throw new Exception("Trunk did not block at the expected close clearance.");
                    foreach(int side in new[]{-1,1})
                    {
                        capsule.enabled=false;probe.transform.position=centre-Vector3.right*distance+Vector3.forward*(tree.radius+.18f+.04f)*side-Vector3.up*.1f;
                        capsule.enabled=true;Physics.SyncTransforms();capsule.Move(Vector3.right*distance*2);
                        if(probe.transform.position.x<centre.x+distance-.05f)throw new Exception("Capsule could not pass beside trunk with 4 cm clearance.");
                    }
                    world.SetObstacleCollision(false);
                    if(world.SlimColliders.Any(c=>c.enabled)||!world.Colliders[0].enabled)throw new Exception("Obstacle toggle changed ground collision.");
                    world.SetObstacleCollision(true);return;
                }
                finally{UnityEngine.Object.DestroyImmediate(probe);}
            }
            throw new Exception("No isolated slim trunk available for clearance verification.");
        }

        static void Finish(bool ok,string details)
        {
            EditorApplication.update-=Tick;SessionState.SetBool("LidarSmokeTest",false);
            Directory.CreateDirectory("../Artifacts");File.WriteAllText("../Artifacts/unity-smoke-test.txt",(ok?"PASS ":"FAIL ")+details);
            Debug.Log((ok?"LIDAR_SMOKE_PASS ":"LIDAR_SMOKE_FAIL ")+details);EditorApplication.Exit(ok?0:1);
        }

        [MenuItem("LiDAR/Build Windows Viewer")]
        public static void BuildWindows()
        {
            if(!File.Exists("Assets/Scenes/UnityLidarViewer.unity"))CreateScene();
            else ConfigurePipeline();
            const string explorerPath = "Assets/Lidar/Prefabs/Explorer.prefab";
            var adventure = PrepareAdventurePlayer();
            var explorer = PrefabUtility.LoadPrefabContents(explorerPath);
            try
            {
                explorer.GetComponent<Explorer>().thirdPersonPrefab = adventure;
                PrefabUtility.SaveAsPrefabAsset(explorer, explorerPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(explorer); }
            AssetDatabase.SaveAssets();
            string[] args = Environment.GetCommandLineArgs();
            int outputIndex = Array.IndexOf(args, "--build-output");
            if (outputIndex >= 0 && outputIndex + 1 >= args.Length) throw new ArgumentException("--build-output requires an executable path.");
            string path=Path.GetFullPath(outputIndex >= 0 ? args[outputIndex + 1] : "../Builds/Windows/UnityLidarViewer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string playerPath = Path.Combine(Path.GetDirectoryName(path), "Player", "UnityLidarViewer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(playerPath));
            var report=BuildPipeline.BuildPlayer(EditorBuildSettings.scenes,playerPath,BuildTarget.StandaloneWindows64,BuildOptions.None);
            if(report.summary.result!=UnityEditor.Build.Reporting.BuildResult.Succeeded)throw new Exception("Build failed: "+report.summary.result);
            string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET/Framework64/v4.0.30319/csc.exe");
            string launcherSource = Path.GetFullPath("Assets/Lidar/Editor/WindowsLauncher.cs.txt");
            using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                compiler, $"/nologo /target:exe /platform:x64 /out:\"{path}\" \"{launcherSource}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new Exception("Launcher build failed: " + output);
            }
            Debug.Log("LIDAR_BUILD_COMPLETE "+path);
        }
    }
}
