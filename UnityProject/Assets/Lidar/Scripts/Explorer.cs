using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using Unity.Cinemachine;
using StarterAssets;

namespace UnityLidarViewer
{
    // Switches between the actual vendor player and Cinemachine prefabs.
    // Input, look, movement, jumping and third-person animation remain vendor code.
    public sealed class Explorer : MonoBehaviour
    {
        public LidarWorld world;
        public GameObject thirdPersonPrefab,firstPersonPrefab,mainCameraPrefab,thirdCameraPrefab,firstCameraPrefab;
        public bool ThirdPerson { get; private set; }
        public GameObject CurrentPlayer => ThirdPerson?thirdPlayer:firstPlayer;
        public CharacterController CurrentBody => CurrentPlayer?CurrentPlayer.GetComponent<CharacterController>():null;
        public Animator CharacterAnimator => thirdPlayer?thirdPlayer.GetComponent<Animator>():null;
        public Camera View { get; private set; }
        GameObject thirdPlayer,firstPlayer;
        CinemachineCamera thirdCamera,firstCamera;
        Vector3 spawn;
        bool quitting;
        bool introLocked;
        Renderer[] avatarRenderers;
        Color avatarTint;
        MaterialPropertyBlock lightingProperties;
        internal const float HeightReduction = .33f;

        void Awake()
        {
            lightingProperties = new MaterialPropertyBlock();
            Application.wantsToQuit += PrepareToQuit;
        }

        bool PrepareToQuit()
        {
            // Release the pointer while the native player window still exists.
            quitting = true;
            SetCapture(false);
            return true;
        }

        void Start()
        {
            spawn=transform.position;
            var main=Instantiate(mainCameraPrefab,transform);main.name="Main Camera (Starter Assets)";
            View=main.GetComponent<Camera>();View.tag="MainCamera";View.allowHDR=true;
            View.nearClipPlane=.05f;View.farClipPlane=600;View.clearFlags=CameraClearFlags.SolidColor;View.backgroundColor=Color.black;
            View.GetUniversalAdditionalCameraData().renderPostProcessing=true;
            var brain=main.GetComponent<CinemachineBrain>();
            brain.DefaultBlend=new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.Cut,0);
            // PlayerInput pairs devices when enabled. Let the starting controller claim them first.
            firstPlayer=Instantiate(firstPersonPrefab,spawn,Quaternion.identity,transform);
            thirdPlayer=Instantiate(thirdPersonPrefab,spawn,Quaternion.identity,transform);
            thirdPlayer.SetActive(false);
            ConfigurePlayer(thirdPlayer);ConfigurePlayer(firstPlayer);
            avatarRenderers = thirdPlayer.GetComponentsInChildren<Renderer>(true);
            var third=thirdPlayer.GetComponent<ThirdPersonController>();third.GroundLayers=1;
            var first=firstPlayer.GetComponent<FirstPersonController>();first.GroundLayers=1;
            thirdCamera=Instantiate(thirdCameraPrefab,transform).GetComponent<CinemachineCamera>();
            firstCamera=Instantiate(firstCameraPrefab,transform).GetComponent<CinemachineCamera>();
            thirdCamera.Follow=third.CinemachineCameraTarget.transform;
            firstCamera.Follow=first.CinemachineCameraTarget.transform;
            foreach(var camera in new[]{thirdCamera,firstCamera})
            {
                camera.Lens.FieldOfView=65;camera.Lens.NearClipPlane=.05f;camera.Lens.FarClipPlane=600;
                var noise=camera.GetComponent<CinemachineBasicMultiChannelPerlin>();if(noise)noise.enabled=false;
            }
            thirdCamera.GetComponent<CinemachineThirdPersonFollow>().CameraDistance=3.5f;
            thirdCamera.gameObject.SetActive(false);
            introLocked = world.IntroActive;
            foreach (var renderer in avatarRenderers) renderer.forceRenderingOff = introLocked;
            Physics.SyncTransforms();SetCapture(!Application.isBatchMode && !introLocked);
            Debug.Log("STARTER_ASSETS_READY third-person + first-person controllers, PlayerInput and Cinemachine.");
        }

