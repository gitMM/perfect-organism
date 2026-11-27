using UnityEngine;

namespace Foliage
{
    public sealed class FigStressBough : MonoBehaviour
    {
        internal FigStressGrove grove;
        void OnCollisionEnter(Collision hit)=>Report(hit);
        void OnCollisionStay(Collision hit)=>Report(hit);
        void Report(Collision hit)=>grove.Record(gameObject.layer-8,hit.collider.gameObject.layer-8);
    }
}
