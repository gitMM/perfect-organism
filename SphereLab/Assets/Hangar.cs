using UnityEngine;

public static class Hangar
{
    static readonly Color concrete=new(.36f,.39f,.4f), steel=new(.19f,.22f,.24f), amber=new(.95f,.53f,.13f);
    static void UpperCover()
    {
        foreach(int side in new[]{-1,1}){
            float z=side*6;
            float height=side<0?5.4f:7.1f;
            // A shelf and short outer screen leave the wall route open at both ends.
            ArenaGeometry.Block("Wall service shelf",new(side*18.9f,height,z),new(3.2f,.5f,8),steel);
            ArenaGeometry.Block("Service screen",new(side*17.55f,height+1,z),new(.5f,1.6f,4),concrete);

            // The hangers join the roof to the casing. The space above it is crawlable.
            float x=side*8;
            ArenaGeometry.Block("Overhead air handler",new(x,8.55f,z),new(6,.8f,5),steel);
            foreach(int end in new[]{-1,1})
                ArenaGeometry.Block("Duct hanger",new(x+end*2.5f,9.975f,z),new(.7f,2.05f,1.2f),concrete);
        }
    }

    public static void Make(bool loadingPlatforms=true)
    {
        ArenaGeometry.Block("Slab",new(0,-.5f,0),new(42,1,36),concrete);
        foreach(int side in new[]{-1,1})
        {
            ArenaGeometry.Block("End wall",new(0,5.5f,side*18),new(42,11,1),concrete);
            ArenaGeometry.Block("Side wall",new(side*21,5.5f,0),new(1,11,36),concrete);
            for(int z=-15;z<=15;z+=6)
                ArenaGeometry.Block("Wall rib",new(side*20.3f,5.5f,z),new(.45f,11,.6f),steel);
            for(int z=-9;z<=9;z+=9)
            {
                if(loadingPlatforms && z==0){
                    // A walk-through frame under each end of the transfer bridge.
                    foreach(int foot in new[]{-1,1})
                        ArenaGeometry.Block("Gantry pier",new(side*6+foot*1.85f,1.75f,0),new(1.05f,3.5f,2.4f),concrete);
                    ArenaGeometry.Block("Gantry header",new(side*6,3.3f,0),new(4.75f,.4f,2.4f),steel);
                    ArenaGeometry.Block("Upper mast",new(side*7.85f,6.9f,0),new(1.05f,5.8f,1.6f),steel);
                    continue;
                }
                if(loadingPlatforms && z==9){
                    ArenaGeometry.Block("Service pier",new(side*6,4.8f,z),new(1.6f,9.6f,2.5f),steel);
                    ArenaGeometry.Block("Service bay screen",new(side*4.55f,1.05f,z+.9f),new(2.1f,2.1f,.7f),concrete);
                    continue;
                }
                ArenaGeometry.Block("Column",new(side*6,4.8f,z),new(2.1f,9.6f,2.1f),steel);
                ArenaGeometry.Block("Column marking",new(side*6,1.8f,z),new(2.13f,.18f,2.13f),amber,false);
            }
            float h=side<0?2:3;
            ArenaGeometry.Block("Terrace",new(side*15,h/2,3),new(8,h,13),concrete);
            for(int i=0;i<6;i++)
            {
                float top=h*(i+1)/7;
                ArenaGeometry.Block("Terrace step",new(side*15,top/2,(loadingPlatforms?-9f:-9.5f)+i),new(4,top,1),concrete);
            }
        }
        foreach(var p in new[]{new Vector3(-3,0,-5),new Vector3(8,0,-13),new Vector3(2,0,8),new Vector3(-10,0,12),new Vector3(14,3,4)})
        {
            ArenaGeometry.Block("Cargo",p+Vector3.up*1.15f,new(3,2.3f,2.7f),new(.33f,.32f,.29f));
            foreach(float x in new[]{-.95f,.95f})ArenaGeometry.Block("Binding",p+new Vector3(x,1.15f,0),new(.13f,2.33f,2.73f),steel,false);
        }
        if(loadingPlatforms){
            UpperCover();
            ArenaGeometry.Block("West terrace pallet",new(-12.5f,2.25f,4),new(3,.5f,2),steel);
            ArenaGeometry.Block("Transfer bridge",new(0,3.75f,0),new(24,.5f,3),steel);
            ArenaGeometry.Block("Rear loading dock",new(9,1.6f,15),new(7,3.2f,5),concrete);
            for(int i=0;i<5;i++){
                float h=(i+1)*.54f;
                ArenaGeometry.Block("Dock stair",new(16.1f-i*.8f,h/2,15),new(.8f,h,2),concrete);
            }
            ArenaGeometry.Block("West loading shelf",new(-17.4f,2.75f,-11),new(6.8f,.5f,3.2f),steel);
        }
        ArenaGeometry.Block("Cover alcove",new(-1,1.6f,13),new(7,3.2f,.7f),steel);
        ArenaGeometry.Block("Alcove return",new(-4.25f,1.6f,14.5f),new(.5f,3.2f,3),steel);
        for(int z=-15;z<=15;z+=6)
        {
            ArenaGeometry.Block("Roof truss",new(0,10.6f,z),new(42,.45f,.5f),steel);
            ArenaGeometry.Block("Strip light",new(0,10.34f,z),new(14,.09f,.26f),Color.white,false);
        }
        ArenaGeometry.Block("Roof",new(0,11.5f,0),new(42,1,36),concrete);
        for(int z=-15;z<16;z+=3)ArenaGeometry.Block("Floor marking",new(0,.012f,z),new(.1f,.015f,1.3f),amber,false);
        RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight=new(.78f,.8f,.82f);
        var light=new GameObject("Directional fill").AddComponent<Light>();
        light.type=LightType.Directional;light.intensity=.9f;light.transform.rotation=Quaternion.Euler(48,-28,0);
        Physics.SyncTransforms();
    }
}