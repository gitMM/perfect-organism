using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Foliage.Editor
{
    [ScriptedImporter(2,"interactivefig")]
    public sealed class InteractiveFigImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context){
            foreach(string source in new[]{"InteractiveFigLayout","JungleFig","FigShoot","PlantGeometry","BladeSpawnClearance","VbdSheet","RodPlant","SolverSetup/VbdSetup"})
                context.DependsOnSourceAsset("Assets/Foliage/"+source+".cs");
            var layout=ScriptableObject.CreateInstance<InteractiveFigLayout>();
            layout.seed=int.Parse(File.ReadAllText(context.assetPath),CultureInfo.InvariantCulture);
            layout.settings=new FigSettings();
            var shoots=new List<FigShoot>();
            layout.wood=JungleFig.Generate(layout.seed,layout.settings,shoots);
            layout.boughs=new FigShoot.Baked[shoots.Count];
            context.AddObjectToAsset("layout",layout);
            context.SetMainObject(layout);
            context.AddObjectToAsset("wood",layout.wood);
            for(int i=0;i<shoots.Count;i++){
                var model=new FigShoot.Baked(shoots[i]);
                layout.boughs[i]=model;
                model.mesh.name=$"Fig {layout.seed} bough {i+1}";
                context.AddObjectToAsset($"bough-{i}",model.mesh);
            }
        }
    }
}
