using System;
using System.Collections.Generic;
using UnityEngine;

namespace Foliage
{
    public enum PickMode { Element, Node, Part, Plant }
    public struct PlantHit
    {
        public bool valid;
        public int rod, face, node, part;
        public float along;
        public Vector3 weights;
        public Vector3 Point(PlantGeometry g)
        {
            if (node>=0) return g.nodes[node];
            if (rod>=0)
            {
                var r=g.rods[rod];
                return Vector3.Lerp(g.nodes[r.a],g.nodes[r.b],along);
            }
            var f=g.faces[face];
            return g.nodes[f.a]*weights.x+g.nodes[f.b]*weights.y+g.nodes[f.c]*weights.z;
        }
    }

    [RequireComponent(typeof(MeshFilter),typeof(MeshRenderer))]
    public partial class FoliageLab : MonoBehaviour
    {
        public PlantSettings settings = PlantSettings.Default;
        public Camera view;
        public Transform testSphere;
        public PlantGeometry Geometry { get; private set; }
        public int Tick { get; private set; }
        public event Action<float> SimulationStep;
        public bool paused;
        public float speed=1;
        public float grabStiffness=100, grabDamping=5, grabMaxForce=20;
        const float step=1f/60;
        Mesh mesh;
        Material plantMaterial, lines;
        PlantHit selected;
        PickMode mode;
        Vector3 pivot, target, sphereOffset, sphereStart;
        float yaw=-22, pitch=14, distance, depth, accumulator;
        double lastClockTime;
        bool grabbing, movingSphere;
        Vector2 previousMouse;
        Vector3[] normals;

