using UnityEngine;

namespace Foliage
{
    public partial class FoliageLab
    {
        int inspectedRod=-1, rodMap;
        float rodFraction;
        bool centers=true, directors, collarErrors;
        static readonly string[] rodMaps={"Plain","Bending","Twisting","Stretch"};
        PlantSettings previousPlant;
        public void SetBranchTest(bool enable)
        {
            if(settings.forked==enable)return;
            if(enable)previousPlant=settings;
            RestoreGeometry();
            settings=enable?PlantSettings.BranchTest:previousPlant.height>0?previousPlant:PlantSettings.Default;
            seedText=settings.seed.ToString();
            Geometry=PlantGeometry.Generate(settings);
            OpenStems();
        }
        public void OpenStems()
        {
            RestoreGeometry();
            SetSurface(new VbdSheet(plant:new RodPlant(Geometry)));
            yaw=-22; pitch=14; Frame();
        }
        bool BeginPlantGrab(Ray ray,float limit=float.PositiveInfinity)
        {
            EndDrag(); inspectedFace=inspectedRod=-1;
            var plant=Sheet.plant;
            float nearest=limit;
            Vector3 point=default;
            foreach(var f in Geometry.surface)
            {
                if(!Intersect(ray,plant.skin[f.a],plant.skin[f.b],plant.skin[f.c],out float t,out Vector3 weights) || t>=nearest) continue;
                nearest=t; inspectedFace=f.face; inspectedRod=f.rod;
                if(f.rod>=0)
                {
                    var rod=Geometry.rods[f.rod];
                    Vector3 a=Sheet.positions[rod.a], axis=Sheet.positions[rod.b]-a;
                    rodFraction=Mathf.Clamp01(Vector3.Dot(ray.GetPoint(t)-a,axis)/Mathf.Max(axis.sqrMagnitude,1e-12f));
                    weights=new Vector3(1-rodFraction,rodFraction,0);
                    point=a+axis*rodFraction;
                }
                else point=Sheet.Point(f.face,weights);
                inspectedWeights=weights;
            }
            if(inspectedFace<0 && inspectedRod<0) return false;
            Sheet.grabFace=inspectedFace; Sheet.grabRod=inspectedRod; Sheet.weights=inspectedWeights;
            Sheet.target=point; depth=view.WorldToScreenPoint(point).z; grabbing=true;
            return true;
        }
        void DrawRodInspector()
        {
            var plant=Sheet.plant;
            centers=GUILayout.Toggle(centers,"Rod centre lines");
            directors=GUILayout.Toggle(directors,"Material frames (RGB axes)");
            collarErrors=GUILayout.Toggle(collarErrors,"Leaf attachment errors");
            rodMap=GUILayout.SelectionGrid(rodMap,rodMaps,2);
            if(rodMap!=0) GUILayout.Label(rodMap==3?"Blue 0 → red 5% absolute strain":"Blue 0 → red 30° absolute rotation");
            float maxError=0;
            for(int i=0;i<plant.leafRod.Length;i++)
            {
                var row=Geometry.bladeRows[i][1];
                if(plant.leafRod[i]<0)continue;
                for(int j=row.Length/2-1;j<=row.Length/2+1;j++)
                    if(!Geometry.severedCollars.Contains(row[j]))maxError=Mathf.Max(maxError,Vector3.Distance(Sheet.positions[row[j]],plant.CollarTarget(plant.leafRod[i],row[j],Sheet.positions)));
            }
            GUILayout.Label($"{Geometry.rods.Count} rod segments\nMaximum collar error   {maxError*1000:F2} mm");
            if(inspectedRod<0) return;
            plant.Strain(inspectedRod,Sheet.positions,out float strain,out float bend,out float twist);
            GUILayout.Label($"{PlantGeometry.Part(Geometry.rods[inspectedRod].part)} · rod {inspectedRod}\nStretch {strain:P2}    Bend {bend:F1}°\nTwist {twist:F1}°    Pick t {rodFraction:F2}");
        }
        void DrawRodOverlay()
        {
            var plant=Sheet.plant;
            for(int i=0;i<Geometry.rods.Count;i++)
            {
                var rod=Geometry.rods[i];
                Vector3 a=Sheet.positions[rod.a],b=Sheet.positions[rod.b],center=(a+b)*.5f;
                if(centers || rodMap!=0)
                {
                    plant.Strain(i,Sheet.positions,out float stretch,out float bend,out float twist);
                    float value=rodMap==1?bend/30:rodMap==2?Mathf.Abs(twist)/30:Mathf.Abs(stretch)/.05f;
                    GL.Color(rodMap==0?Color.cyan:Color.Lerp(Color.blue,Color.red,Mathf.Clamp01(value))); Line(a,b);
                }
                if(directors)
                {
                    var q=plant.frames[i]; float scale=(b-a).magnitude*.32f*debugScale;
                    GL.Color(Color.red); Line(center,center+q*Vector3.right*scale);
                    GL.Color(Color.green); Line(center,center+q*Vector3.up*scale);
                    GL.Color(Color.blue); Line(center,center+q*Vector3.forward*scale);
                }
            }
            if(collarErrors)
                for(int i=0;i<plant.leafRod.Length;i++)
                {
                    var row=Geometry.bladeRows[i][1];
                    if(plant.leafRod[i]<0)continue;
                    for(int j=row.Length/2-1;j<=row.Length/2+1;j++)
                    {
                        if(Geometry.severedCollars.Contains(row[j]))continue;
                        Vector3 targetPoint=plant.CollarTarget(plant.leafRod[i],row[j],Sheet.positions);
                        GL.Color(Color.magenta); Cross(targetPoint,.003f*debugScale); Line(targetPoint,Sheet.positions[row[j]]);
                    }
                }
            if(interaction && inspectedRod>=0)
            {
                var r=Geometry.rods[inspectedRod]; var p=Vector3.Lerp(Sheet.positions[r.a],Sheet.positions[r.b],rodFraction);
                GL.Color(Color.yellow); Cross(p,.012f);
                if(Sheet.grabRod>=0) { GL.Color(Color.white); Line(p,Sheet.target); Cross(Sheet.target,.012f); }
            }
        }
    }
}
