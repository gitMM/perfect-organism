using UnityEngine;
using UnityEngine.Rendering;

namespace Foliage
{
    public sealed partial class VbdSheet
    {
        // Cluster zero is the woody skeleton and always participates in the sweeps.
        internal bool useFigClusters=true;
        int figClusterRelax=-1,figWake,figMass,figFollow;
        bool fullFigSweeps,figWasGrabbed;
        internal bool FigSelectiveStep {get;private set;}
        internal ComputeBuffer FigStates=>figStates;
        int figTrialEnergy,figChooseTrial,figApplyTrial,figTrialGroups;
        int figTwigNodes;
        ComputeBuffer figStates,figRanges,figRest;
        int[] initialFigStates;

        void BuildFigClusters(float[] mass,ComputeBuffer baseMass)
        {
            var geom=plant.geometry;
            var clusters=geom.twigClusters;
            int count=clusters.Count+1;
            var owners=new int[Count];
            var ranges=new Int4[count];
            var anchors=new Int4[count];
            var loads=new float[count];
            var heads=new int[Count];
            for(int i=1;i<count;i++){
                var c=clusters[i-1];
                int parent=plant.parent[c.firstRod];
                var rod=geom.rods[parent];
                anchors[i]=new Int4(rod.a,c.anchor,plant.Ghost(parent),heads[c.anchor]);
                heads[c.anchor]=i;
                figTwigNodes+=c.endNode-c.firstNode+c.endRod-c.firstRod;
                ranges[i]=new Int4(c.firstNode,c.endNode,plant.Ghost(c.firstRod),plant.Ghost(c.endRod));
                for(int v=c.firstNode;v<c.endNode;v++){owners[v]=i;loads[i]+=mass[v];}
                for(int r=c.firstRod;r<c.endRod;r++)owners[plant.Ghost(r)]=i;
            }
            figCollectTiles=gpu.FindKernel("FigCollectTiles");
            figWake=gpu.FindKernel("FigWakeClusters");
            figMass=gpu.FindKernel("FigAttachmentMass");
            figFollow=gpu.FindKernel("FigFollowAttachment");
            ComputeBuffer Bind<T>(string name,T[] data,int stride,params int[] kernels) where T:struct
            {
                var buffer=new ComputeBuffer(data.Length,stride);
                buffers.Add(buffer);buffer.SetData(data);
                foreach(int k in kernels)gpu.SetBuffer(k,name,buffer);
                return buffer;
            }
            initialFigStates=new int[count];initialFigStates[0]=30;
            figTrialEnergy=gpu.FindKernel("FigTrialEnergy");
            figChooseTrial=gpu.FindKernel("FigChooseTrial");
            figApplyTrial=gpu.FindKernel("FigApplyTrial");
            figRest=Bind("FigRest",rest,12,figWake,figTrialEnergy,figApplyTrial);
            figTrialGroups=(Mathf.Max(Count,structuralCount)+127)/128;
            gpu.SetInt("FigTrialGroupCount",figTrialGroups);
            Bind("FigTrialGroups",new Vector3[figTrialGroups],12,figTrialEnergy,figChooseTrial);
            Bind("FigTrialFraction",new float[1],4,figChooseTrial,figApplyTrial);
            gpu.SetBuffer(figTrialEnergy,"X",x);
            gpu.SetBuffer(figTrialEnergy,"Old",oldPositions);
            gpu.SetBuffer(figTrialEnergy,"Mass",baseMass);
            gpu.SetBuffer(figApplyTrial,"X",x);
            figStates=Bind("FigState",initialFigStates,4,figClusterRelax,figFollow,figMass,figCollectTiles);
            gpu.SetBuffer(figWake,"FigNextState",figStates);
            Bind("FigOwner",owners,4,figClusterRelax,figWake,figFollow,figCollectTiles);
            Bind("FigAnchor",anchors,16,figWake,figFollow,figMass);
            figRanges=Bind("FigRange",ranges,16,figWake);
            Bind("FigLoad",loads,4,figMass);
            Bind("FigHead",heads,4,figMass);
            var effectiveMass=Bind("FigEffectiveMass",mass,4,figMass);
            gpu.SetBuffer(figClusterRelax,"Mass",effectiveMass);
            gpu.SetBuffer(figMass,"Mass",baseMass);
            gpu.SetBuffer(figWake,"Velocity",velocity);
            gpu.SetBuffer(figWake,"X",x);
            gpu.SetBuffer(figFollow,"Old",oldPositions);
            gpu.SetBuffer(figFollow,"X",x);
        }

