using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

/// <summary>
/// A connection plus the city mirror it keeps current: the part of a client that has nothing to do with drawing.
/// Panels listen for the replies they asked for through <see cref="Message"/>.
/// </summary>
public sealed class CityClient : IDisposable
{
    public readonly CityMirror City = new();
    public readonly Connection Connection;
    /// <summary>Every message, after the mirror has applied it.</summary>
    public event Action<ServerMessage>? Message;

    public CityClient(Uri server, string name = "toycity-godot", SignIn? credentials = null)
    {
        Connection = new Connection(server, name, credentials);
    }

    /// <summary>Applies whatever has arrived. Call once a frame.</summary>
    public void Poll() => Connection.Poll(m =>
    {
        switch (m.BodyCase)
        {
            case ServerMessage.BodyOneofCase.Full: City.ApplyFull(m.Full); break;
            case ServerMessage.BodyOneofCase.Delta: City.ApplyDelta(m.Delta); break;
            case ServerMessage.BodyOneofCase.Region: City.Borders = m.Region.Borders.ToList(); break;
        }
        Message?.Invoke(m);
    });

    public void Dispose() => Connection.Dispose();
}
