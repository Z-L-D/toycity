// Sets up a region on a running server for the Godot shots run to picture (Beta 4, part 6: room to grow): a city
// buys out its computer-mayored neighbour, then annexes the land beside the two, ending as one city of 2 × 2 tiles.
//   dotnet run --project tools/ToyCity.Stage -- ws://localhost:5183/ws
using ToyCity.ClientModel;
using ToyCity.Protocol.V0;
using PAction = ToyCity.Protocol.V0.Action;

var uri = new Uri(args.Length > 0 ? args[0] : "ws://localhost:5180/ws");
using var client = new CityClient(uri, "stage");
RegionState? region = null;
string error = "";
client.Message += m =>
{
    if (m.BodyCase == ServerMessage.BodyOneofCase.Region) region = m.Region;
    if (m.BodyCase == ServerMessage.BodyOneofCase.Error) error = m.Error.Message;
};

void Wait(Func<bool> done, string what, int seconds = 120)
{
    var end = DateTime.UtcNow.AddSeconds(seconds);
    while (!done())
    {
        if (error.Length > 0) throw new Exception($"{what}: {error}");
        if (DateTime.UtcNow > end) throw new TimeoutException($"waiting for {what}");
        client.Poll();
        Thread.Sleep(20);
    }
}

void Ask() { region = null; client.Connection.Send(new ClientMessage { Region = new RegionRequest() }); Wait(() => region != null, "the region"); }

Wait(() => client.City.Loaded, "the city");
client.Connection.Send(new ClientMessage { NewRegion = new NewRegionRequest { Name = "Halden", Seed = 21, Cols = 3, Rows = 3, TileSize = 128, StarterTown = true, CityName = "Halden" } });
Wait(() => client.City.Stats?.CityName == "Halden", "Halden", 300);
client.Connection.Send(new ClientMessage { FoundCity = new FoundCity { Col = 2, Row = 1, Name = "Ostby", StarterTown = true, Computer = "careful" } });
Ask();
Wait(() => { if (region!.Tiles.Any(t => t.Col == 2 && t.Row == 1 && t.Founded)) return true; Ask(); return false; }, "Ostby");
client.Connection.Act(new PAction { Loan = new TakeLoan { Amount = 100_000 } });
Thread.Sleep(1000);
client.Poll();

// Buy Ostby at its mayor's price.
Ask();
var option = region!.TakeoverOptions.Single(o => o.Col == 2 && o.Row == 1);
Console.WriteLine($"STAGE Ostby's price: ${option.Floor:N0}");
client.Connection.Send(new ClientMessage { Takeover = new TakeoverRequest { Op = TakeoverRequest.Types.Op.Offer, Col = 2, Row = 1, Price = option.Floor } });
Wait(() => client.City.Width == 256, "the takeover");
Console.WriteLine("STAGE Halden took in Ostby: 2 x 1 tiles");

// Then the land north of both: a corner tile takes the one between, so the city stays a rectangle.
Ask();
var annex = region!.Annex.First(a => a.Col == 2 && a.Row == 0);
Console.WriteLine($"STAGE annexing to {annex.ToCols} x {annex.ToRows} for ${annex.Price:N0}: {(annex.Why.Length > 0 ? annex.Why : "ok")}");
client.Connection.Send(new ClientMessage { Annex = new AnnexRequest { Col = 2, Row = 0 } });
Wait(() => client.City.Height == 256, "the annex");
Console.WriteLine($"STAGE OK Halden is {client.City.Width / 128} x {client.City.Height / 128} tiles");
