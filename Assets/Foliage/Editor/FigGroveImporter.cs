using System;
using System.IO;
using System.Globalization;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Foliage.Editor
{
    [ScriptedImporter(1,"figgrove")]
    public sealed class FigGroveImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            foreach(string source in new[]{"FigGroveLayout","JungleFig","FigShoot","PlantGeometry","BladeSpawnClearance"})
                context.DependsOnSourceAsset("Assets/Foliage/"+source+".cs");
            var seeds=File.ReadAllText(context.assetPath).Split((char[])null,StringSplitOptions.RemoveEmptyEntries);
            if(seeds.Length!=FigStressGrove.Count)throw new InvalidDataException("Expected twenty grove seeds.");
            var layout=ScriptableObject.CreateInstance<FigGroveLayout>();
            layout.trees=new FigGroveLayout.Tree[seeds.Length];
            context.AddObjectToAsset("layout",layout);
            context.SetMainObject(layout);
            for(int i=0;i<seeds.Length;i++){
                var tree=FigGroveLayout.BakeTree(i,int.Parse(seeds[i],CultureInfo.InvariantCulture));
                layout.trees[i]=tree;
                context.AddObjectToAsset($"tree-{i}",tree.wood);
                for(int b=0;b<tree.boughs.Length;b++){
                    var mesh=tree.boughs[b].mesh;
                    mesh.name=$"{tree.name} bough {b+1}";
                    context.AddObjectToAsset($"tree-{i}-bough-{b}",mesh);
                }
            }
        }
    }
}
