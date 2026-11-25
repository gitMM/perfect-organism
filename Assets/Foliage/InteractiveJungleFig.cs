using System.Collections.Generic;
using UnityEngine;

namespace Foliage
{
    // Attach to a scene object or drop a generator-created prefab into a scene.
    public sealed class InteractiveJungleFig : MonoBehaviour
    {
        public int seed=2;
        public FigSettings settings=new();
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
        MeshCollider trunk;
        FigBranchInstance grabbed;
        float depth;
        Vector3 grabOffset;
        Camera View=>interactionCamera?interactionCamera:Camera.main;

        void OnEnable(){
            var shoots=new List<FigShoot>();
            var prepared=new System.Collections.Concurrent.ConcurrentDictionary<int,(FigShoot.MeshData mesh,VbdSheet solver)>();
            wood=JungleFig.Generate(seed,settings,shoots,(i,shoot)=>{
                var rig=new RodPlant(shoot.geometry);
                prepared[i]=(new FigShoot.MeshData(shoot),new VbdSheet(null,rig,true));
            });
            generated=new GameObject("Fig wood");
            generated.transform.SetParent(transform,false);
            generated.AddComponent<MeshFilter>().sharedMesh=wood;
            generated.AddComponent<MeshRenderer>().sharedMaterial=bark;
            trunk=generated.AddComponent<MeshCollider>();trunk.sharedMesh=wood;
            leafMaterial=new Material(Resources.Load<Shader>("FigLeaf"));
            normalMaterials=new[]{bark,leafMaterial};
            foreach(var shoot in shoots){
                prepared.TryRemove(crown.Count,out var data);
                var b=new FigBranchInstance(shoot,data.mesh);crown.Add(b);
                b.Activate(data.solver);b.EnableRuntimeSkin();
                var go=new GameObject("Physical bough");
                go.transform.SetParent(generated.transform,false);go.transform.localPosition=shoot.origin;
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
            if(wood)Destroy(wood);
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
