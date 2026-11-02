using Google.Protobuf;
using ToyCity.Contract;
using ToyCity.Emergency;
using EW = ToyCity.Emergency.Wire;

namespace ToyCity.Sim.Tests;

/// <summary>Beta 3.2, C1 to C5: a calendar and a day clock, the calendar's pace set by the mayor, life-paced play on the clock.</summary>
public class ClockTests
{
    [Fact]
    public void AtAMonthADayEveryCalendarDayIsATurnOfTheClock()
    {
        var w = World.Create(Fixture.Content, 3, 64, "Clocks");
        Assert.Equal(World.DefaultPace, w.Pace);
        Assert.Equal("Mon 08:00 · January, year 1", w.ClockText);
        for (int k = 0; k < 40; k++)
        {
            long turn = w.Turn;
            w.Step();
            Assert.Equal(turn + 1, w.Turn);
            Assert.Equal(w.Tick, w.Turn);
        }
        // Forty steps of 48 minutes: 32 hours on, Tuesday at 16:00, and the calendar on its 41st day.
        Assert.Equal(8 * 60 + 40 * 48, w.ClockMinute);
        Assert.Equal("Tue 16:00 · February, year 1", w.ClockText);
    }

    [Fact]
    public void AFasterCalendarRunsManyDaysATurnAndTheClockKeepsItsHours()
    {
        var w = World.Create(Fixture.Content, 3, 64, "Clocks");
        w.Enqueue(new SetPaceAction(360));
        int turns = 0;
        for (int k = 0; k < 360; k++)
        {
            long before = w.Turn;
            w.Step();
            if (w.Turn != before) turns++;
        }
        // A year of calendar is one clock day: 30 turns of 48 minutes, each twelve calendar days long.
        Assert.Equal(360, w.Pace);
        Assert.Equal(4, w.ClockMinutesPerTick);
        Assert.Equal(30, turns);
        Assert.Equal(8 * 60 + 24 * 60, w.ClockMinute);
        Assert.StartsWith("Tue 08:00", w.ClockText);

        // A pace that isn't on the list is refused; players can't set the region's pace on one city.
        Assert.NotNull(w.OutOfBounds(new SetPaceAction(100)));
        Assert.NotNull(w.Forbidden(new SetPaceAction(30) { By = "someone" }));
    }

    [Fact]
    public void TheCitysOwnCallsComeAtOneRateAnHourWhateverTheCalendarsPace()
    {
        // The same city, two clock days at a month a day and at a year a day: the same turns, so about the same calls.
        int Calls(int pace)
        {
            SideGameCatalog.Register(EmergencyGame.Id, () => new EmergencyGame());
            var w = Fixture.Starter(715);
            w.Run(60);
            w.Enqueue(new AttachSideGameAction(EmergencyGame.Id));
            w.Enqueue(new SetPaceAction(pace));
            w.Step();
            long until = w.ClockMinute + 2 * World.MinutesPerClockDay;
            var seen = new HashSet<long>(w.RecentIncidents.Select(i => i.Id));
            int calls = 0;
            while (w.ClockMinute < until)
            {
                w.Step();
                foreach (var i in w.RecentIncidents)
                    if (seen.Add(i.Id) && i.Source == "city" && i.Kind is "crime" or "injury") calls++;
            }
            return calls;
        }
        int slow = Calls(30), fast = Calls(360);
        Assert.True(slow > 0 && fast > 0, $"{slow} calls at a month a day, {fast} at a year a day");
        Console.WriteLine($"CLOCK calls: {slow} at a month a day, {fast} at a year a day");
        // A year a day runs twelve times the calendar in the same clock time, yet no more calls come.
        Assert.True(fast <= slow * 3 / 2 + 2, $"{fast} calls at a year a day, {slow} at a month a day");
    }

    [Fact]
    public void AnUnansweredCallIsLostAfterHoursOfTheClockNotDaysOfTheCalendar()
    {
        using var w = EmergencyTests.Town(713, player: "player");
        w.Enqueue(new SideGameAction(EmergencyGame.Id, "operator", new EW.Command { Settings = new EW.Settings { Style = EW.CommandStyle.FullCommand, CrewsUseHeads = true, DriversWatchOut = true } }.ToByteArray()) { By = "player" });
        w.Enqueue(new SetPaceAction(360));
        w.Step();
        var home = w.Buildings.Where(b => b != null && b.Residents > 0).OrderBy(b => b!.Id).Skip(3).First()!;
        w.RaiseIncident("injury", home.X, home.Y, "city", "a fall", 1, "");
        w.Step();
        Assert.Equal("waiting", EmergencyTests.View(w).Calls.Single().Status);
        // Three days of calendar is nothing on the clock at this pace: the call still waits.
        w.Run(3);
        Assert.Equal("waiting", EmergencyTests.View(w).Calls.Single().Status);
        // Three turns (2 h 24 min of the clock, 36 calendar days) and it's lost.
        long until = w.ClockMinute + 3 * World.TurnMinutes;
        while (w.ClockMinute <= until) w.Step();
        var call = EmergencyTests.View(w).Calls.Single();
        Assert.Equal("done", call.Status);
        Assert.Contains(w.Headlines, h => h.Text.Contains("unanswered for 2 h 24 min"));
    }

    [Fact]
    public void TheClocksAreSavedAndATileFoundedLaterReadsTheRegionsHour()
    {
        var r = Region.Create(Fixture.Content, "Hours", 9, 2, 1, 64);
        var west = r.Found(0, 0, "West", "Ana");
        west.Enqueue(new SetPaceAction(120));
        for (int k = 0; k < 7; k++) { r.BeginDay(); Region.StepCities(r.Cities.ToList()); r.EndDay(); }
        var east = r.Found(1, 0, "East", "Bo");
        Assert.Equal((west.ClockMinute, west.Pace), (east.ClockMinute, east.Pace));
        for (int k = 0; k < 5; k++) { r.BeginDay(); Region.StepCities(r.Cities.ToList()); r.EndDay(); }
        Assert.Equal(west.ClockMinute, east.ClockMinute);
        // A replay from its founding starts on the same hour.
        var again = r.ReplayTile(r.TileAt(1, 0)!, east.Tick);
        Assert.Equal(east.ClockMinute, again.ClockMinute);

        var back = World.FromSnapshot(Fixture.Content, east.Snapshot());
        Assert.Equal((east.ClockMinute, east.Pace, east.FoundedClocks), (back.ClockMinute, back.Pace, back.FoundedClocks));
    }
}
