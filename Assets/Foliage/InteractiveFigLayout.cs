using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Foliage
{
    [PreferBinarySerialization]
    public sealed class InteractiveFigLayout : ScriptableObject
    {
        public int seed;
        public FigSettings settings;
        public Mesh wood;
        [HideInInspector] public FigShoot.Baked[] boughs;

        internal bool Matches(int requestedSeed,FigSettings requested,Vector3 gradient)=>
            requestedSeed==seed && gradient.Equals(settings.GroundGradient) &&
            JsonUtility.ToJson(requested)==JsonUtility.ToJson(settings);

        internal void BuildBranches(List<FigBranchInstance> branches){
            // Mesh access and GPU allocation belong to Unity's main thread. Each
            // worker restores one independent rest geometry and prepares its solver.
            var positions=new Vector3[boughs.Length][];
            var colors=new Color[boughs.Length][];
            var normals=new Vector3[boughs.Length][];
            for(int i=0;i<boughs.Length;i++){
                positions[i]=boughs[i].mesh.vertices;
                colors[i]=boughs[i].mesh.colors;
                normals[i]=boughs[i].mesh.normals;
            }
            var prepared=new (FigShoot shoot,VbdSheet solver)[boughs.Length];
            Parallel.For(0,boughs.Length,i=>{
                var shoot=new FigShoot(boughs[i],positions[i],colors[i]);
                var solver=FigBranchInstance.PrepareSolver(shoot,boughs[i].solverLayout);
                solver.PrepareRenderData(normals[i]);
                prepared[i]=(shoot,solver);
            });
            for(int i=0;i<boughs.Length;i++){
                var branch=new FigBranchInstance(prepared[i].shoot,boughs[i].mesh);
                branches.Add(branch);
                branch.Activate(prepared[i].solver);
                branch.EnableRuntimeSkin();
            }
        }
    }

    public sealed partial class FigShoot
    {
        [Serializable] public sealed class Baked
        {
            [SerializeField] internal FigSolverLayout solverLayout;
            public Mesh mesh;
            public Vector3 origin;
            public Vector3[] nodes,grain,normals;
            public Vector4[] direction;
            public float[] thickness,age;
            public Vector2[] bladeUV;
            public PlantGeometry.Rod[] rods;
            public PlantGeometry.Face[] faces;
            public PlantGeometry.SkinFace[] surface;
            [SerializeField] internal PlantGeometry.TwigCluster[] clusters;
            public int[] roots,groundPins,joints,bladeRows,neighbors,aerial,ring;
            public int leafyTwigs,collar;

#if UNITY_EDITOR
            public Baked(FigShoot shoot){
                var g=shoot.geometry;
                mesh=shoot.CreateMesh(new MeshData(shoot),true);
                origin=shoot.origin;nodes=g.nodes.ToArray();thickness=g.thicknessRatio.ToArray();
                rods=g.rods.ToArray();faces=g.faces.ToArray();surface=g.surface.ToArray();
                clusters=g.twigClusters.ToArray();roots=g.rootNodes.ToArray();groundPins=g.groundPins.ToArray();
                joints=g.joints.ToArray();bladeUV=g.bladeUV.ToArray();collar=g.fixedCollarVertices;
                neighbors=shoot.neighboringLeaf.ToArray();leafyTwigs=shoot.LeafyTwigCount;
                aerial=new int[shoot.aerialRods.Count];shoot.aerialRods.CopyTo(aerial);
                ring=shoot.boundarySource;grain=shoot.boundaryGrain;normals=shoot.boundaryNormals;
                age=shoot.boundaryAge;direction=shoot.boundaryDirection;
                // Unity serializes one-dimensional arrays. Preserve each blade's
                // row widths and particle indices, including its single-node tip.
                var rows=new List<int>();
                foreach(var blade in g.bladeRows){
                    rows.Add(blade.Count);
                    foreach(var row in blade){rows.Add(row.Length);rows.AddRange(row);}
                }
                bladeRows=rows.ToArray();
                solverLayout=new FigSolverLayout();
                var rig=new RodPlant(g,null,null,trackCutNeighbors:false);
                // CPU-only capture. No compute shaders or GPU state are created by the importer.
                _=new VbdSheet(null,rig,true,obstacleContacts:false,capture:solverLayout);
            }
#endif
        }

        internal FigShoot(Baked data,Vector3[] skin,Color[] colors){
            clearance=null;origin=data.origin;LeafyTwigCount=data.leafyTwigs;
            var g=geometry;
            g.distributedStemSupport=false;g.fixedCollarVertices=data.collar;
            g.nodes.AddRange(data.nodes);g.thicknessRatio.AddRange(data.thickness);
            g.skin.AddRange(skin);g.colors.AddRange(colors);g.bladeUV.AddRange(data.bladeUV);
            g.rods.AddRange(data.rods);g.faces.AddRange(data.faces);g.surface.AddRange(data.surface);
            g.twigClusters.AddRange(data.clusters);g.rootNodes.AddRange(data.roots);
            g.groundPins.AddRange(data.groundPins);g.joints.AddRange(data.joints);
            neighboringLeaf.AddRange(data.neighbors);aerialRods.UnionWith(data.aerial);
            boundarySource=data.ring;boundaryGrain=data.grain;boundaryNormals=data.normals;
            boundaryDirection=data.direction;boundaryAge=data.age;
            for(int i=0;i<data.bladeRows.Length;){
                int count=data.bladeRows[i++];
                var blade=new List<int[]>(count);
                for(int r=0;r<count;r++){
                    var row=new int[data.bladeRows[i++]];
                    Array.Copy(data.bladeRows,i,row,0,row.Length);i+=row.Length;blade.Add(row);
                }
                g.bladeRows.Add(blade);
            }
        }
    }

    [Serializable] internal sealed class FigSolverLayout
    {
        public VbdElement[] elements;
        public float[] mass;
        public int[] triangles,colors,rodCounts,offsets,incident,groupOffsets,groupNodes,stiffJoints;
        public Vector2Int[] edges;
    }
}
