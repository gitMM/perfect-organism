using UnityEngine;

namespace Foliage
{
    public partial class FoliageLab
    {
        GameObject obstacleObject;
        Mesh obstacleMesh;
        bool showContacts=true,showCandidates,showObstacleBounds;
        static readonly string[] obstacleNames={"Off","Sphere","Capsule","Box","Narrow gap"};
        void ClearObstacle()
        {
            if(obstacleObject!=null){obstacleObject.SetActive(false);Destroy(obstacleObject);}
            if(obstacleMesh!=null)Destroy(obstacleMesh);
            obstacleObject=null;obstacleMesh=null;movingSphere=false;
        }
        public void SetObstacle(ObstacleShape shape,Vector3 start,Quaternion orientation,float radius=.15f,float opening=.06f,float capsuleHalfHeight=0)
        {
            FinishQueuedTick();
            EndDrag();ClearObstacle();
            var contact=Sheet.contact; contact.Configure(shape,start,orientation,radius,opening,capsuleHalfHeight);
            if(shape==ObstacleShape.Off)return;
            Sheet.ResolveObstacleSpawn();
            var type=shape==ObstacleShape.Box || shape==ObstacleShape.Gate?PrimitiveType.Cube:shape==ObstacleShape.Capsule?PrimitiveType.Capsule:PrimitiveType.Sphere;
            obstacleObject=GameObject.CreatePrimitive(type);
            obstacleObject.name="Foliage obstacle · "+shape;
            obstacleObject.transform.SetPositionAndRotation(contact.center,orientation);
            Vector3 size=2*contact.extents;
            if(shape==ObstacleShape.Capsule)
            {
                // Unity's primitive is two units high. Stretch only the cylinder to preserve hemispherical caps.
                obstacleObject.transform.localScale=Vector3.one*(2*radius);
            }
            else obstacleObject.transform.localScale=size;
            var filter=obstacleObject.GetComponent<MeshFilter>();
            obstacleMesh=Instantiate(filter.sharedMesh);filter.sharedMesh=obstacleMesh;
            if(shape==ObstacleShape.Capsule)
            {
                float offset=contact.extents.y/(2*radius)-1;
                var vertices=obstacleMesh.vertices;
                for(int i=0;i<vertices.Length;i++)vertices[i].y+=Mathf.Sign(vertices[i].y)*offset;
                obstacleMesh.vertices=vertices;obstacleMesh.RecalculateBounds();
                obstacleObject.GetComponent<CapsuleCollider>().height=2*(1+offset);
            }
            var colors=new Color[obstacleMesh.vertexCount];
            System.Array.Fill(colors,new Color(.78f,.42f,.22f));
            obstacleMesh.colors=colors;
            obstacleObject.GetComponent<MeshRenderer>().sharedMaterial=plantMaterial;
            if(shape==ObstacleShape.Gate)
            {
                var first=obstacleObject;
                obstacleObject=new GameObject("Foliage obstacle · Narrow gap");
                obstacleObject.transform.SetPositionAndRotation(contact.center,orientation);
                first.transform.SetParent(obstacleObject.transform,true);
                first.transform.localPosition=contact.WallOffset(0);
                var second=Instantiate(first,obstacleObject.transform);
                second.transform.localPosition=contact.WallOffset(1);
            }
            Physics.SyncTransforms();
        }
        public void StartPlayerCapsuleTest(int count=10)
        {
            SetBranchPreset(true,count);
            if(Sheet?.plant==null)OpenStems();
            var pivot=settings.OrbitPivot+Vector3.up*.9f;
            float radius=PlantContact.OrbitRadius(count);
            SetObstacle(ObstacleShape.Capsule,pivot+Vector3.right*radius,Quaternion.identity,.30f,capsuleHalfHeight:.90f);
            Sheet.contact.StartPlayerOrbit(pivot,radius);
            lastClockTime=Time.realtimeSinceStartupAsDouble;
            paused=false;
        }
        public bool BeginLabDrag(Ray ray)
        {
            if(obstacleObject==null || Sheet.contact.playerOrbit)return BeginSheetPick(ray);
            Physics.SyncTransforms();
            RaycastHit hit=default;float nearest=100;
            foreach(var collider in obstacleObject.GetComponentsInChildren<Collider>())
                if(collider.Raycast(ray,out var candidate,nearest)){hit=candidate;nearest=hit.distance;}
            if(hit.collider==null)return BeginSheetPick(ray);
            if(BeginPlantGrab(ray,hit.distance))return true;
            movingSphere=true;sphereOffset=Sheet.contact.center-hit.point;
            depth=view.WorldToScreenPoint(hit.point).z;return true;
        }
        public void MoveObstacleTarget(Vector2 screen,float wheel)
        {
            depth=Mathf.Clamp(depth*Mathf.Exp(wheel*.08f),.08f,40);
            Sheet.contact.target=view.ScreenToWorldPoint(new Vector3(screen.x,screen.y,depth))+sphereOffset;
        }
        void SelectLabObstacle(ObstacleShape shape)
        {
            SetObstacle(shape,new Vector3(-.48f,.56f,-.12f)*settings.height,
                shape==ObstacleShape.Sphere?Quaternion.identity:Quaternion.Euler(8,16,-15),settings.height*.075f);
        }
        void DrawObstaclePanel(bool inspect)
        {
            var contact=Sheet.contact;
            Sheet.selfContact.enabled=GUILayout.Toggle(Sheet.selfContact.enabled,"Plant self-collision");
            if(Sheet.selfContact.enabled)
                Sheet.selfContact.dat=GUILayout.Toggle(Sheet.selfContact.dat,"DAT displacement safeguards");
            if(inspect && Sheet.selfContact.safeguarding)
                GUILayout.Label($"Minimum motion bound {Sheet.minMotionBound*1000:F2} mm\nLargest rejected update {Sheet.peakRejectedMotion*1000:F2} mm");
            if(inspect && Sheet.selfContact.enabled)
                GUILayout.Label($"Self-contacts {Sheet.selfContact.active}   Penetration {Sheet.selfContact.penetration*1000:F2} mm");
            GUILayout.Label("Test collider");
            if(GUILayout.Button(contact.playerOrbit?"Stop player capsule":"Player capsule orbit"))
            {
                if(contact.playerOrbit)SelectLabObstacle(ObstacleShape.Off);
                else StartPlayerCapsuleTest(settings.PlantCount==100?100:10);
                return;
            }
            var shape=(ObstacleShape)GUILayout.SelectionGrid((int)contact.shape,obstacleNames,2,GUILayout.Width(PanelWidth-34));
            if(shape!=contact.shape)
                SelectLabObstacle(shape);
            if(shape==ObstacleShape.Off)return;
            if(shape==ObstacleShape.Gate)GUILayout.Label($"Gate opening   {contact.gapWidth*1000:F1} mm");
            contact.model=(ContactModel)GUILayout.SelectionGrid((int)contact.model,new[]{"Penalty","OGC"},2);
            GUILayout.Label($"Contacts   {contact.rods} rods   {contact.faces} faces\nDeepest penetration   {contact.physicalPenetration*1000:F2} mm");
            if(contact.model==ContactModel.OGC)
                GUILayout.Label($"Offset {PlantContact.Offset*1000:F0} mm   Minimum gap {contact.minClearance*1000:F2} mm");
            if(inspect)
            {
                if(contact.model==ContactModel.OGC && !Sheet.selfContact.safeguarding)
                    GUILayout.Label($"Smallest node bound {Sheet.minMotionBound*1000:F2} mm\nLargest rejected motion {Sheet.peakRejectedMotion*1000:F2} mm");
                showContacts=GUILayout.Toggle(showContacts,"Contact points and normals");
                showCandidates=GUILayout.Toggle(showCandidates,$"Broad-phase candidates ({contact.candidates})");
                showObstacleBounds=GUILayout.Toggle(showObstacleBounds,"Collider bounds");
                if(contact.deepest>=0)
                {
                    int id=contact.deepest%contact.features.Length; var hit=contact.hits[contact.deepest];
                    GUILayout.Label($"{(id<contact.rodCount?"Rod "+id:"Face "+(id-contact.rodCount))}   thickness {contact.features[id].skin*1000:F1} mm\nNormal {hit.normal:F2}");
                }
            }
            else GUILayout.Label("Drag the collider or the plant. Scroll while dragging to adjust depth.");
        }
        void DrawContactOverlay(bool batched)
        {
            var c=Sheet.contact;
            if(!batched && showContacts && Sheet.selfContact.enabled)
            {
                GL.Color(Color.magenta);
                foreach(var p in Sheet.selfContact.points)Cross(p,.006f*debugScale);
            }
            if(c.shape==ObstacleShape.Off)return;
            if(showObstacleBounds)
            {
                GL.Color(Color.yellow);
                for(int wall=0;wall<c.wallCount;wall++)
                for(int corner=0;corner<8;corner++)
                {
                    Vector3 local=new Vector3((corner&1)==0?-1:1,(corner&2)==0?-1:1,(corner&4)==0?-1:1);
                    Vector3 a=Vector3.Scale(local,c.extents);
                    for(int axis=0;axis<3;axis++)if((corner&(1<<axis))==0)
                    {
                        Vector3 b=a;b[axis]*=-1;
                        Vector3 center=c.center+c.rotation*c.WallOffset(wall);
                        Line(center+c.rotation*a,center+c.rotation*b);
                    }
                }
            }
            foreach(int i in c.candidateSlots)
            {
                var h=c.hits[i];
                var f=c.features[i%c.features.Length];
                if(showCandidates)
                {
                    GL.Color(Color.cyan);Line(Sheet.positions[f.a],Sheet.positions[f.b]);
                    if(f.c>=0){Line(Sheet.positions[f.b],Sheet.positions[f.c]);Line(Sheet.positions[f.c],Sheet.positions[f.a]);}
                }
                if(showContacts && h.depth>0)
                {
                    Vector3 point=h.witness-h.normal*f.skin;
                    GL.Color(Color.yellow); Cross(point,.006f*debugScale);
                    GL.Color(Color.red);Line(point,point+h.normal*h.depth);
                    GL.Color(Color.white);Line(point+h.normal*h.depth,point+h.normal*(h.depth+.07f*debugScale));
                }
            }
        }
    }
}
