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
        Material leaf;
        Scene scene;
        PhysicsScene physicsScene;

        public void Initialize(FigGroveLayout layout,Material bark){
            leaf=new Material(Resources.Load<Shader>("FigLeaf"));
            var woodMaterials=new[]{bark};
            var boughMaterials=new[]{bark,leaf};
            scene=SceneManager.CreateScene("Fig stress grove "+GetInstanceID(),new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            SceneManager.MoveGameObjectToScene(gameObject,scene);
            physicsScene=scene.GetPhysicsScene();
            // Register each compound body with PhysX once, after all its shapes exist.
            gameObject.SetActive(false);
            var drives=new List<(ConfigurableJoint joint,Rigidbody body,float rigidity)>();
            foreach(var model in layout.trees){
                var tree=new GameObject(model.name);
                tree.transform.SetParent(transform,false);
                tree.transform.SetLocalPositionAndRotation(model.position,model.rotation);
                tree.layer=8+Trees;
                AddMesh(tree,model.wood,woodMaterials);
                var anchor=tree.AddComponent<Rigidbody>();
                anchor.isKinematic=true;
                anchor.excludeLayers=1<<tree.layer;
                tree.AddComponent<MeshCollider>().sharedMesh=model.wood;
                foreach(var bough in model.boughs){
                    var joint=AddBough(anchor,bough,model.mass,boughMaterials,out var body);
                    drives.Add((joint,body,bough.rigidity));
                }
                Trees++;
            }
            gameObject.SetActive(true);
            // Unity computes compound inertia when the complete body enters its physics scene.
            foreach(var drive in drives){
                float inertia=drive.body.inertiaTensor.magnitude;
                drive.joint.slerpDrive=new JointDrive{positionSpring=18*inertia*drive.rigidity,
                    positionDamper=4*inertia*Mathf.Sqrt(drive.rigidity),maximumForce=float.MaxValue};
            }
            var renderers=GetComponentsInChildren<MeshRenderer>();
            var bounds=renderers[0].bounds;
            foreach(var renderer in renderers)bounds.Encapsulate(renderer.bounds);
            TreeBounds=bounds;
        }

        static void AddMesh(GameObject obj,Mesh mesh,Material[] materials){
            obj.AddComponent<MeshFilter>().sharedMesh=mesh;
            obj.AddComponent<MeshRenderer>().sharedMaterials=materials;
        }

        ConfigurableJoint AddBough(Rigidbody anchor,FigGroveLayout.Bough model,float mass,Material[] materials,out Rigidbody body){
            var tree=anchor.gameObject;
            var obj=new GameObject("Jointed bough");
            obj.transform.SetParent(tree.transform,false);
            obj.transform.localPosition=model.origin;
            obj.layer=tree.layer;
            AddMesh(obj,model.mesh,materials);
            body=obj.AddComponent<Rigidbody>();
            foreach(var shape in model.capsules){
                var colliderObject=new GameObject("Wood capsule");
                colliderObject.layer=tree.layer;
                colliderObject.transform.SetParent(obj.transform,false);
                colliderObject.transform.SetLocalPositionAndRotation(shape.center,shape.rotation);
                var capsule=colliderObject.AddComponent<CapsuleCollider>();
                capsule.radius=shape.radius;
                capsule.height=shape.height;
            }
            body.useGravity=false;
            body.excludeLayers=1<<tree.layer;
            body.mass=mass;
            body.angularDamping=2;
            body.maxDepenetrationVelocity=2;
            var joint=obj.AddComponent<ConfigurableJoint>();
            joint.autoConfigureConnectedAnchor=false;
            joint.connectedBody=anchor;
            joint.anchor=Vector3.zero;
            joint.connectedAnchor=model.origin;
            joint.projectionMode=JointProjectionMode.PositionAndRotation;
            joint.projectionDistance=.015f;
            joint.projectionAngle=2;
            joint.xMotion=joint.yMotion=joint.zMotion=ConfigurableJointMotion.Locked;
            joint.angularXMotion=joint.angularYMotion=joint.angularZMotion=ConfigurableJointMotion.Limited;
            float rigidity=model.rigidity;
            float limit=22f/Mathf.Sqrt(rigidity);
            joint.lowAngularXLimit=new SoftJointLimit{limit=-limit};
            joint.highAngularXLimit=joint.angularYLimit=joint.angularZLimit=new SoftJointLimit{limit=limit};
            joint.rotationDriveMode=RotationDriveMode.Slerp;
            obj.AddComponent<FigStressBough>().grove=this;
            return joint;
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

            if(leaf)Destroy(leaf);
        }
        public void Close(){
            gameObject.SetActive(false);
            if(scene.IsValid() && scene.isLoaded)SceneManager.UnloadSceneAsync(scene);
        }
    }
}

