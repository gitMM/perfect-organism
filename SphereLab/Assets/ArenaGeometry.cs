using UnityEngine;

public static class ArenaGeometry
{
    static Material material;
    public static GameObject Block(string name,Vector3 p,Vector3 size,Color color,bool solid=true)
    {
        if(!material)material=Resources.Load<Material>("HangarSurface");
        var g=GameObject.CreatePrimitive(PrimitiveType.Cube);
        g.name=name;g.transform.position=p;g.transform.localScale=size;
        var renderer=g.GetComponent<Renderer>();renderer.sharedMaterial=material;
        var props=new MaterialPropertyBlock();props.SetColor("_Color",color);renderer.SetPropertyBlock(props);
        g.layer=solid?8:2;g.GetComponent<Collider>().enabled=solid;return g;
    }
}