        static void ConfigurePlayer(GameObject player)
        {
            var body=player.GetComponent<CharacterController>();
            float scale = (body.height - HeightReduction) / body.height;
            // Keep the capsule's sole at the original height relative to the spawn.
            float originalBottom = body.center.y - body.height * .5f;
            player.transform.localScale *= scale;
            var center = body.center;
            center.y = originalBottom / scale + body.height * .5f;
            body.center = center;
            body.minMoveDistance=0;body.skinWidth=.018f / scale;
            body.radius=.18f / scale;body.contactOffset=.003f;body.stepOffset=.4f / scale;body.slopeLimit=60;
            foreach(var t in player.GetComponentsInChildren<Transform>(true))t.gameObject.layer=8;
            var animator=player.GetComponent<Animator>();if(animator)animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
            player.GetComponent<StarterAssetsInputs>().cursorLocked=false;
        }

        void Update()
        {
            if(!CurrentPlayer)return;
            if (introLocked)
            {
                if (world.IntroActive) return;
                introLocked = false;
                foreach (var renderer in avatarRenderers) renderer.forceRenderingOff = false;
                SetCapture(!Application.isBatchMode);
                return;
            }
            if(Input.GetKeyDown(KeyCode.Escape))SetSettingsOpen(!world.SettingsOpen);
            if(world.SettingsOpen)return;
            if(Input.GetKeyDown(KeyCode.V))SetThirdPerson(!ThirdPerson);
            if(Input.GetMouseButtonDown(0))SetCapture(true);
            if(Input.GetKeyDown(KeyCode.R)||CurrentPlayer.transform.position.y<world.Manifest.boundsMin[1]-30)
            {CurrentBody.enabled=false;CurrentPlayer.transform.position=spawn;CurrentBody.enabled=true;}
        }

        internal void SetAvatarLighting(Color tint)
        {
            if (avatarRenderers == null || avatarTint == tint) return;
            avatarTint = tint;
            foreach (var renderer in avatarRenderers)
            {
                var materials = renderer.sharedMaterials;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot];
                    if (!material || !material.HasProperty("_BaseColor")) continue;
                    renderer.GetPropertyBlock(lightingProperties, slot);
                    lightingProperties.SetColor("_BaseColor", material.GetColor("_BaseColor") * tint);
                    if (material.HasProperty("_Smoothness"))
                        lightingProperties.SetFloat("_Smoothness", Mathf.Min(.25f, material.GetFloat("_Smoothness")));
                    if (material.HasProperty("_Metallic"))
                        lightingProperties.SetFloat("_Metallic", Mathf.Min(.12f, material.GetFloat("_Metallic")));
                    renderer.SetPropertyBlock(lightingProperties, slot);
                }
            }
        }

        public void SetSettingsOpen(bool open)
        {
            world.SettingsOpen = open;
            SetCapture(!open);
        }

        internal void SetCapture(bool capture)
        {
            if(!CurrentPlayer)return;
            var controls=CurrentPlayer.GetComponent<StarterAssetsInputs>();controls.cursorLocked=capture;
            var input=CurrentPlayer.GetComponent<PlayerInput>();
            if(capture)input.ActivateInput();
            else{input.DeactivateInput();controls.MoveInput(Vector2.zero);controls.LookInput(Vector2.zero);controls.JumpInput(false);controls.SprintInput(false);}
            Cursor.lockState=capture?CursorLockMode.Locked:CursorLockMode.None;Cursor.visible=!capture;
        }

        public void SetThirdPerson(bool enabled)
        {
            if(enabled==ThirdPerson||!CurrentPlayer)return;
            Vector3 position=CurrentPlayer.transform.position;
            Quaternion rotation=Quaternion.Euler(0,View.transform.eulerAngles.y,0);
            bool capture=Cursor.lockState==CursorLockMode.Locked;
            SetCapture(false);CurrentPlayer.SetActive(false);
            ThirdPerson=enabled;
            CurrentPlayer.transform.SetPositionAndRotation(position,rotation);CurrentPlayer.SetActive(true);
            thirdCamera.gameObject.SetActive(enabled);firstCamera.gameObject.SetActive(!enabled);
            Physics.SyncTransforms();SetCapture(capture&&!Application.isBatchMode);
        }

        void OnDestroy()
        {
            Application.wantsToQuit -= PrepareToQuit;
            if (quitting) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
