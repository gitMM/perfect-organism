using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Foliage
{
    public partial class FoliageLab
    {
        // The common interaction panel is retained so Unity only rebuilds text and
        // geometry when a value changes. Inspect and the full solver controls share
        // the existing lab panel with all other presets.
        GameObject branchPanelObject;
        PanelSettings branchPanelSettings;
        Label branchStats,branchContactStats,branchHint;
        Button branchPreset,groupPreset,hundredPreset,pauseButton,grabButton,cutButton,selfButton,datButton,debugButton;
        readonly Button[] shapeButtons=new Button[obstacleNames.Length];
        Button penaltyButton,ogcButton,orbitButton;
        Label obstacleStats;
        VisualElement contactModels;
        float nextPanelUpdate;
        bool CompactBranchPanel=>Sheet?.plant!=null && settings.forked &&
            sheetTab==0 && !expandBranchSettings;

        static void Highlight(Button button,bool selected)
        {
            button.style.backgroundColor=selected?new Color(.20f,.36f,.30f):new Color(.22f,.24f,.25f);
        }

        void RefreshBranchPanel()
        {
            if(!CompactBranchPanel)
            {
                if(branchPanelObject && branchPanelObject.activeSelf)branchPanelObject.SetActive(false);
                return;
            }
            if(!branchPanelObject)
            {
                branchPanelObject=new GameObject("Branch controls");
                branchPanelObject.transform.SetParent(transform,false);
                branchPanelSettings=ScriptableObject.CreateInstance<PanelSettings>();
                branchPanelSettings.themeStyleSheet=Resources.Load<ThemeStyleSheet>("BranchPanelTheme");
                branchPanelSettings.scaleMode=PanelScaleMode.ConstantPixelSize;
                branchPanelObject.AddComponent<UIDocument>().panelSettings=branchPanelSettings;
                BuildBranchPanel();
            }
            else if(!branchPanelObject.activeSelf)
            {
                branchPanelObject.SetActive(true);
                BuildBranchPanel();
            }
            if(branchPanelSettings.scale!=UiScale)branchPanelSettings.scale=UiScale;
            if(Time.unscaledTime<nextPanelUpdate)return;
            nextPanelUpdate=Time.unscaledTime+.15f;
            branchStats.text=$"{Sheet.Count} nodes   /   {Sheet.triangles.Length/3} faces\nTick {Sheet.Steps}    |    1/60 s";
            branchStats.text+=$"\n{Geometry.bladeRows.Count:N0} leaves";
            if(settings.adaptivePhysics)branchStats.text+=$" · {settings.PlantCount-Geometry.coarsePlants} higher-detail plants";
            branchContactStats.text=$"Branch contacts   {Sheet.selfContact.branchContacts}    Blade contacts   {Sheet.selfContact.bladeContacts}";
            if(settings.PlantCount>1)branchContactStats.text+=$"\nBetween plants   {Sheet.selfContact.betweenPlantContacts}";
            branchHint.text=Sheet.badVertex>=0?$"Non-finite solver value at node {Sheet.badVertex}":
                cutMessage??(cutTool?"Click a stem to cut. Drag across a leaf to slice it.":"Pull an outer fork toward its middle branch. Scroll while holding to change depth.");
            var contact=Sheet.contact;
            for(int i=0;i<shapeButtons.Length;i++)Highlight(shapeButtons[i],(ObstacleShape)i==contact.shape);
            bool obstacle=contact.shape!=ObstacleShape.Off;
            contactModels.style.display=obstacle?DisplayStyle.Flex:DisplayStyle.None;
            obstacleStats.style.display=obstacle?DisplayStyle.Flex:DisplayStyle.None;
            if(obstacle)
            {
                Highlight(penaltyButton,contact.model==ContactModel.Penalty);
                Highlight(ogcButton,contact.model==ContactModel.OGC);
                obstacleStats.text=$"Contacts   {contact.rods} rods   {contact.faces} faces\nPenetration   {1000*contact.physicalPenetration:F2} mm";
                if(contact.shape==ObstacleShape.Gate)obstacleStats.text+=$"\nGate opening   {1000*contact.gapWidth:F1} mm";
                if(contact.model==ContactModel.OGC)obstacleStats.text+=$"\nMinimum gap   {1000*contact.minClearance:F2} mm";
            }
            debugButton.text=debugRendering?"Debug rendering: on":"Debug rendering: off";
            Highlight(debugButton,debugRendering);
            orbitButton.text=Sheet.contact.playerOrbit?"Stop player capsule":"Player capsule orbit";
            Highlight(orbitButton,Sheet.contact.playerOrbit);
            Highlight(hundredPreset,settings.PlantCount==100);Highlight(branchPreset,settings.PlantCount==1);Highlight(groupPreset,settings.tenPlants);
            pauseButton.text=paused?"Resume":"Pause";
            Highlight(grabButton,!cutTool);Highlight(cutButton,cutTool);
            selfButton.text=Sheet.selfContact.enabled?"Plant self-collision: on":"Plant self-collision: off";
            datButton.text=Sheet.selfContact.dat?"DAT safeguards: on":"DAT safeguards: off";
            Highlight(selfButton,Sheet.selfContact.enabled);
            Highlight(datButton,Sheet.selfContact.dat);
            datButton.style.display=Sheet.selfContact.enabled?DisplayStyle.Flex:DisplayStyle.None;
        }

        void BuildBranchPanel()
        {
            nextPanelUpdate=0;
            var root=branchPanelObject.GetComponent<UIDocument>().rootVisualElement;
            root.Clear();
            root.style.position=Position.Absolute;
            root.style.top=0;root.style.left=0;root.style.bottom=0;root.style.right=0;
            root.pickingMode=PickingMode.Ignore;
            var panel=new ScrollView(ScrollViewMode.Vertical);
            panel.style.width=PanelWidth; panel.style.height=Length.Percent(100);
            panel.style.backgroundColor=new Color(.10f,.12f,.13f);
            panel.style.paddingLeft=8;panel.style.paddingRight=8;panel.style.paddingTop=8;
            panel.style.color=Color.white;panel.style.fontSize=13;
            root.Add(panel);
            VisualElement Row()
            {
                var row=new VisualElement();
                row.style.flexDirection=FlexDirection.Row;
                panel.Add(row);return row;
            }
            Label Text(string value)
            {
                var label=new Label(value);
                label.style.whiteSpace=WhiteSpace.Normal;
                label.style.marginTop=5;label.style.marginBottom=4;
                panel.Add(label);return label;
            }
            Button Control(VisualElement parent,string title,Action action,bool selected=false)
            {
                var button=new Button(()=>{action();nextPanelUpdate=0;}){text=title};
                button.style.flexGrow=1;button.style.flexBasis=0;button.style.minHeight=23;
                button.style.marginRight=1;button.style.marginBottom=2;
                button.style.color=Color.white;button.style.unityTextAlign=TextAnchor.MiddleCenter;
                Highlight(button,selected);parent.Add(button);return button;
            }
            Text("FOLIAGE  /  TEST SCENE");
            var modes=Row();
            Control(modes,"Plant",()=>SetSheetMode(false));
            Control(modes,"Sheet",()=>SetSheetMode(true));
            Control(modes,"Leaf",()=>OpenLeaf(leafIndex));
            Control(modes,"Stems",()=>{},true);
            debugButton=Control(Row(),"Debug rendering",()=>SetDebugRendering(!debugRendering),debugRendering);
            Control(Row(),TenCapsuleTest,()=>OpenCapsulePreset(10));
            hundredPreset=Control(Row(),HundredCapsuleTest,()=>OpenCapsulePreset(100));
            var tabsRow=Row();
            Control(tabsRow,"Solver",()=>{},true);Control(tabsRow,"Inspect",()=>sheetTab=1);
            branchStats=Text("");
            var clock=Row();
            pauseButton=Control(clock,"Pause",()=>paused=!paused);
            Control(clock,"Step",()=>{paused=true;Advance();});
            Control(clock,"Reset",ResetState);
            var presets=Row();
            Control(presets,"Single stem",()=>SetBranchTest(false));branchPreset=Control(presets,"Branch test",()=>SetBranchTest(true),!settings.tenPlants);
            groupPreset=Control(presets,"10 plants",()=>SetBranchTest(true,true),settings.tenPlants);
            branchContactStats=Text("");
            var interactionRow=Row();
            grabButton=Control(interactionRow,"Grab",()=>cutTool=false);
            cutButton=Control(interactionRow,"Cut",()=>cutTool=true);
            branchHint=Text("");
            selfButton=Control(Row(),"Plant self-collision",()=>Sheet.selfContact.enabled=!Sheet.selfContact.enabled);
            datButton=Control(Row(),"DAT displacement safeguards",()=>Sheet.selfContact.dat=!Sheet.selfContact.dat);
            Text("Test collider");
            orbitButton=Control(Row(),"Player capsule orbit",()=>
            {
                if(Sheet.contact.playerOrbit)SelectLabObstacle(ObstacleShape.Off);
                else StartPlayerCapsuleTest(settings.PlantCount==100?100:10);
            });
            for(int i=0;i<obstacleNames.Length;i+=2)
            {
                var row=Row();
                for(int j=i;j<Mathf.Min(i+2,obstacleNames.Length);j++)
                {
                    var shape=(ObstacleShape)j;
                    shapeButtons[j]=Control(row,obstacleNames[j],()=>{if(shape!=Sheet.contact.shape)SelectLabObstacle(shape);});
                }
            }
            contactModels=Row();
            penaltyButton=Control(contactModels,"Penalty",()=>Sheet.contact.model=ContactModel.Penalty);
            ogcButton=Control(contactModels,"OGC",()=>Sheet.contact.model=ContactModel.OGC);
            obstacleStats=Text("");
            Control(Row(),"Show solver settings",()=>expandBranchSettings=true);
        }
    }
}
