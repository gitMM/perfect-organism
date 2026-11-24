using UnityEngine;

[CreateAssetMenu(menuName = "Sphere Lab/Arena")]
public class ArenaDefinition : ScriptableObject
{
    [Tooltip("Optional geometry prefab. Leave empty to use geometry authored in the scene.")]
    public GameObject geometry;
    public Vector3 playerEye = new(0, 1.51f, -14);
    public float playerYaw;
    public Vector3 enemySpawn = new(6, 3.75f, 6);
    public FlightGrid navigation = FlightGrid.Hangar;

    public virtual void BuildGeometry()
    {
        if (geometry) Instantiate(geometry);
    }
}