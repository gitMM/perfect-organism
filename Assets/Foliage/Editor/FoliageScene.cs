using System;
using Foliage;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class FoliageScene
{
    public const string Path="Assets/Foliage/FoliageLab.unity";

    [MenuItem("Tools/Foliage/Open test scene")]
    public static void Open()
    {
        if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        if(AssetDatabase.LoadAssetAtPath<SceneAsset>(Path)!=null)
        {
            EditorSceneManager.OpenScene(Path);
            return;
        }
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        SceneManager.SetActiveScene(scene);
        var lab=new GameObject("Foliage lab").AddComponent<FoliageLab>();
        var camera=new GameObject("Lab camera").AddComponent<Camera>();
        camera.tag="MainCamera";
        camera.clearFlags=CameraClearFlags.SolidColor;
        camera.backgroundColor=new Color(.095f,.125f,.13f);
        camera.nearClipPlane=.025f;
        camera.fieldOfView=37;
        camera.transform.position=new Vector3(0,1.15f,-4.4f);
        lab.view=camera;
        var sphere=GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name="Movable test sphere";
        sphere.transform.position=new Vector3(.85f,.38f,-.25f);
        sphere.transform.localScale=Vector3.one*.4f;
        sphere.SetActive(false);
        lab.testSphere=sphere.transform;
        if(!EditorSceneManager.SaveScene(scene,Path)) throw new Exception("Could not save foliage scene");
    }

    [MenuItem("Tools/Foliage/Check generated topology")]
    public static void CheckGeometry()
    {
        foreach(int seed in new[]{0,17,-1,int.MaxValue})
        foreach(int resolution in new[]{4,8,20})
        foreach(int leaves in new[]{1,7,16})
        {
            var s=PlantSettings.Default;
            s.seed=seed; s.resolution=resolution; s.leaves=leaves;
            var g=PlantGeometry.Generate(s);
            if(g.issues.Count!=0 || g.joints.Count!=2*leaves)
                throw new Exception($"Topology failed for {seed}/{resolution}/{leaves}: {string.Join(", ",g.issues)}");
            foreach(var f in g.faces)
                if(Vector3.Cross(g.nodes[f.b]-g.nodes[f.a],g.nodes[f.c]-g.nodes[f.a]).y<=0)
                    throw new Exception($"Reversed leaf face in {PlantGeometry.Part(f.part)}");
            var repeat=PlantGeometry.Generate(s);
            for(int i=0;i<g.nodes.Count;i++)
                if(!g.nodes[i].Equals(repeat.nodes[i])) throw new Exception($"Seed mismatch at node {i}");
        }
        Debug.Log("Foliage: 36 deterministic geometry cases passed.");
    }

    [MenuItem("Tools/Foliage/Check picking in Play mode")]
    public static void CheckPicking()
    {
        var lab=UnityEngine.Object.FindFirstObjectByType<FoliageLab>();
        if(!Application.isPlaying || lab==null) throw new InvalidOperationException("Open the foliage lab in Play mode");
        var g=lab.Geometry;
        for(int i=0;i<g.faces.Count;i++)
        {
            var f=g.faces[i];
            var a=g.nodes[f.a]; var b=g.nodes[f.b]; var c=g.nodes[f.c];
            Vector3 p=a*.2f+b*.3f+c*.5f;
            Vector3 n=Vector3.Cross(b-a,c-a).normalized;
            foreach(float sign in new[]{-1f,1f})
            {
                var ray=new Ray(p+n*(.0001f*sign),-sign*n);
                var hit=lab.Pick(ray,PickMode.Element);
                if(!hit.valid || hit.face!=i || Vector3.Distance(hit.Point(g),p)>.0001f)
                    throw new Exception($"Face {i} pick failed");
            }
        }
        for(int i=0;i<g.rods.Count;i++)
        {
            var r=g.rods[i];
            Vector3 a=g.nodes[r.a],b=g.nodes[r.b], p=Vector3.Lerp(a,b,.38f);
            Vector3 side=Vector3.Cross(b-a,Vector3.forward).normalized;
            var hit=lab.Pick(new Ray(p+side*r.radius*2,-side),PickMode.Element);
            if(!hit.valid || hit.rod!=i || Mathf.Abs(hit.along-.38f)>.001f)
                throw new Exception($"Rod {i} pick failed");
        }
        Debug.Log($"Foliage: verified {g.faces.Count} two-sided face attachments and {g.rods.Count} rod attachments.");
    }
}
