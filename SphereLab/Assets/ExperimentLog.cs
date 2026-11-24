using System;
using System.IO;
using UnityEngine;

public sealed class ExperimentLog : IDisposable
{
    [Serializable] sealed class Session
    {
        public string kind = "session", arena, enemy, behavior;
        public int version = 8, budget;
        public float step = FlightPlanner.Step, horizon;
        public FlightBody body;
    }

    StreamWriter writer;
    public string Location { get; private set; }

    public void Open(string arena, FlightBody body)
    {
        try
        {
            string directory = Path.Combine(Application.persistentDataPath, "Decisions");
            Directory.CreateDirectory(directory);
            string filename = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" +
                Guid.NewGuid().ToString("N").Substring(0, 8) + ".jsonl";
            Location = Path.Combine(directory, filename);
            writer = new StreamWriter(new FileStream(Location, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            Write(JsonUtility.ToJson(new Session { arena=arena, enemy="Sphere", behavior="Survival", body=body,
                horizon=FlightPlanner.Depth * FlightPlanner.Step,
                budget=FlightPlanner.Budget }));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Unavailable(e); }
    }

    public void Write(string record)
    {
        try { writer?.WriteLine(record); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Unavailable(e); }
    }

    public void Flush()
    {
        try { writer?.Flush(); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Unavailable(e); }
    }

    void Unavailable(Exception error)
    {
        Location = "Logging unavailable — " + error.Message;
        Debug.LogWarning(Location);
        Dispose();
    }

    public void Dispose()
    {
        var closing = writer;
        writer = null;
        try { closing?.Dispose(); }
        catch (IOException e) { Debug.LogWarning(e.Message); }
    }
}