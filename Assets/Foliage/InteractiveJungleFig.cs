using System.Collections.Generic;
using UnityEngine;

namespace Foliage
{
    // Attach to a scene object or drop a generator-created prefab into a scene.
    public sealed class InteractiveJungleFig : MonoBehaviour
    {
        public int seed=2;
        public FigSettings settings=new();
        public InteractiveFigLayout bakedLayout;
        [Tooltip("Use the surface under the trunk when a ground collider is present. Otherwise use the generator slope.")]
        public bool sampleGround=true;
        public LayerMask groundLayers=Physics.DefaultRaycastLayers;
        public Material bark;
        public Camera interactionCamera;
        readonly List<FigBranchInstance> crown=new();
        readonly List<MeshRenderer> boughRenderers=new();
        Material[] normalMaterials;
        bool debugVisible;
        public bool DebugVisible=>debugVisible;
        public void SetDebugVisible(bool visible){
            if(visible==debugVisible)return;
            debugVisible=visible;
            for(int i=0;i<crown.Count;i++){
                if(visible){
                    var material=crown[i].DebugMaterial(true);
                    boughRenderers[i].sharedMaterials=new[]{material,material};
                }else boughRenderers[i].sharedMaterials=normalMaterials;
            }
        }
        void LateUpdate(){
            if(debugVisible)foreach(var branch in crown)branch.DebugMaterial(true);
        }
        GameObject generated;
        Material leafMaterial;
        Mesh wood;
        bool ownsWood;
        MeshCollider trunk;
        FigBranchInstance grabbed;
        float depth;
        Vector3 grabOffset;
        Camera View=>interactionCamera?interactionCamera:Camera.main;

        Vector3 GroundGradient(){
            if(sampleGround){
                Vector3 up=transform.up;
                if(Physics.Raycast(transform.position+up*.5f,-up,out var hit,1f,groundLayers,QueryTriggerInteraction.Ignore)){
                    // Plane normals transform as covectors, including non-uniform object scale.
                    Vector3 normal=transform.localToWorldMatrix.transpose.MultiplyVector(hit.normal).normalized;
                    if(normal.y>.01f)return new Vector3(-normal.x,0,-normal.z)/normal.y;
                }
            }
            return settings.GroundGradient;
        }
        void OnEnable(){
            var gradient=GroundGradient();
            ownsWood=!bakedLayout || !bakedLayout.Matches(seed,settings,gradient);
            if(ownsWood)wood=FigBranchInstance.BuildTree(seed,settings,crown,gradient);
            else{
                wood=bakedLayout.wood;
                bakedLayout.BuildBranches(crown);
            }
            generated=new GameObject("Fig wood");
            generated.transform.SetParent(transform,false);
            generated.AddComponent<MeshFilter>().sharedMesh=wood;
            generated.AddComponent<MeshRenderer>().sharedMaterial=bark;
            trunk=generated.AddComponent<MeshCollider>();trunk.sharedMesh=wood;
            leafMaterial=new Material(Resources.Load<Shader>("FigLeaf"));
            normalMaterials=new[]{bark,leafMaterial};
            foreach(var b in crown){
                var go=new GameObject("Physical bough");
                go.transform.SetParent(generated.transform,false);go.transform.localPosition=b.shoot.origin;
                go.AddComponent<MeshFilter>().sharedMesh=b.mesh;
                var renderer=go.AddComponent<MeshRenderer>();
                renderer.sharedMaterials=normalMaterials;boughRenderers.Add(renderer);
            }
        }
        void OnDisable(){
            Release();
            if(generated){generated.SetActive(false);Destroy(generated);}
            foreach(var b in crown){b.Sleep();Destroy(b.mesh);}
            crown.Clear();boughRenderers.Clear();debugVisible=false;elapsed=0;
            if(ownsWood && wood)Destroy(wood);
            wood=null;
            if(leafMaterial)Destroy(leafMaterial);
        }
        void Release(){
            grabbed?.ReleaseGrab();
            grabbed=null;
        }
        void OnApplicationFocus(bool focus){if(!focus)Release();}
        [System.NonSerialized] public bool labClock,simulationPaused;
        float labTime;
        void Update(){
            // Under load keep the lab interactive and advance one calibrated tick per render.
            if(labClock && !simulationPaused){
                labTime+=Time.deltaTime;
                if(labTime>=1f/60){foreach(var b in crown)b.RuntimeTick();labTime%=1f/60;}
            }
            var cam=View;
            if(!cam){Release();return;}
            if(Input.GetMouseButtonUp(0))Release();
            if(Input.GetMouseButtonDown(0) && cam.pixelRect.Contains(Input.mousePosition))Pick(cam);
            if(grabbed!=null){
                Vector3 pixel=Input.mousePosition;pixel.z=depth;
                grabbed.solver.target=transform.InverseTransformPoint(cam.ScreenToWorldPoint(pixel))-grabbed.shoot.origin+grabOffset;
            }
        }
        void Pick(Camera cam){
            Release();
            Ray world=cam.ScreenPointToRay(Input.mousePosition);
            float limit=cam.farClipPlane;
            if(trunk.Raycast(world,out var hit,limit))limit=hit.distance;
            Vector3 direction=transform.InverseTransformVector(world.direction);
            float nearest=limit*direction.magnitude;
            Vector3 start=transform.InverseTransformPoint(world.origin);
            FigBranchInstance selected=null;int selectedTriangle=-1;Vector3 selectedWeights=default;
            foreach(var b in crown){
                var ray=new Ray(start-b.shoot.origin,direction.normalized);
                int triangle=b.PickSurface(ray,ref nearest,out var weights);
                if(triangle<0)continue;
                selected=b;selectedTriangle=triangle;selectedWeights=weights;
            }
            if(selectedTriangle<0)return;
            grabbed=selected;
            Vector3 local=grabbed.BeginGrab(selectedTriangle,selectedWeights,start+direction.normalized*nearest-grabbed.shoot.origin);
            // Keep the cursor on the picked surface while the solver pulls the rod centreline.
            Vector3 surface=start+direction.normalized*nearest;
            grabOffset=local+grabbed.shoot.origin-surface;
            depth=cam.WorldToScreenPoint(transform.TransformPoint(surface)).z;
        }
        void FixedUpdate(){
            // The solver uses its calibrated 60 Hz step independently of Unity's fixed interval.
            if(labClock || simulationPaused)return;
            elapsed+=Time.fixedDeltaTime;
            while(elapsed>=1f/60){foreach(var b in crown)b.RuntimeTick();elapsed-=1f/60;}
        }
        float elapsed;
    }
}
