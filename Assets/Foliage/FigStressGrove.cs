using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Foliage
{
    public sealed class FigStressGrove : MonoBehaviour
    {
        public const int Count=20;
        public int Trees {get; private set;}
        public int Tick {get; private set;}
        public int ContactPairs=>pairs.Count;
        public int TreesInContact=>touched.Count;
        public bool Paused;
        public Bounds TreeBounds {get; private set;}
        readonly HashSet<int> pairs=new();
        readonly HashSet<int> touched=new();
        readonly List<Mesh> meshes=new();
        Material bark,leaf;
        Scene scene;
        PhysicsScene physicsScene;

        public void Initialize(Material barkMaterial){
            bark=barkMaterial;
            leaf=new Material(Resources.Load<Shader>("FigLeaf"));
            scene=SceneManager.CreateScene("Fig stress grove "+GetInstanceID(),new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            SceneManager.MoveGameObjectToScene(gameObject,scene);
            physicsScene=scene.GetPhysicsScene();
        }

        // Five trees per patch. Spacing scales with crown size so the saplings
        // meet other saplings below the two taller canopy layers.
        void Update(){
            if(Trees>=Count)return;
            int index=Trees, patch=index/5, slot=index%5;
            var size=patch==0?FigSize.Small:patch==3?FigSize.Large:FigSize.Medium;
            var settings=new FigSettings{sizeClass=size,size=.76f+.105f*slot+.024f*patch,canopyDensity=1};
            int seed=23+17*index;
            float spread=size==FigSize.Small?1f:size==FigSize.Medium?5f:10f;
            Vector3 center=new Vector3(patch%2==0?-10:10,0,patch<2?-10:10);
            float angle=slot*2.4f+patch;
            Vector3 offset=new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle))*spread*Mathf.Sqrt(slot/4f);
            var tree=new GameObject(size+" fig "+seed);
            tree.transform.SetParent(transform,false);
            tree.transform.localPosition=center+offset;
            tree.transform.localRotation=Quaternion.Euler(0,index*137.5f,0);
            var shoots=new List<FigShoot>();
            var wood=JungleFig.Generate(seed,settings,shoots);
            AddMesh(tree,wood,new[]{bark});
            tree.layer=8+index;
            var anchor=tree.AddComponent<Rigidbody>();
            anchor.isKinematic=true;
            anchor.excludeLayers=1<<tree.layer;
            var trunk=tree.AddComponent<MeshCollider>();
            trunk.sharedMesh=wood;
            foreach(var shoot in shoots)AddBough(anchor,shoot,settings.ScaleFor(seed));
            Trees++;
        }

        void AddMesh(GameObject obj,Mesh mesh,Material[] materials){
            meshes.Add(mesh);
            obj.AddComponent<MeshFilter>().sharedMesh=mesh;
            var renderer=obj.AddComponent<MeshRenderer>();
            renderer.sharedMaterials=materials;
            var bounds=meshes.Count==1?renderer.bounds:TreeBounds;
            bounds.Encapsulate(renderer.bounds);
            TreeBounds=bounds;
        }

        void AddBough(Rigidbody anchor,FigShoot shoot,float scale){
            var tree=anchor.gameObject;
            var obj=new GameObject("Jointed bough");
            obj.transform.SetParent(tree.transform,false);
            obj.transform.localPosition=shoot.origin;
            obj.layer=tree.layer;
            AddMesh(obj,shoot.CreateMesh(),new[]{bark,leaf});
            var geometry=shoot.geometry;
            int endRod=geometry.twigClusters.Count==0?geometry.rods.Count:geometry.twigClusters[0].firstRod;
            // Short capsule chains follow the wood. Leaf sprays move with their bough.
            for(int i=1;i<endRod;){
                var first=geometry.rods[i++];
                var last=first;
                for(int span=1;span<3 && i<endRod && geometry.rods[i].a==last.b;span++)
                    last=geometry.rods[i++];
                if(first.radius<.035f*scale)continue;
                Vector3 a=geometry.nodes[first.a],b=geometry.nodes[last.b];
                var colliderObject=new GameObject("Wood capsule");
                colliderObject.layer=tree.layer;
                var t=colliderObject.transform;
                t.SetParent(obj.transform,false);
                t.localPosition=(a+b)*.5f;
                t.localRotation=Quaternion.FromToRotation(Vector3.up,b-a);
                var capsule=colliderObject.AddComponent<CapsuleCollider>();
                capsule.radius=(first.radius+last.radius)*.5f;
                capsule.height=(b-a).magnitude+2*capsule.radius;
            }
            var body=obj.AddComponent<Rigidbody>();
            body.useGravity=false;
            body.excludeLayers=1<<tree.layer;
            body.mass=Mathf.Clamp(80*scale*scale*scale,1,180);
            body.angularDamping=2;
            body.maxDepenetrationVelocity=2;
            var joint=obj.AddComponent<ConfigurableJoint>();
            joint.autoConfigureConnectedAnchor=false;
            joint.connectedBody=anchor;
            joint.anchor=Vector3.zero;
            joint.connectedAnchor=shoot.origin;
            joint.projectionMode=JointProjectionMode.PositionAndRotation;
            joint.projectionDistance=.015f;
            joint.projectionAngle=2;
            joint.xMotion=joint.yMotion=joint.zMotion=ConfigurableJointMotion.Locked;
            joint.angularXMotion=joint.angularYMotion=joint.angularZMotion=ConfigurableJointMotion.Limited;
            float rigidity=1+FigShoot.WoodRigidityGain(geometry.rods[0].radius/scale);
            float limit=22f/Mathf.Sqrt(rigidity);
            joint.lowAngularXLimit=new SoftJointLimit{limit=-limit};
            joint.highAngularXLimit=joint.angularYLimit=joint.angularZLimit=new SoftJointLimit{limit=limit};
            joint.rotationDriveMode=RotationDriveMode.Slerp;
            float inertia=body.inertiaTensor.magnitude;
            joint.slerpDrive=new JointDrive{positionSpring=18*inertia*rigidity,positionDamper=4*inertia*Mathf.Sqrt(rigidity),maximumForce=float.MaxValue};
            obj.AddComponent<FigStressBough>().grove=this;
        }

        public void Step(){
            if(Trees!=Count)return;
            pairs.Clear();touched.Clear();
            physicsScene.Simulate(1f/60);
            Tick++;
        }
        void LateUpdate(){if(!Paused)Step();}
        internal void Record(int a,int b){
            if(a==b || a<0 || b<0 || a>=Count || b>=Count)return;
            pairs.Add(Mathf.Min(a,b)*Count+Mathf.Max(a,b));
            touched.Add(a);touched.Add(b);
        }
        void OnDestroy(){
            foreach(var mesh in meshes)Destroy(mesh);
            if(leaf)Destroy(leaf);
        }
        public void Close(){
            gameObject.SetActive(false);
            if(scene.IsValid() && scene.isLoaded)SceneManager.UnloadSceneAsync(scene);
        }
    }
}