        int figClearTiles,figCollectTiles;
        ComputeBuffer figDispatchArgs;
        CommandBuffer figSparseSweeps,figDenseSweeps;
        int recordedIterations=-1;

        void BuildFigTiles(ComputeBuffer groupNodes)
        {
            figClearTiles=gpu.FindKernel("FigClearTiles");
            int tileCount=groupOffsets[colorCount]/64;
            var tiles=new ComputeBuffer(tileCount,4);
            buffers.Add(tiles);
            gpu.SetBuffer(figClusterRelax,"FigTiles",tiles);
            if(!SystemInfo.supportsIndirectArgumentsBuffer){
                var identity=new int[tileCount];
                for(int i=0;i<tileCount;i++)identity[i]=i;
                tiles.SetData(identity);
                return;
            }
            var tileColors=new Vector2Int[tileCount];
            for(int color=0;color<colorCount;color++){
                int first=groupOffsets[color]/64,end=groupOffsets[color+1]/64;
                for(int tile=first;tile<end;tile++)tileColors[tile]=new Vector2Int(color,first);
            }
            var colorsBuffer=new ComputeBuffer(tileCount,8);
            figDispatchArgs=new ComputeBuffer(colorCount*3,4,ComputeBufferType.IndirectArguments | ComputeBufferType.Raw);
            buffers.Add(colorsBuffer);buffers.Add(figDispatchArgs);
            colorsBuffer.SetData(tileColors);
            gpu.SetInt("FigColorCount",colorCount);
            gpu.SetBuffer(figCollectTiles,"FigTileColors",colorsBuffer);
            gpu.SetBuffer(figCollectTiles,"FigTileOut",tiles);
            gpu.SetBuffer(figCollectTiles,"GroupNodes",groupNodes);
            gpu.SetBuffer(figClearTiles,"FigDispatchArgs",figDispatchArgs);
            gpu.SetBuffer(figCollectTiles,"FigDispatchArgs",figDispatchArgs);
        }

        void RunFigSweeps(bool selective,int dualBlocks,int sweeps)
        {
            if(recordedIterations!=sweeps){
                figSparseSweeps?.Release();figSparseSweeps=null;
                figDenseSweeps?.Release();figDenseSweeps=null;
                recordedIterations=sweeps;
            }
            ref CommandBuffer commands=ref (selective?ref figSparseSweeps:ref figDenseSweeps);
            if(commands==null){
                commands=new CommandBuffer{name="Fig colored sweeps"};
                bool compact=selective && figDispatchArgs!=null;
                if(compact){
                    commands.DispatchCompute(gpu,figClearTiles,(colorCount+63)/64,1,1);
                    commands.DispatchCompute(gpu,figCollectTiles,groupOffsets[colorCount]/64,1,1);
                }
                int startId=Shader.PropertyToID("GroupStart");
                for(int sweep=0;sweep<sweeps;sweep++){
                    for(int color=0;color<colorCount;color++){
                        int start=groupOffsets[color];
                        commands.SetComputeIntParam(gpu,startId,start);
                        if(compact)commands.DispatchCompute(gpu,figClusterRelax,figDispatchArgs,(uint)(12*color));
                        else commands.DispatchCompute(gpu,selective?figClusterRelax:relax,(groupOffsets[color+1]-start)/64,1,1);
                    }
                    if(dualBlocks>0)commands.DispatchCompute(gpu,figDualKernel,dualBlocks,1,1);
                }
            }
            Graphics.ExecuteCommandBuffer(commands);
        }

        struct Int4
        {
            public int a,b,c,d;
            public Int4(int a,int b,int c,int d){this.a=a;this.b=b;this.c=c;this.d=d;}
        }

        void ResetFigClusters()
        {
            figWasGrabbed=false;fullFigSweeps=false;FigSelectiveStep=false;
            if(figStates!=null)figStates.SetData(initialFigStates);
        }

        void UpdateFigClusters()
        {
            int node=grabFace>=0?triangles[3*grabFace]:grabRod>=0?plant.geometry.rods[grabRod].b:-1;
            gpu.SetInt("FigGrabNode",node);
            gpu.SetInt("FigFullSweeps",fullFigSweeps?1:0);
            gpu.Dispatch(figWake,initialFigStates.Length-1,1,1);
            if(!fullFigSweeps)gpu.Dispatch(figMass,(Count+63)/64,1,1);
        }
    }
}
