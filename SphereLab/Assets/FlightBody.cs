using System;
using UnityEngine;

[Serializable]
public struct FlightBody
{
    [Min(.01f)] public float radius, speed, acceleration;
    public static FlightBody Default => new() { radius = .48f, speed = 16, acceleration = 64 };
    public float ContactDistance => radius + .32f;
    public bool Touches(Vector3 center, Vector3 eye) =>
        Vector3.Distance(center, eye - Vector3.up * .45f) < ContactDistance;

    public Vector3 BrakingOffset(Vector3 velocity, float step)
    {
        float speedNow = velocity.magnitude;
        if (speedNow <= acceleration * step) return velocity * (step * .5f);
        // Whole deceleration steps followed by one step ending at zero speed.
        float fullTime = Mathf.Floor(speedNow / (acceleration * step)) * step;
        float remaining = Mathf.Max(0, speedNow - acceleration * fullTime);
        float distance = (speedNow + remaining) * fullTime * .5f + remaining * step * .5f;
        return velocity.normalized * distance;
    }

    public void Validate()
    {
        if (!(radius > 0 && speed > 0 && acceleration > 0) ||
            float.IsInfinity(radius) || float.IsInfinity(speed) || float.IsInfinity(acceleration))
            throw new ArgumentException("Enemy radius, speed and acceleration must be positive finite values.");
    }
}