        void Awake()
        {
            mesh=new Mesh {name="Procedural plant",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            GetComponent<MeshFilter>().sharedMesh=mesh;
            plantMaterial=new Material(Resources.Load<Shader>("PlantSurface"));
            GetComponent<MeshRenderer>().sharedMaterial=plantMaterial;
            lines=new Material(Shader.Find("Hidden/Internal-Colored"));
            lines.SetInt("_ZWrite",0);
            lines.SetInt("_ZTest",(int)UnityEngine.Rendering.CompareFunction.LessEqual);
            sphereStart=testSphere.position;
            Rebuild();
            Frame();
        }
        void OnDestroy()
        {
            if(branchPanelSettings)Destroy(branchPanelSettings);
            if(branchFramePacing) QualitySettings.vSyncCount=previousVSync;
            ClearObstacle();
            Sheet?.Dispose();
            if(graphLines)Destroy(graphLines);
            crossPoints?.Release();pinPoints?.Release();
            if(debugCrosses)Destroy(debugCrosses);
            Destroy(mesh); Destroy(plantMaterial); Destroy(lines);
        }
        public void Rebuild()
        {
            var renderSettings=settings;
            if(Sheet==null)renderSettings.adaptivePhysics=false;
            Geometry=PlantGeometry.Generate(renderSettings);
            mesh.Clear();
            mesh.SetVertices(Geometry.skin);
            mesh.SetColors(Geometry.colors);mesh.SetUVs(0,Geometry.bladeUV);
            var indices=new List<int>(3*Geometry.surface.Count);
            foreach(var f in Geometry.surface) { indices.Add(f.a); indices.Add(f.b); indices.Add(f.c); }
            mesh.SetTriangles(indices,0);
            mesh.RecalculateNormals(); normals=mesh.normals;
            ResetState();
        }
        public void ResetState()
        {
            CancelCut();
            if(uncutGeometry!=null){OpenStems();return;}
            if(Sheet!=null) { Sheet.Reset(); inspectedFace=inspectedRod=-1; RefreshSheetMesh(); }
            lastClockTime=Time.realtimeSinceStartupAsDouble;
            Tick=0; accumulator=0; selected=default; grabbing=false; movingSphere=false;
            testSphere.position=sphereStart;
        }
        public void Advance()=>Advance(true);
        void FinishQueuedTick()
        {
            if(Sheet==null || !Sheet.Pending)return;
            Sheet.CompletePendingStep();
            if(Sheet.badVertex>=0)paused=true;
            else RefreshSheetMesh();
            Tick++; SimulationStep?.Invoke(step);
        }
        void Advance(bool measureSweeps)
        {
            FinishQueuedTick();
            if(Sheet!=null)
            {
                if(Sheet.badVertex>=0) return;
                Sheet.Step(step,measureSweeps);
                if(Sheet.badVertex>=0) paused=true;
                else RefreshSheetMesh();
            }
            Tick++; SimulationStep?.Invoke(step);
        }
        void Frame()
        {
            pivot=Vector3.up*(settings.height*.53f);
            distance=settings.height*2.0f;
            if(settings.PlantCount>1 && Sheet?.plant!=null)
            {
                var bounds=mesh.bounds;pivot=bounds.center;
                var inverse=Quaternion.Inverse(Quaternion.Euler(pitch,yaw,0));
                float vertical=Mathf.Tan(view.fieldOfView*Mathf.Deg2Rad*.5f);
                float horizontal=vertical*Mathf.Max(1,Screen.width-PanelWidth*UiScale)/Mathf.Max(1,Screen.height);
                foreach(var p in Geometry.skin)
                {
                    Vector3 local=inverse*(p-pivot);
                    distance=Mathf.Max(distance,1.08f*Mathf.Max(Mathf.Abs(local.x)/horizontal,Mathf.Abs(local.y)/vertical)-local.z);
                }
            }
            PlaceCamera();
        }
        void PlaceCamera()
        {
            Quaternion rotation=Quaternion.Euler(pitch,yaw,0);
            view.transform.SetPositionAndRotation(pivot-rotation*Vector3.forward*distance,rotation);
            // Center the plant in the unobstructed portion of the Game view.
            float panelWidth=PanelWidth*UiScale;
            view.rect=new Rect(panelWidth/Screen.width,0,1-panelWidth/Screen.width,1);
        }
        internal static bool Intersect(Ray ray,Vector3 a,Vector3 b,Vector3 c,out float t,out Vector3 weights)
        {
            Vector3 ab=b-a, ac=c-a, h=Vector3.Cross(ray.direction,ac);
            float det=Vector3.Dot(ab,h);
            weights=default; t=0;
            if(Mathf.Abs(det)<1e-10f) return false;
            Vector3 s=ray.origin-a, q=Vector3.Cross(s,ab);
            float u=Vector3.Dot(s,h)/det, v=Vector3.Dot(ray.direction,q)/det;
            t=Vector3.Dot(ac,q)/det;
            weights=new Vector3(1-u-v,u,v);
            return t>=0 && u>=0 && v>=0 && u+v<=1;
        }
        public PlantHit Pick(Ray ray, PickMode pick, float limit=float.PositiveInfinity)
        {
            var g=Geometry;
            PlantHit hit=default;
            float nearest=limit;
            foreach(var f in g.surface)
            {
                if(!Intersect(ray,g.skin[f.a],g.skin[f.b],g.skin[f.c],out float t,out Vector3 w) || t>=nearest) continue;
                nearest=t;
                hit=new PlantHit {valid=true,rod=f.rod,face=f.face,node=-1,weights=w};
                if(f.rod>=0)
                {
                    var rod=g.rods[f.rod];
                    Vector3 axis=g.nodes[rod.b]-g.nodes[rod.a];
                    hit.along=Mathf.Clamp01(Vector3.Dot(ray.GetPoint(t)-g.nodes[rod.a],axis)/axis.sqrMagnitude);
                    hit.part=rod.part;
                    if(pick==PickMode.Node) hit.node=hit.along<.5f?rod.a:rod.b;
                }
                else
                {
                    var face=g.faces[f.face];
                    hit.part=face.part;
                    if(pick==PickMode.Node)
                    {
                        Vector3 p=ray.GetPoint(t);
                        int closest=(g.nodes[face.a]-p).sqrMagnitude<(g.nodes[face.b]-p).sqrMagnitude?face.a:face.b;
                        hit.node=(g.nodes[closest]-p).sqrMagnitude<(g.nodes[face.c]-p).sqrMagnitude?closest:face.c;
                    }
                }
            }
            return hit;
        }
        void LateUpdate()=>RefreshBranchPanel();
        void Update()
        {
            double now=Time.realtimeSinceStartupAsDouble;
            float elapsed=(float)(now-lastClockTime);lastClockTime=now;
            if(releasePending){CompleteRelease(false);return;}
            bool canQueue=speed<=1 && settings.forked && Sheet?.plant!=null && sheetTab==0 &&
                SimulationStep==null && SystemInfo.supportsAsyncGPUReadback;
            // Render/input can run while the GPU works. Synchronize when the next
            // fixed tick actually needs positions, or a caller requests a step.
            if(!canQueue || paused || cutting || Sheet.ReadbackReady || accumulator+elapsed*speed>=step)
                FinishQueuedTick();
            if(!paused && !cutting)
            {
                // The queued solver can repay a brief stall over later renders.
                // Keep elapsed time measured after fixture creation, including the first frame.
                accumulator+=Mathf.Min(elapsed,canQueue?1f:.1f)*speed;
                while(accumulator>=step && !paused)
                {
                    accumulator-=step;
                    if(canQueue)
                    {
                        // Keep elapsed time in the accumulator. A slow frame must not
                        // turn the following ticks into blocking diagnostic readbacks.
                        Sheet.QueueStep(step);
                        break;
                    }
                    else Advance(sheetTab!=0);
                }
            }
            Vector2 mouse=Input.mousePosition, delta=mouse-previousMouse;
            previousMouse=mouse;
            if(cutting){if(Input.GetMouseButtonUp(0))ReleaseCut(mouse);else PreviewCut(mouse);return;}
            if(Input.GetMouseButtonUp(0)) EndDrag();
            PlaceCamera();
            if(!view.pixelRect.Contains(mouse) && !grabbing && !movingSphere) return;
            float scroll=Input.mouseScrollDelta.y;
            if(Input.GetMouseButton(1)) { yaw+=delta.x*.3f; pitch=Mathf.Clamp(pitch-delta.y*.3f,-85,85); }
            if(Input.GetMouseButton(2))
                pivot-=(view.transform.right*delta.x+view.transform.up*delta.y)*distance/view.pixelHeight;
            Vector3 travel=new Vector3((Input.GetKey(KeyCode.D)?1:0)-(Input.GetKey(KeyCode.A)?1:0),
                (Input.GetKey(KeyCode.E)?1:0)-(Input.GetKey(KeyCode.Q)?1:0),
                (Input.GetKey(KeyCode.W)?1:0)-(Input.GetKey(KeyCode.S)?1:0));
            pivot+=view.transform.rotation*travel.normalized*(Time.unscaledDeltaTime*distance*.6f);
            if(!grabbing && !movingSphere) distance=Mathf.Clamp(distance*Mathf.Exp(-scroll*.12f),.15f,25);
            PlaceCamera();
            var ray=view.ScreenPointToRay(mouse);
            if(Sheet!=null)
            {
                if(Input.GetMouseButtonDown(0))
                {
                    if(cutTool && Sheet.plant!=null)BeginCut(mouse);else BeginLabDrag(ray);
                }
                if(grabbing) MoveSheetTarget(mouse,scroll);
                if(movingSphere) MoveObstacleTarget(mouse,scroll);
                return;
            }
            if(Input.GetMouseButtonDown(0))
            {
                RaycastHit contact=default;
                bool sphere=testSphere.gameObject.activeSelf && testSphere.GetComponent<Collider>()
                    .Raycast(ray,out contact,100);
                float limit=float.PositiveInfinity;
                if(sphere) limit=contact.distance;
                selected=Pick(ray,mode,limit);
                movingSphere=!selected.valid && sphere;
                grabbing=selected.valid;
                if(grabbing || movingSphere)
                {
                    Vector3 p=grabbing?selected.Point(Geometry):ray.GetPoint(limit);
                    depth=view.WorldToScreenPoint(p).z;
                    sphereOffset=testSphere.position-p;
                }
            }
            if(grabbing || movingSphere)
            {
                depth=Mathf.Clamp(depth*Mathf.Exp(scroll*.08f),.08f,40);
                target=view.ScreenToWorldPoint(new Vector3(mouse.x,mouse.y,depth));
                if(movingSphere) testSphere.position=target+sphereOffset;
            }
        }
    }
}
