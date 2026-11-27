using System;
using System.Collections.Generic;
using UnityEngine;

namespace Foliage
{
    public sealed class FigGroveLayout : ScriptableObject
    {
        [Serializable] public struct Capsule
        {
            public Vector3 center;
            public Quaternion rotation;
            public float radius,height;
        }
        [Serializable] public struct Bough
        {
            public Mesh mesh;
            public Vector3 origin;
            public Capsule[] capsules;
            public float rigidity;
        }
        [Serializable] public struct Tree
        {
            public string name;
            public Vector3 position;
            public Quaternion rotation;
            public float mass;
            public Mesh wood;
            public Bough[] boughs;
        }
        public Tree[] trees;

#if UNITY_EDITOR
        public static Tree BakeTree(int index,int seed)
        {
            int patch=index/5,slot=index%5;
            var size=patch==0?FigSize.Small:patch==3?FigSize.Large:FigSize.Medium;
            var settings=new FigSettings{sizeClass=size,size=.76f+.105f*slot+.024f*patch,canopyDensity=1};
            float scale=settings.ScaleFor(seed);
            float spread=size==FigSize.Small?1f:size==FigSize.Medium?5f:10f;
            var center=new Vector3(patch%2==0?-10:10,0,patch<2?-10:10);
            float angle=slot*2.4f+patch;
            var shoots=new List<FigShoot>();
            var tree=new Tree{
                name=size+" fig "+seed,
                position=center+new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle))*spread*Mathf.Sqrt(slot/4f),
                rotation=Quaternion.Euler(0,index*137.5f,0),
                mass=Mathf.Clamp(80*scale*scale*scale,1,180),
                wood=JungleFig.Generate(seed,settings,shoots),
                boughs=new Bough[shoots.Count]
            };
            for(int b=0;b<shoots.Count;b++){
                var shoot=shoots[b];
                var geometry=shoot.geometry;
                int end=geometry.twigClusters.Count==0?geometry.rods.Count:geometry.twigClusters[0].firstRod;
                var capsules=new List<Capsule>();
                for(int i=1;i<end;){
                    var first=geometry.rods[i++];
                    var last=first;
                    for(int span=1;span<3 && i<end && geometry.rods[i].a==last.b;span++)last=geometry.rods[i++];
                    if(first.radius<.035f*scale)continue;
                    Vector3 a=geometry.nodes[first.a],tip=geometry.nodes[last.b];
                    float radius=(first.radius+last.radius)*.5f;
                    capsules.Add(new Capsule{center=(a+tip)*.5f,rotation=Quaternion.FromToRotation(Vector3.up,tip-a),
                        radius=radius,height=(tip-a).magnitude+2*radius});
                }
                tree.boughs[b]=new Bough{mesh=shoot.CreateMesh(),origin=shoot.origin,capsules=capsules.ToArray(),
                    rigidity=1+FigShoot.WoodRigidityGain(geometry.rods[0].radius/scale)};
            }
            return tree;
        }
#endif
    }
}
