using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Foliage
{
    public partial class FoliageLab
    {
        void Start()
        {
            var args=Environment.GetCommandLineArgs();
            adaptiveField=Array.IndexOf(args,"-fullDetailFoliage")<0;
            if(Array.IndexOf(args,"-noFoliageDebugRendering")>=0)SetDebugRendering(false);
            if(Array.IndexOf(args,"-benchmarkPlantCuts")>=0)
            {
                Application.runInBackground=true;
                StartPlayerCapsuleTest(100);
                StartCoroutine(MeasurePlantCuts());
                return;
            }
            if(Array.IndexOf(args,"-hundredPlantCapsuleOrbit")>=0 || Array.IndexOf(args,"-benchmarkHundredPlantCapsule")>=0)
            {
                StartPlayerCapsuleTest(100);
                if(Array.IndexOf(args,"-benchmarkHundredPlantCapsule")>=0)
                {
                    Application.runInBackground=true;
                    StartCoroutine(MeasureCapsuleOrbit());
                }
                return;
            }
            bool benchTen=Array.IndexOf(args,"-benchmarkTenPlantCapsule")>=0;
            if(benchTen || Array.IndexOf(args,"-playerCapsuleOrbit")>=0)
            {
                StartPlayerCapsuleTest();
                if(benchTen){Application.runInBackground=true;StartCoroutine(MeasureCapsuleOrbit());}
                return;
            }
            bool overlap=Array.IndexOf(args,"-benchmarkFoliageSpawn")>=0;
            bool ten=Array.IndexOf(args,"-benchmarkTenPlants")>=0;
            if(ten || overlap || Array.IndexOf(args,"-benchmarkBranchedPlant")>=0)
            {
                Application.runInBackground=true;
                StartCoroutine(BranchFrameBenchmark(true,overlap,ten));
            }
        }

        IEnumerator MeasurePlantCuts()
        {
            while(Tick<60)yield return null;
            FinishQueuedTick();
            paused=true;
            for(int test=0;test<2;test++)
            {
                var source=Sheet;
                var face=Geometry.faces[0];var x=source.positions;
                var plane=new Plane((x[face.b]-x[face.a]).normalized,(x[face.a]+x[face.b])*.5f);
                var timer=System.Diagnostics.Stopwatch.StartNew();
                var snapshot=new PlantCut(source);
                int rod=test==0?2:-1;
                var work=System.Threading.Tasks.Task.Run(()=>Prepare(source,snapshot,rod,.41f,0,plane,true));
                while(!work.IsCompleted)yield return null;
                var result=work.GetAwaiter().GetResult();
                double prepare=timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                bool ok=CommitCut(result);
                double commit=timer.Elapsed.TotalMilliseconds;
                Debug.Log($"Cut benchmark {(test==0?"stem":"leaf")}: prepare {prepare:F1} ms, main-thread commit {commit:F1} ms, success {ok}, components {Geometry.islands}.");
                if(!ok){Application.Quit(1);yield break;}
                yield return null;
            }
            paused=false;
            yield return MeasureCapsuleOrbit();
        }

        IEnumerator MeasureCapsuleOrbit()
        {
            var contact=Sheet.contact;
            var previous=contact.center;
            int frames=0,rods=0,faces=0,firstTick=Tick;
            float distance=0,penetration=0,peakStretch=0;
            int sampledTick=-1,longestContact=0;
            int[] streak=new int[settings.PlantCount==10?Geometry.rods.Count:0];
            float[] lengths=new float[streak.Length];
            for(int i=0;i<lengths.Length;i++)
            {
                var r=Geometry.rods[i];lengths[i]=Vector3.Distance(Sheet.rest[r.a],Sheet.rest[r.b]);
            }
            double start=Time.realtimeSinceStartupAsDouble;
            int warmTick=firstTick,warmFrames=0;
            double warmStart=0,lastRender=0;
            var frameTimes=new List<double>();
            void Rendered(Camera camera)
            {
                if(camera!=view)return;
                double now=Time.realtimeSinceStartupAsDouble;
                if(warmStart>0 && lastRender>0)frameTimes.Add(1000*(now-lastRender));
                lastRender=now;
                frames++;
            }
            Camera.onPostRender+=Rendered;
            try
            {
                while(Tick-firstTick<700 && Sheet.badVertex<0 && Time.realtimeSinceStartupAsDouble-start<600)
                {
                    yield return null;
                    if(warmStart==0 && Tick-firstTick>=60)
                    {
                        warmStart=Time.realtimeSinceStartupAsDouble;
                        warmFrames=frames;warmTick=Tick;
                    }
                    distance+=Vector3.Distance(previous,contact.center);
                    previous=contact.center;
                    if(settings.PlantCount==10 && sampledTick!=Tick)
                    {
                        sampledTick=Tick;
                        for(int i=0;i<streak.Length;i++)
                        {
                            var r=Geometry.rods[i];
                            peakStretch=Mathf.Max(peakStretch,Vector3.Distance(Sheet.positions[r.a],Sheet.positions[r.b])/lengths[i]-1);
                            streak[i]=contact.hits[i].depth>0?streak[i]+1:0;
                            longestContact=Math.Max(longestContact,streak[i]);
                        }
                    }
                    rods=Math.Max(rods,contact.rods);
                    faces=Math.Max(faces,contact.faces);
                    penetration=Mathf.Max(penetration,contact.physicalPenetration);
                }
            }
            finally { Camera.onPostRender-=Rendered; }
            double seconds=Time.realtimeSinceStartupAsDouble-start;
            if(settings.PlantCount==10)Debug.Log($"Rod tensile strain {peakStretch:P1}, longest continuous rod contact {longestContact} ticks.");
            Debug.Log($"{settings.PlantCount}-plant capsule: {Geometry.rootNodes.Count-Geometry.coarsePlants} higher-detail and {Geometry.coarsePlants} reduced plants, {Geometry.bladeRows.Count} leaves, {Sheet.Count} nodes, {frames/seconds:F2} FPS, {(Tick-firstTick)/seconds:F2} ticks/s, {Tick-firstTick} ticks, travel {distance:F3} m, peak {rods} rod and {faces} face contacts, penetration {penetration*1000:F2} mm, invalid node {Sheet.badVertex}, debug rendering {debugRendering}.");
            double steady=Time.realtimeSinceStartupAsDouble-warmStart;
            frameTimes.Sort();
            double p95=frameTimes.Count==0?double.PositiveInfinity:frameTimes[(int)(frameTimes.Count*.95)];
            double steadyTicks=warmStart>0?(Tick-warmTick)/steady:0;
            if(warmStart>0)Debug.Log($"After first 60 ticks: {(frames-warmFrames)/steady:F2} FPS, {steadyTicks:F2} ticks/s, p95 {p95:F2} ms.");
            bool strainOk=settings.PlantCount!=10 || peakStretch<=.051f;
            Application.Quit(strainOk && Tick-firstTick>=700 && Sheet.badVertex<0 && distance>=2*Mathf.PI*PlantContact.OrbitRadius(settings.PlantCount) && rods+faces>0 && frames/seconds>60 && (Tick-firstTick)/seconds>=59 && steadyTicks>=59?0:1);
        }

        public IEnumerator BranchFrameBenchmark(bool quitPlayer=false,bool overlapSpawn=false,bool tenPlants=false)
        {
            if(!settings.forked || settings.PlantCount!=(tenPlants?10:1))SetBranchTest(true,tenPlants);
            else OpenStems();
            paused=false;
            speed=1;
            sheetTab=0;
            expandBranchSettings=false;
            if(overlapSpawn)
            {
                int face=Geometry.faces.Count/2;
                SetObstacle(ObstacleShape.Sphere,Sheet.Point(face,Vector3.one/3f),Quaternion.identity,.15f);
            }
            bool passed=true;
            // Time actual lab camera renders, including the interval spent on simulation and UI.
            var times=new List<double>();
            double last=0;int renderedFrames=0;
            void Rendered(Camera camera)
            {
                if(camera!=view)return;
                renderedFrames++;
                double now=Time.realtimeSinceStartupAsDouble;
                if(last!=0)times.Add((now-last)*1000);
                last=now;
            }
            Camera.onPostRender+=Rendered;
            try
            {
                if(tenPlants){while(Tick<60 && Sheet.badVertex<0)yield return null;}
                else for(int i=0;i<120;i++)yield return null;
                for(int pass=0;pass<2;pass++)
                {
                    float penetration=0,betweenDepth=0;
                    times.Clear();renderedFrames=0;
                    last=0;
                    int startTick=Tick,contacts=0,bladeContacts=0,between=0;
                    double start=Time.realtimeSinceStartupAsDouble;
                    int rodIndex=Geometry.rods.Count-1;
                    var rod=Geometry.rods[rodIndex];
                    Vector3 origin=Vector3.Lerp(Sheet.positions[rod.a],Sheet.positions[rod.b],.75f);
                    if(pass==1)
                    {
                        Sheet.grabRod=rodIndex;
                        Sheet.weights=new Vector3(.25f,.75f,0);
                    }
                    double elapsed;
                    do
                    {
                        if(pass==1)Sheet.target=origin+view.transform.right*(.18f*Mathf.Sin((float)(Time.realtimeSinceStartupAsDouble-start)*2));
                        yield return null;
                        elapsed=Time.realtimeSinceStartupAsDouble-start;
                        penetration=Mathf.Max(penetration,Sheet.contact.physicalPenetration);
                        betweenDepth=Mathf.Max(betweenDepth,Sheet.selfContact.betweenPlantPenetration);
                        between=Math.Max(between,Sheet.selfContact.betweenPlantContacts);
                        contacts=Math.Max(contacts,Sheet.selfContact.branchContacts);
                        bladeContacts=Math.Max(bladeContacts,Sheet.selfContact.bladeContacts);
                    }
                    while(elapsed<12);
                    times.Sort();
                    double fps=renderedFrames/elapsed,ticks=(Tick-startTick)/elapsed;
                    double p95=times.Count>0?times[(int)(times.Count*.95)]:double.PositiveInfinity;
                    string scenario=tenPlants?"Ten plants":overlapSpawn?"Spawn":"Branch";
                    Debug.Log($"{scenario} benchmark {(pass==0?"gravity":"drag")} {Screen.width}x{Screen.height}, {SystemInfo.graphicsDeviceName}, debug rendering {(debugRendering?"on":"off")}, {fps:F2} FPS, p95 {p95:F2} ms, {ticks:F2} ticks/s, peak branch contacts {contacts}, peak blade contacts {bladeContacts}, between plants {between}, peak inter-plant penetration {1000*betweenDepth:F2} mm, peak obstacle penetration {1000*penetration:F3} mm, invalid node {Sheet.badVertex}.");
                    passed &= Sheet.badVertex<0 && ticks>=59 && fps>60 && (!overlapSpawn || penetration<.0001f) && (!tenPlants || between>0);
                }
            }
            finally
            {
                Camera.onPostRender-=Rendered;
                EndDrag();
            }
            if(quitPlayer)Application.Quit(passed?0:1);
        }
    }
}
